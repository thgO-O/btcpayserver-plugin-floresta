using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.HostedServices;
using BTCPayServer.Logging;
using BTCPayServer.Plugins.Floresta.Data;
using BTCPayServer.Plugins.Floresta.Services;
using BTCPayServer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBXplorer;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests;

public class FlorestaListenerInitializationTests
{
    [Theory]
    [InlineData("ibd")]
    [InlineData("http-error")]
    [InlineData("invalid-json")]
    public async Task PendingWalletIsReconciledByTheNextInitializationWithoutAnotherEvent(string deferredReason)
    {
        await using var harness = new Harness();
        harness.BlockchainInfo = call => call == 2
            ? deferredReason switch
            {
                "http-error" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                "invalid-json" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not JSON") },
                _ => Harness.Info(true)
            }
            : Harness.Info(call == 1 && deferredReason == "ibd");
        await harness.AddCachedWalletAsync();

        // The final status refresh is ready, but the wallet itself was deferred.
        Assert.Null(await harness.InitializeAsync());
        Assert.True(harness.Monitor.IsNetworkCompatible, harness.Monitor.LastError);
        Assert.Equal(0, harness.HistoryRequests);
        Assert.False(await harness.CachedOutputIsSpentAsync());

        // No block, subscription notification, invoice or reconnect is sent.
        Assert.Equal(90, await harness.InitializeAsync());
        Assert.Equal(1, harness.HistoryRequests);
        Assert.True(await harness.CachedOutputIsSpentAsync());

        // Once reconciled, the same initialized generation stops doing work.
        Assert.Null(await harness.InitializeAsync());
        Assert.Equal(1, harness.HistoryRequests);
    }

    [Fact]
    public async Task NewAndFailedWalletsRetryDuringIbdWithoutPreparingSuccessfulWalletsAgain()
    {
        await using var harness = new Harness();
        harness.BlockchainInfo = _ => Harness.Info(true);
        Assert.Null(await harness.InitializeAsync());
        Assert.True(harness.Monitor.IsNetworkCompatible, harness.Monitor.LastError);

        var first = new ExtKey().Neuter().ToString(Network.RegTest);
        harness.FailNextDescriptorLoad = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Tracker.TrackWalletAsync(first, harness.Token));
        Assert.Null(await harness.InitializeAsync());
        Assert.Equal(2, harness.LoadedDescriptors.Count);
        var checks = harness.DescriptorChecks;

        Assert.Null(await harness.InitializeAsync());
        Assert.Equal(checks, harness.DescriptorChecks);

        var second = new ExtKey().Neuter().ToString(Network.RegTest);
        harness.FailNextDescriptorLoad = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Tracker.TrackWalletAsync(second, harness.Token));
        checks = harness.DescriptorChecks;
        Assert.Null(await harness.InitializeAsync());
        Assert.Equal(checks + 1, harness.DescriptorChecks);
        Assert.Equal(4, harness.LoadedDescriptors.Count);

        // A new Electrum generation must prepare both wallets again.
        checks = harness.DescriptorChecks;
        await harness.Electrum.DisconnectAsync();
        Assert.Null(await harness.InitializeAsync());
        Assert.Equal(checks + 2, harness.DescriptorChecks);
    }

    [Fact]
    public async Task PreparationSettingsChangedDuringIbdTakeEffectWithoutReconnecting()
    {
        await using var harness = new Harness();
        harness.BlockchainInfo = _ => Harness.Info(true);
        harness.UpdateSettings(settings => settings.AutoRegisterDescriptors = false);
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        await harness.Tracker.TrackWalletAsync(wallet, harness.Token);
        Assert.Null(await harness.InitializeAsync());
        Assert.Empty(harness.LoadedDescriptors);
        var generation = harness.Electrum.ConnectionGeneration;

        harness.UpdateSettings(settings => settings.AutoRegisterDescriptors = true);
        Assert.Null(await harness.InitializeAsync());
        Assert.Equal(generation, harness.Electrum.ConnectionGeneration);
        Assert.Equal(2, harness.LoadedDescriptors.Count);
    }

    [Fact]
    public async Task SharedPreparationIsCachedWithTheSettingsItActuallyUsed()
    {
        await using var harness = new Harness();
        harness.BlockchainInfo = _ => Harness.Info(true);
        harness.UpdateSettings(settings => settings.AutoRegisterDescriptors = false);
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        await harness.Tracker.TrackWalletAsync(wallet, harness.Token);
        Assert.Null(await harness.InitializeAsync());
        var oldPreparation = harness.CapturePreparation(wallet);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shared = harness.Tracker.GetOrStartWalletPreparationAsync(wallet, async ct =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(ct);
            return oldPreparation;
        }, harness.Token);
        await entered.Task.WaitAsync(harness.Token);
        harness.UpdateSettings(settings => settings.AutoRegisterDescriptors = true);

        // In-memory queries and cached settings complete synchronously; this
        // pass joins the shared preparation before yielding to its completion.
        var initializing = harness.Tracker.InitializeAsync(harness.Token, isInitialBlockDownload: true);
        Assert.False(initializing.IsCompleted);
        release.SetResult();
        await shared;
        await initializing;
        Assert.Empty(harness.LoadedDescriptors);

        Assert.Null(await harness.InitializeAsync());
        Assert.Equal(2, harness.LoadedDescriptors.Count);
    }

    [Fact]
    public async Task IbdEndingDuringAReusedPreparationStillRequiresAReadyPreparationPass()
    {
        await using var harness = new Harness();
        harness.BlockchainInfo = call => Harness.Info(call <= 4);
        await harness.AddCachedWalletAsync();
        Assert.Null(await harness.InitializeAsync());
        Assert.Equal(1, harness.SubscriptionRequests);

        Assert.Null(await harness.InitializeAsync());
        Assert.Equal(1, harness.SubscriptionRequests);
        Assert.Equal(90, await harness.InitializeAsync());
        Assert.Equal(2, harness.SubscriptionRequests);
    }

    [Fact]
    public async Task PendingRescanRunsAfterIbdWithDescriptorsAlreadyLoadedAndStopsAfterAcceptance()
    {
        await using var harness = new Harness();
        harness.UpdateSettings(settings => settings.AutoRescanOnNewDescriptor = true);
        harness.BlockchainInfo = _ => Harness.Info(true);
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        await harness.Tracker.TrackWalletAsync(wallet, harness.Token);
        Assert.Equal(FlorestaWalletRescanStatus.Pending, (await harness.GetWalletAsync(wallet)).RescanStatus);
        Assert.Null(await harness.InitializeAsync());
        Assert.Equal(0, harness.RescanRequests);

        harness.BlockchainInfo = _ => Harness.Info(false);
        Assert.Equal(90, await harness.InitializeAsync());
        Assert.Equal(2, harness.LoadedDescriptors.Count);
        Assert.Equal(1, harness.RescanRequests);
        Assert.Equal(FlorestaWalletRescanStatus.Requested, (await harness.GetWalletAsync(wallet)).RescanStatus);
        Assert.Null(await harness.InitializeAsync());
        await harness.Tracker.TrackWalletAsync(wallet, harness.Token);
        Assert.Equal(1, harness.RescanRequests);
    }

    [Fact]
    public async Task IbdRejectionAfterReadinessReopensAnAlreadyInitializedListener()
    {
        await using var harness = new Harness();
        harness.UpdateSettings(settings => settings.AutoRescanOnNewDescriptor = true);
        Assert.Equal(90, await harness.InitializeAsync());
        var generation = harness.Electrum.ConnectionGeneration;
        harness.Rescan = _ => Task.FromResult(Harness.RpcError(-32093));
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        await harness.Tracker.TrackWalletAsync(wallet, harness.Token);
        Assert.Equal(1, harness.RescanRequests);
        Assert.Equal(FlorestaWalletRescanStatus.Pending, (await harness.GetWalletAsync(wallet)).RescanStatus);

        Assert.Null(await harness.InitializeAsync());
        Assert.Equal(2, harness.RescanRequests);
        harness.Rescan = _ => Task.FromResult(Harness.Json(true));
        await harness.RetryUntilRescanRequestedAsync(wallet);
        Assert.Equal(generation, harness.Electrum.ConnectionGeneration);
        Assert.Equal(3, harness.RescanRequests);
        Assert.Null(await harness.InitializeAsync());
        Assert.Equal(3, harness.RescanRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingRescanSurvivesPluginRestartIncludingCancellation(bool cancelRequest)
    {
        var database = new DatabaseFactory();
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        var descriptors = new HashSet<string>();
        await using (var first = new Harness(database))
        {
            first.UpdateSettings(settings =>
            {
                settings.AutoRescanOnNewDescriptor = true;
                settings.DefaultRescanStartHeight = 12;
            });
            first.BlockchainInfo = _ => Harness.Info(!cancelRequest);
            if (cancelRequest)
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                first.Rescan = async ct =>
                {
                    entered.SetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                    return Harness.Json(true);
                };
                var preparing = first.Tracker.PrepareWalletTrackingAsync(wallet, first.Token);
                await entered.Task.WaitAsync(first.Token);
                await first.Tracker.StopAsync(first.Token);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparing);
            }
            else
                await first.Tracker.TrackWalletAsync(wallet, first.Token);
            Assert.Equal(FlorestaWalletRescanStatus.Pending, (await first.GetWalletAsync(wallet)).RescanStatus);
            descriptors.UnionWith(first.LoadedDescriptors);
        }

        await using var restarted = new Harness(database);
        restarted.LoadedDescriptors.UnionWith(descriptors);
        restarted.UpdateSettings(settings =>
        {
            settings.AutoRescanOnNewDescriptor = true;
            settings.DefaultRescanStartHeight = 80;
        });
        Assert.Equal(90, await restarted.InitializeAsync());
        Assert.Equal(1, restarted.RescanRequests);
        Assert.Equal(12, (await restarted.GetWalletAsync(wallet)).RescanStartHeight);
        Assert.Equal(12, Assert.Single(restarted.LastRescanParameters).GetInt32());
    }

    [Theory]
    [InlineData("rpc-error")]
    [InlineData("false")]
    public async Task DefinitiveRejectionDoesNotRepeatRescans(string rejection)
    {
        await using var harness = new Harness();
        harness.UpdateSettings(settings => settings.AutoRescanOnNewDescriptor = true);
        harness.Rescan = _ => Task.FromResult(rejection == "rpc-error" ? Harness.RpcError(-32092) : Harness.Json(false));
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        await Assert.ThrowsAnyAsync<Exception>(() => harness.Tracker.TrackWalletAsync(wallet, harness.Token));
        Assert.Equal(FlorestaWalletRescanStatus.Failed, (await harness.GetWalletAsync(wallet)).RescanStatus);
        Assert.Equal(90, await harness.InitializeAsync());
        Assert.Equal(1, harness.RescanRequests);
    }

    [Fact]
    public async Task DisablingAutomaticRescanClearsPendingIntent()
    {
        await using var harness = new Harness();
        harness.UpdateSettings(settings => settings.AutoRescanOnNewDescriptor = true);
        harness.BlockchainInfo = _ => Harness.Info(true);
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        await harness.Tracker.TrackWalletAsync(wallet, harness.Token);
        harness.UpdateSettings(settings => settings.AutoRescanOnNewDescriptor = false);
        harness.BlockchainInfo = _ => Harness.Info(false);
        Assert.Equal(90, await harness.InitializeAsync());
        Assert.Equal(FlorestaWalletRescanStatus.NotRequested, (await harness.GetWalletAsync(wallet)).RescanStatus);
        Assert.Equal(0, harness.RescanRequests);
    }

    [Fact]
    public async Task IntentIsPersistedBeforeDescriptorImportAndRetainedAfterPartialFailure()
    {
        await using var harness = new Harness();
        harness.UpdateSettings(settings => settings.AutoRescanOnNewDescriptor = true);
        harness.BlockchainInfo = _ => Harness.Info(true);
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        var loads = 0;
        harness.BeforeDescriptorLoad = async () =>
        {
            Assert.Equal(FlorestaWalletRescanStatus.Pending, (await harness.GetWalletAsync(wallet)).RescanStatus);
            if (++loads == 2)
                throw new HttpRequestException("descriptor response lost");
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Tracker.TrackWalletAsync(wallet, harness.Token));
        Assert.Equal(FlorestaWalletRescanStatus.Pending, (await harness.GetWalletAsync(wallet)).RescanStatus);
        Assert.Single(harness.LoadedDescriptors);
        harness.BeforeDescriptorLoad = () => Task.CompletedTask;
        Assert.Null(await harness.InitializeAsync());
        Assert.Equal(2, harness.LoadedDescriptors.Count);
        harness.BlockchainInfo = _ => Harness.Info(false);
        Assert.Equal(90, await harness.InitializeAsync());
        Assert.Equal(1, harness.RescanRequests);
    }

    [Fact]
    public async Task PendingRescanRetriesAfterTransientDescriptorListFailure()
    {
        await using var harness = new Harness();
        harness.UpdateSettings(settings => settings.AutoRescanOnNewDescriptor = true);
        Assert.Equal(90, await harness.InitializeAsync());
        harness.Rescan = _ => Task.FromResult(Harness.RpcError(-32093));
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        await harness.Tracker.TrackWalletAsync(wallet, harness.Token);
        harness.FailNextDescriptorList = true;
        Assert.Null(await harness.InitializeAsync());
        harness.Rescan = _ => Task.FromResult(Harness.Json(true));
        await harness.RetryUntilRescanRequestedAsync(wallet);
        Assert.Equal(FlorestaWalletRescanStatus.Requested, (await harness.GetWalletAsync(wallet)).RescanStatus);
        Assert.Equal(2, harness.RescanRequests);
    }

    [Fact]
    public async Task PendingFailedRegistrationDoesNotPreventOtherWalletsFromReconciling()
    {
        await using var harness = new Harness();
        harness.UpdateSettings(settings => settings.AutoRescanOnNewDescriptor = true);
        await harness.AddCachedWalletAsync();
        harness.BeforeDescriptorLoad = () => throw new HttpRequestException("descriptor unavailable");
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Tracker.TrackWalletAsync(wallet, harness.Token));
        Assert.Equal(90, await harness.InitializeAsync());
        Assert.True(await harness.CachedOutputIsSpentAsync());
        Assert.Equal(1, harness.HistoryRequests);
        Assert.Equal(0, harness.RescanRequests);
    }

    [Fact]
    public async Task RepeatedPendingRetriesDoNotPrepareOrSynchronizeHealthyDescriptorWallets()
    {
        await using var harness = new Harness();
        harness.UpdateSettings(settings => settings.AutoRescanOnNewDescriptor = true);
        var healthy = new ExtKey().Neuter().ToString(Network.RegTest);
        await harness.Tracker.TrackWalletAsync(healthy, harness.Token);
        Assert.Equal(90, await harness.InitializeAsync());
        var histories = harness.HistoryRequests;
        var subscriptions = harness.SubscriptionRequests;
        var rescanRequests = harness.RescanRequests;
        harness.BeforeDescriptorLoad = () => throw new HttpRequestException("descriptor unavailable");
        var failing = new ExtKey().Neuter().ToString(Network.RegTest);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Tracker.TrackWalletAsync(failing, harness.Token));
        var descriptorChecks = harness.DescriptorChecks;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.Null(await harness.InitializeAsync());
            if (attempt == 0)
                Assert.Equal(descriptorChecks + 1, harness.DescriptorChecks);
            Assert.Equal(histories, harness.HistoryRequests);
            Assert.Equal(subscriptions, harness.SubscriptionRequests);
            Assert.Equal(rescanRequests, harness.RescanRequests);
        }
    }

    // These tests exercise the actual tracker/listener and RPC/Electrum clients.
    // The in-memory provider checks orchestration and state transitions, not SQL;
    // the existing PostgreSQL E2E remains responsible for database fidelity.
    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));
        private readonly TcpListener _server = new(IPAddress.Loopback, 0);
        private readonly Task _serverTask;
        private readonly HttpClient _http;
        private readonly MemoryCache _settingsCache = new(new MemoryCacheOptions());
        private readonly EventAggregator _events = new(new Logs());
        private readonly DatabaseFactory _database;
        private readonly FlorestaSettings _settings;
        private readonly FlorestaDescriptorService _descriptors;
        private int _blockchainInfoCalls;
        public Func<int, HttpResponseMessage> BlockchainInfo { get; set; } = _ => Info(false);
        public Func<CancellationToken, Task<HttpResponseMessage>> Rescan { get; set; } = _ => Task.FromResult(Json(true));
        public int RescanRequests { get; private set; }
        public JsonElement[] LastRescanParameters { get; private set; } = [];
        public bool FailNextDescriptorLoad { get; set; }
        public bool FailNextDescriptorList { get; set; }
        public Func<Task> BeforeDescriptorLoad { get; set; } = () => Task.CompletedTask;
        public HashSet<string> LoadedDescriptors { get; } = new();
        public int DescriptorChecks { get; private set; }
        public int HistoryRequests { get; private set; }
        public int SubscriptionRequests { get; private set; }
        public FlorestaElectrumClient Electrum { get; }
        public FlorestaWalletTracker Tracker { get; }
        public FlorestaListener Listener { get; }
        public FlorestaStatusMonitor Monitor { get; }
        public CancellationToken Token => _timeout.Token;

        public Task<int?> InitializeAsync() => EnvironmentVariableTestHelper.WithBackendReplacementEnvironmentAsync(
            "true", () => Listener.InitializeListenerAsync(Token));

        public async Task RetryUntilRescanRequestedAsync(string wallet)
        {
            // A tracking job returns after preparation and keeps synchronizing
            // in the background. Later loop passes may join that existing job.
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            do
            {
                Assert.Null(await InitializeAsync());
                if ((await GetWalletAsync(wallet)).RescanStatus == FlorestaWalletRescanStatus.Requested)
                    return;
                await Task.Delay(10, Token);
            } while (DateTimeOffset.UtcNow < deadline);
            Assert.Equal(FlorestaWalletRescanStatus.Requested, (await GetWalletAsync(wallet)).RescanStatus);
        }

        public Harness(DatabaseFactory? database = null)
        {
            _database = database ?? new DatabaseFactory();
            _server.Start();
            _serverTask = ServeElectrumAsync();
            var settings = new FlorestaSettings
            {
                Network = "regtest", ElectrumHost = "127.0.0.1",
                ElectrumPort = ((IPEndPoint)_server.LocalEndpoint).Port,
                ElectrumUseTls = false, RpcUrl = "http://floresta.test",
                AutoRegisterDescriptors = true, AutoRescanOnNewDescriptor = false, GapLimit = 2,
                Enabled = true, UseFlorestaAsBitcoinBackend = true
            };
            _settings = settings;
            _settingsCache.Set("SettingsRepository_" + SettingsRepository.KeyNameByType(typeof(FlorestaSettings)),
                Newtonsoft.Json.JsonConvert.SerializeObject(settings));
            var settingsRepository = new SettingsRepository(null!, _events, _settingsCache);
            _http = new HttpClient(new RpcHandler(HandleRpc));
            var rpc = new FlorestaRpcClient(settings, NullLogger<FlorestaRpcClient>.Instance, _http);
            Electrum = new FlorestaElectrumClient(settings, NullLogger<FlorestaElectrumClient>.Instance);
            var nbx = new NBXplorerNetworkProvider(ChainName.Regtest);
            var btc = new BTCPayNetwork
            {
                CryptoCode = "BTC", NBXplorerNetwork = nbx.GetFromCryptoCode("BTC"), CoinType = new KeyPath("1'")
            }.SetDefaultElectrumMapping(ChainName.Regtest);
            var networks = new BTCPayNetworkProvider(new BTCPayNetworkBase[] { btc }, nbx, new Logs());
            var descriptors = new FlorestaDescriptorService(networks);
            _descriptors = descriptors;
            Tracker = new FlorestaWalletTracker(Electrum, null!, rpc, descriptors,
                new FlorestaDescriptorRegistry(descriptors, rpc), _database, settingsRepository,
                networks, NullLogger<FlorestaWalletTracker>.Instance);
            Monitor = new FlorestaStatusMonitor(Electrum, rpc, settingsRepository, null!,
                new NBXplorerDashboard(), networks, _events, NullLogger<FlorestaStatusMonitor>.Instance);
            Listener = new FlorestaListener(Electrum, Tracker, Monitor, settingsRepository,
                null!, null!, _events, null!, null!, networks, NullLogger<FlorestaListener>.Instance);
        }

        public async Task AddCachedWalletAsync()
        {
            await using var ctx = _database.CreateContext();
            ctx.TrackedWallets.Add(new TrackedWallet { Id = "wallet", CryptoCode = "BTC" });
            ctx.TrackedAddresses.Add(new TrackedAddress
            {
                WalletId = "wallet", Scripthash = "known", Address = "known", ScriptPubKey = Array.Empty<byte>()
            });
            ctx.Utxos.Add(new TrackedUtxo
            {
                WalletId = "wallet", Scripthash = "known", Txid = uint256.One.ToString(),
                Outpoint = uint256.One + ":0", ScriptPubKey = Array.Empty<byte>(), Value = 1000
            });
            await ctx.SaveChangesAsync(Token);
        }

        public void UpdateSettings(Action<FlorestaSettings> update)
        {
            update(_settings);
            _settingsCache.Set("SettingsRepository_" + SettingsRepository.KeyNameByType(typeof(FlorestaSettings)),
                Newtonsoft.Json.JsonConvert.SerializeObject(_settings));
        }

        public FlorestaWalletTracker.WalletPreparationContext CapturePreparation(string wallet)
        {
            var settings = Newtonsoft.Json.JsonConvert.DeserializeObject<FlorestaSettings>(
                Newtonsoft.Json.JsonConvert.SerializeObject(_settings))!;
            return new(settings, new FlorestaDescriptorRegistrationResult(
                _descriptors.CreateDescriptors("BTC", wallet), 0, 0, null), null);
        }

        public async Task<bool> CachedOutputIsSpentAsync()
        {
            await using var ctx = _database.CreateContext();
            return (await ctx.Utxos.SingleAsync(Token)).IsSpent;
        }

        public static HttpResponseMessage Info(bool ibd) => Json(new
        {
            chain = "regtest", blocks = 90, headers = 100, initialblockdownload = ibd
        });

        public async Task<TrackedWallet> GetWalletAsync(string wallet)
        {
            await using var ctx = _database.CreateContext();
            return await ctx.TrackedWallets.SingleAsync(w => w.Id == wallet, Token);
        }

        public static HttpResponseMessage RpcError(int code) => new(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { error = new { code, message = "rescan rejected" } }))
        };

        public static HttpResponseMessage Json(object? result) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { result }))
        };

        private async Task<HttpResponseMessage> HandleRpc(JsonElement request, CancellationToken ct)
        {
            switch (request.GetProperty("method").GetString())
            {
                case "getblockchaininfo": return BlockchainInfo(++_blockchainInfoCalls);
                case "listdescriptors":
                    if (FailNextDescriptorList)
                    {
                        FailNextDescriptorList = false;
                        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    }
                    DescriptorChecks++; return Json(LoadedDescriptors);
                case "loaddescriptor":
                    await BeforeDescriptorLoad();
                    if (FailNextDescriptorLoad)
                    {
                        FailNextDescriptorLoad = false;
                        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    }
                    LoadedDescriptors.Add(request.GetProperty("params")[0].GetString()!);
                    return Json(true);
                case "rescanblockchain":
                    RescanRequests++;
                    var parameters = new List<JsonElement>();
                    foreach (var parameter in request.GetProperty("params").EnumerateArray())
                        parameters.Add(parameter.Clone());
                    LastRescanParameters = parameters.ToArray();
                    return await Rescan(ct);
                default: throw new InvalidOperationException("Unexpected RPC in initialization test");
            }
        }

        private async Task ServeElectrumAsync()
        {
            try
            {
                while (!Token.IsCancellationRequested)
                {
                    using var client = await _server.AcceptTcpClientAsync(Token);
                    using var reader = new StreamReader(client.GetStream());
                    await using var writer = new StreamWriter(client.GetStream()) { AutoFlush = true };
                    while (await reader.ReadLineAsync(Token) is { } line)
                    {
                        using var request = JsonDocument.Parse(line);
                        object? result = request.RootElement.GetProperty("method").GetString() switch
                        {
                            "server.ping" => null,
                            "server.version" => new[] { "floresta-test", "1.4" },
                            "server.features" => new { genesis_hash = Network.RegTest.GenesisHash.ToString() },
                            "blockchain.headers.subscribe" => new { height = 100, hex = Convert.ToHexString(Network.RegTest.GetGenesis().Header.ToBytes()) },
                            "blockchain.scripthash.subscribe" => Subscribe(),
                            "blockchain.scripthash.get_history" => History(),
                            "blockchain.scripthash.listunspent" => Array.Empty<object>(),
                            "blockchain.scripthash.get_balance" => new { confirmed = 0, unconfirmed = 0 },
                            _ => throw new InvalidOperationException("Unexpected Electrum request in initialization test")
                        };
                        await writer.WriteLineAsync(JsonSerializer.Serialize(new
                        {
                            jsonrpc = "2.0", id = request.RootElement.GetProperty("id").GetInt32(), result
                        }));
                    }
                }
            }
            catch (OperationCanceledException) when (Token.IsCancellationRequested) { }
        }

        private object[] History()
        {
            HistoryRequests++;
            return Array.Empty<object>();
        }

        private object? Subscribe()
        {
            SubscriptionRequests++;
            return null;
        }

        public async ValueTask DisposeAsync()
        {
            await Tracker.StopAsync(CancellationToken.None);
            await _timeout.CancelAsync();
            await Electrum.DisposeAsync();
            await _serverTask;
            _server.Stop();
            _http.Dispose();
            _events.Dispose();
            _settingsCache.Dispose();
            _timeout.Dispose();
        }
    }

    private sealed class RpcHandler(Func<JsonElement, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            return await respond(json.RootElement, ct);
        }
    }

    private sealed class DatabaseFactory() : FlorestaDbContextFactory(Options.Create(new DatabaseOptions()))
    {
        private readonly DbContextOptions<FlorestaDbContext> _options =
            new DbContextOptionsBuilder<FlorestaDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

        public override FlorestaDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptionsAction = null) => new(_options);
    }
}
