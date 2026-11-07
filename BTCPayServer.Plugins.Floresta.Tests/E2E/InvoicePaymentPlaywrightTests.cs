using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Floresta.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using NBitcoin;
using NBitcoin.RPC;
using Npgsql;
using static Microsoft.Playwright.Assertions;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests.E2E;

[Collection(FlorestaE2ECollection.Name)]
public sealed class InvoicePaymentPlaywrightTests : IAsyncLifetime
{
    private const string BitcoinWalletName = "floresta-e2e";

    private const string WalletSeedMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    private static readonly KeyPath WalletAccountKeyPath = new("84'/1'/0'");

    private readonly BtcpayFlorestaWebAppFixture _server;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage? _page;

    public InvoicePaymentPlaywrightTests(BtcpayFlorestaWebAppFixture server)
    {
        _server = server;
    }

    [Fact(Timeout = 900_000)]
    [Trait("Playwright", "Playwright")]
    public async Task BTCPayInvoiceCanBePaidAndSpentThroughFlorestaWallet()
    {
        var page = _page ?? throw new InvalidOperationException("Playwright page is not initialized.");

        await RegisterAdmin(page);
        await ConfigureFloresta(page);

        var bitcoin = CreateBitcoinRpcClient();
        var bitcoinWallet = await PrepareBitcoinWalletAsync(bitcoin);
        await ConnectRegtestMeshAsync(bitcoin);
        await FundBitcoinMinerAsync(bitcoinWallet);
        await SubmitBitcoinChainToUtreexodAsync(bitcoin);
        await RefreshFlorestaUtreexoPeerAsync();
        var chainHeight = await bitcoin.GetBlockCountAsync();
        var bestBlockHash = await GetBitcoinBestBlockHashAsync(bitcoin);
        await WaitForUtreexodChainAsync(chainHeight, bestBlockHash);
        await WaitForFlorestaChainAsync(chainHeight, bestBlockHash);
        await WaitForBtcpayFlorestaBackendAsync(page, chainHeight);
        var historicalPayments = await CreateHistoricalPaymentsAsync(bitcoin, bitcoinWallet);

        var storeId = await CreateStore(page);
        await ConfigureStoreToSettleUnconfirmed(page, storeId);
        var strategy = GetWalletAccountKey();
        var trackingOutputOffset = _server.Output.Length;
        await AddWatchOnlyWallet(page, storeId);
        await WaitForDescriptorsAsync();
        await WaitForWalletTrackingReadyAsync(strategy, trackingOutputOffset);

        var walletUrl = new Uri(_server.ServerUri, $"/wallets/S-{storeId}-BTC?loadTransactions=true").ToString();

        var txId = await WithContinuousElectrumRequestsAsync(async () =>
        {
            var payment = await PayInvoiceAndWaitAsync(page, storeId, bitcoin, bitcoinWallet, "0.00100000", Money.Coins(0.001m));
            await EventuallyAsync(async () =>
            {
                await using var connection = new NpgsqlConnection(GetE2ePostgresConnectionString());
                await connection.OpenAsync();
                Assert.Equal(1, await CountAsync(connection, "floresta.transactions",
                    @"""WalletId"" = @walletId AND ""Txid"" = @txId AND ""BlockHeight"" > 0 AND ""RemovedAt"" IS NULL",
                    strategy, payment.ToString()));
            }, TimeSpan.FromSeconds(60), "payment confirmation while Electrum requests continue");
            return payment;
        });
        await AssertWalletTransactionVisibleAsync(page, walletUrl, txId, "0.00100000");

        await AssertWalletCacheCanBeRebuiltAsync(page, storeId, txId);

        var beforeBtcpayRestart = await ReadWalletCacheSnapshotAsync(strategy, txId.ToString());
        await _server.RestartAsync();
        await WaitForBtcpayFlorestaBackendAsync(page, await bitcoin.GetBlockCountAsync());
        await AssertWalletSnapshotPreservedAsync(strategy, txId, beforeBtcpayRestart);
        await AssertWalletTransactionVisibleAsync(page, walletUrl, txId, "0.00100000");

        var postBtcpayRestartTxId = await PayInvoiceAndWaitAsync(page, storeId, bitcoin, bitcoinWallet, "0.00070000", Money.Coins(0.0007m));
        await AssertWalletTransactionVisibleAsync(page, walletUrl, postBtcpayRestartTxId, "0.00070000");

        await AssertInterruptedWalletScanCanResumeAsync(
            page,
            storeId,
            await bitcoin.GetBlockCountAsync(),
            txId,
            postBtcpayRestartTxId);

        // This descriptor is never given to BTCPay, so it cannot be re-registered
        // by the plugin and conceal a node persistence failure after restart.
        var historicalDescriptor = await AssertHistoricalPaymentRescanAsync(historicalPayments.ImportPayment, historicalPayments.RescanPayment);
        await RestartFlorestaServiceAsync();
        var descriptorsAfterRestart = await FlorestaRpcAsync("listdescriptors", Array.Empty<object>());
        Assert.Contains(descriptorsAfterRestart.EnumerateArray(), item => item.GetString() == historicalDescriptor);
        await ConnectRegtestMeshAsync(bitcoin);
        await RefreshFlorestaUtreexoPeerAsync();
        await WaitForFlorestaChainAsync(await bitcoin.GetBlockCountAsync(), await GetBitcoinBestBlockHashAsync(bitcoin));
        await WaitForBtcpayFlorestaBackendAsync(page, await bitcoin.GetBlockCountAsync());

        var postFlorestaRestartTxId = await PayInvoiceAndWaitAsync(page, storeId, bitcoin, bitcoinWallet, "0.00060000", Money.Coins(0.0006m));
        await AssertWalletTransactionVisibleAsync(page, walletUrl, postFlorestaRestartTxId, "0.00060000");
        await AssertKnownPaymentsRemainActiveAsync(strategy, txId, postBtcpayRestartTxId, postFlorestaRestartTxId);

        var spendTxId = await SpendFromBtcpayWalletWithSeed(page, storeId, bitcoinWallet);
        await WaitForBitcoinMempoolAsync(bitcoin, spendTxId);
        await bitcoinWallet.GenerateToAddressAsync(1, await bitcoinWallet.GetNewAddressAsync());
        await SubmitBitcoinChainToUtreexodAsync(bitcoin);
        await RefreshFlorestaUtreexoPeerAsync();
        await WaitForUtreexodChainAsync(await bitcoin.GetBlockCountAsync(), await GetBitcoinBestBlockHashAsync(bitcoin));
        await WaitForFlorestaChainAsync(await bitcoin.GetBlockCountAsync(), await GetBitcoinBestBlockHashAsync(bitcoin));

        await AssertWalletTransactionVisibleAsync(page, walletUrl, spendTxId, amountShouldBeNegative: true);
    }

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = Environment.GetEnvironmentVariable("PLAYWRIGHT_HEADLESS") != "false",
            Args = new[] { "--no-sandbox", "--disable-dev-shm-usage" }
        });
        var context = await _browser.NewContextAsync();
        _page = await context.NewPageAsync();
        _page.SetDefaultTimeout(15_000);
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (_page is not null)
                await _page.CloseAsync();
        }
        finally
        {
            try
            {
                if (_browser is not null)
                    await _browser.CloseAsync();
            }
            finally
            {
                _playwright?.Dispose();
                await UnloadBitcoinWalletAsync();
            }
        }
    }

    private static RPCClient CreateBitcoinRpcClient()
    {
        var connectionString = Environment.GetEnvironmentVariable("E2E_BITCOIND_RPC") ??
                               "server=http://127.0.0.1:43782;ceiwHEbqWI83:DwubwWsoo3";
        return new RPCClient(RPCCredentialString.Parse(connectionString), Network.RegTest);
    }

    private static async Task<RPCClient> PrepareBitcoinWalletAsync(RPCClient rpc)
    {
        return await GetOrCreateWalletAsync(rpc);
    }

    private static async Task<string[]> FundBitcoinMinerAsync(RPCClient wallet)
    {
        var address = await wallet.GetNewAddressAsync();
        var blocks = await wallet.GenerateToAddressAsync(101, address);
        return blocks.Select(blockHash => blockHash.ToString()).ToArray();
    }

    private static async Task<RPCClient> GetOrCreateWalletAsync(RPCClient rpc)
    {
        try
        {
            return await rpc.CreateWalletAsync(BitcoinWalletName, new CreateWalletOptions
            {
                LoadOnStartup = false,
                Descriptors = true
            });
        }
        catch (RPCException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
        }

        try
        {
            return await rpc.LoadWalletAsync(BitcoinWalletName, false);
        }
        catch (RPCException ex) when (ex.Message.Contains("already loaded", StringComparison.OrdinalIgnoreCase))
        {
            return rpc.SetWalletContext(BitcoinWalletName);
        }
    }

    private static async Task UnloadBitcoinWalletAsync()
    {
        try
        {
            await CreateBitcoinRpcClient().UnloadWalletAsync(BitcoinWalletName, false);
        }
        catch (RPCException ex) when (
            ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("is not loaded", StringComparison.OrdinalIgnoreCase))
        {
        }
        catch (HttpRequestException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task ConnectRegtestMeshAsync(RPCClient bitcoin)
    {
        var bitcoindEndpoint = ResolveP2PEndpoint("E2E_BITCOIND_P2P_HOST", "E2E_BITCOIND_P2P_PORT", "bitcoind", 39388);
        var utreexodEndpoint = ResolveP2PEndpoint("E2E_UTREEXOD_P2P_HOST", "E2E_UTREEXOD_P2P_PORT", "utreexod", 38333);

        await AddFlorestaPeerAsync(utreexodEndpoint);
        await AddBitcoinPeerAsync(bitcoin, utreexodEndpoint);
        await AddFlorestaPeerAsync(bitcoindEndpoint);
        await WaitForRegtestMeshAsync(bitcoin, bitcoindEndpoint, utreexodEndpoint);
    }

    private static string ResolveP2PEndpoint(string hostVariable, string portVariable, string defaultHost, int defaultPort)
    {
        var host = Environment.GetEnvironmentVariable(hostVariable) ??
                   TryResolveComposeServiceIp(defaultHost) ??
                   defaultHost;
        var port = int.TryParse(Environment.GetEnvironmentVariable(portVariable), out var parsedPort)
            ? parsedPort
            : defaultPort;
        return $"{host}:{port}";
    }

    private static string? TryResolveComposeServiceIp(string serviceName)
    {
        try
        {
            var composeFile = Path.Combine(GetRepoRoot(), "docker-compose.integration.yml");
            var container = RunProcess("docker", "compose", "-f", composeFile, "ps", "-q", serviceName);
            if (string.IsNullOrWhiteSpace(container))
                return null;

            var ip = RunProcess(
                "docker",
                "inspect",
                "-f",
                "{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}",
                container.Trim());
            return string.IsNullOrWhiteSpace(ip) ? null : ip.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string? RunProcess(string fileName, params string[] args)
    {
        return RunProcess(TimeSpan.FromSeconds(5), fileName, args);
    }

    private static string? RunProcess(TimeSpan timeout, string fileName, params string[] args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            return null;
        }

        _ = error.GetAwaiter().GetResult();
        return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
    }

    private static async Task RestartFlorestaServiceAsync()
    {
        var composeFile = Path.Combine(GetRepoRoot(), "docker-compose.integration.yml");
        var output = RunProcess(
            TimeSpan.FromSeconds(60),
            "docker",
            "compose",
            "-f",
            composeFile,
            "restart",
            "floresta");
        Assert.NotNull(output);

        await EventuallyAsync(async () =>
        {
            await FlorestaRpcAsync("ping", Array.Empty<object>());
        }, TimeSpan.FromSeconds(120), "Floresta RPC to recover after Docker restart");
    }

    private static FlorestaElectrumClient CreateElectrumClient()
    {
        var settings = new FlorestaSettings
        {
            ElectrumHost = Environment.GetEnvironmentVariable("FLORESTA_ELECTRUM_HOST") ?? "127.0.0.1",
            ElectrumPort = int.TryParse(Environment.GetEnvironmentVariable("FLORESTA_ELECTRUM_PORT"), out var port) ? port : 20001,
            ElectrumUseTls = bool.TryParse(Environment.GetEnvironmentVariable("FLORESTA_ELECTRUM_TLS"), out var tls) && tls
        };
        return new FlorestaElectrumClient(settings, NullLogger<FlorestaElectrumClient>.Instance);
    }

    private static async Task<T> WithContinuousElectrumRequestsAsync<T>(Func<Task<T>> action)
    {
        using var stop = new CancellationTokenSource();
        var clients = Enumerable.Range(0, 4).Select(_ => CreateElectrumClient()).ToArray();
        var requests = 0;
        async Task PollAsync(FlorestaElectrumClient client)
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await client.PingAsync(stop.Token);
                    Interlocked.Increment(ref requests);
                    await Task.Delay(100, stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
        }

        try
        {
            await Task.WhenAll(clients.Select(client => client.ConnectAsync(stop.Token)));
            var pollers = clients.Select(PollAsync).ToArray();
            try
            {
                var result = await action();
                Assert.True(Volatile.Read(ref requests) >= 20, "Expected sustained Electrum polling during payment confirmation");
                return result;
            }
            finally
            {
                stop.Cancel();
                await Task.WhenAll(pollers);
            }
        }
        finally
        {
            foreach (var client in clients)
                await client.DisposeAsync();
        }
    }

    private static async Task<(HistoricalPayment ImportPayment, HistoricalPayment RescanPayment)> CreateHistoricalPaymentsAsync(RPCClient bitcoin, RPCClient bitcoinWallet)
    {
        var key = new ExtKey().Neuter();
        var descriptor = $"wpkh({key.GetWif(Network.RegTest)}/0/*)";

        async Task<HistoricalPayment> PayAndMineAsync(uint index)
        {
            var address = key.Derive(new KeyPath($"0/{index}")).PubKey.WitHash.GetAddress(Network.RegTest);
            var txId = await bitcoinWallet.SendToAddressAsync(address, Money.Coins(0.0002m));
            await bitcoinWallet.GenerateToAddressAsync(1, await bitcoinWallet.GetNewAddressAsync());
            await SubmitBitcoinChainToUtreexodAsync(bitcoin);
            await RefreshFlorestaUtreexoPeerAsync();
            var height = await bitcoin.GetBlockCountAsync();
            var bestBlock = await GetBitcoinBestBlockHashAsync(bitcoin);
            await WaitForUtreexodChainAsync(height, bestBlock);
            await WaitForFlorestaChainAsync(height, bestBlock);

            return new HistoricalPayment(descriptor, ScriptHashUtility.ComputeScriptHash(address.ScriptPubKey), txId, height);
        }

        // The node initially derives 0..99. Mine index120 before index80, in a
        // separate block, so import's scan cannot recover it after index80
        // triggers derivation through index199.
        var rescanPayment = await PayAndMineAsync(120);
        var importPayment = await PayAndMineAsync(80);
        return (importPayment, rescanPayment);
    }

    private static async Task<string> AssertHistoricalPaymentRescanAsync(HistoricalPayment importPayment, HistoricalPayment rescanPayment)
    {
        await using var electrum = CreateElectrumClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        await electrum.ConnectAsync(timeout.Token);
        Assert.Empty(await electrum.ScripthashGetHistoryAsync(importPayment.ScriptHash, timeout.Token));
        Assert.Empty(await electrum.ScripthashGetHistoryAsync(rescanPayment.ScriptHash, timeout.Token));

        // Import's automatic scan finds index80 and expands the horizon. The
        // older index120 payment must still be absent when that scan finishes.
        Assert.True((await FlorestaRpcAsync("loaddescriptor", new object[] { importPayment.Descriptor })).GetBoolean());
        await EventuallyAsync(async () =>
        {
            var history = await electrum.ScripthashGetHistoryAsync(importPayment.ScriptHash, timeout.Token);
            Assert.Contains(history, item => item.TxHash == importPayment.TxId.ToString() && item.Height == importPayment.Height);
        }, TimeSpan.FromSeconds(60), "historical payment to be discovered by descriptor import");
        Assert.Empty(await electrum.ScripthashGetHistoryAsync(rescanPayment.ScriptHash, timeout.Token));

        Assert.True((await FlorestaRpcAsync("rescanblockchain", new object[] { rescanPayment.Height - 1, rescanPayment.Height, false, "medium" })).GetBoolean());
        await EventuallyAsync(async () =>
        {
            var history = await electrum.ScripthashGetHistoryAsync(rescanPayment.ScriptHash, timeout.Token);
            Assert.Contains(history, item => item.TxHash == rescanPayment.TxId.ToString() && item.Height == rescanPayment.Height);
        }, TimeSpan.FromSeconds(60), "historical payment to be discovered only by explicit rescan");
        return importPayment.Descriptor;
    }

    private static string GetRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BTCPayServer.Plugins.Floresta.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    private static async Task AddFlorestaPeerAsync(string endpoint)
    {
        await FlorestaRpcAsync("addnode", new object[] { endpoint, "add", false });
    }

    private static async Task AddBitcoinPeerAsync(RPCClient bitcoin, string endpoint)
    {
        try
        {
            await bitcoin.SendCommandAsync("addnode", endpoint, "add");
        }
        catch (RPCException ex) when (ex.Message.Contains("already added", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    private static async Task RefreshFlorestaUtreexoPeerAsync()
    {
        var endpoint = ResolveP2PEndpoint("E2E_UTREEXOD_P2P_HOST", "E2E_UTREEXOD_P2P_PORT", "utreexod", 38333);
        await EventuallyAsync(async () =>
        {
            await TryFlorestaRpcAsync("addnode", new object[] { endpoint, "onetry", false });
            var peers = await FlorestaRpcAsync("getpeerinfo", Array.Empty<object>());
            Assert.True(
                peers.EnumerateArray().Any(peer => PeerMatches(peer, "address", endpoint)),
                $"Expected Floresta to have a utreexod peer at {endpoint}");
        }, TimeSpan.FromSeconds(180), "Floresta utreexod connection retry");
    }

    private static async Task WaitForRegtestMeshAsync(RPCClient bitcoin, string bitcoindEndpoint, string utreexodEndpoint)
    {
        await EventuallyAsync(async () =>
        {
            var florestaPeers = await FlorestaRpcAsync("getpeerinfo", Array.Empty<object>());
            Assert.True(florestaPeers.GetArrayLength() >= 2, $"Expected Floresta to have at least 2 peers, got {florestaPeers.GetArrayLength()}");
            Assert.True(florestaPeers.EnumerateArray().Any(peer => PeerMatches(peer, "address", bitcoindEndpoint) || PeerMatches(peer, "user_agent", "Satoshi")),
                "Expected Floresta to be connected to bitcoind");
            Assert.True(florestaPeers.EnumerateArray().Any(peer => PeerMatches(peer, "address", utreexodEndpoint) || PeerMatches(peer, "user_agent", "utreexo")),
                "Expected Floresta to be connected to utreexod");
            Assert.True(florestaPeers.EnumerateArray().Any(peer =>
                    peer.TryGetProperty("servicesnames", out var services) &&
                    services.EnumerateArray().Any(service => service.GetString() == "COMPACT_FILTERS")),
                "Expected a compact-filter peer for the historical rescan (utreexod --cfilters)");

            var bitcoindPeers = await BitcoinRpcJsonAsync(bitcoin, "getpeerinfo");
            Assert.True(bitcoindPeers.EnumerateArray().Any(peer => PeerMatches(peer, "addr", utreexodEndpoint) || PeerMatches(peer, "subver", "utreexo")),
                "Expected bitcoind to be connected to utreexod");
        }, TimeSpan.FromSeconds(90), "regtest P2P mesh");
    }

    private static bool PeerMatches(JsonElement peer, string propertyName, string expected)
    {
        return peer.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String &&
               property.GetString()?.Contains(expected, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static async Task<string> GetBitcoinBestBlockHashAsync(RPCClient bitcoin)
    {
        var response = await bitcoin.SendCommandAsync("getbestblockhash");
        if (string.IsNullOrWhiteSpace(response.ResultString))
            throw new InvalidOperationException("bitcoind getbestblockhash returned an empty result.");
        return response.ResultString;
    }

    private static async Task SubmitBlocksToUtreexodAsync(RPCClient bitcoin, string[] blockHashes)
    {
        foreach (var blockHash in blockHashes)
        {
            var response = await bitcoin.SendCommandAsync("getblock", blockHash, 0);
            if (string.IsNullOrWhiteSpace(response.ResultString))
                throw new InvalidOperationException($"bitcoind getblock {blockHash} returned an empty raw block.");

            var submitResult = await UtreexodRpcAsync("submitblock", new object[] { response.ResultString });
            if (submitResult.ValueKind == JsonValueKind.String)
            {
                var message = submitResult.GetString() ?? string.Empty;
                if (!message.Contains("already have block", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"utreexod rejected block {blockHash}: {message}");
            }
        }
    }

    private static async Task SubmitBitcoinChainToUtreexodAsync(RPCClient bitcoin)
    {
        var bitcoinHeight = await bitcoin.GetBlockCountAsync();
        var utreexodInfo = await UtreexodRpcAsync("getblockchaininfo", Array.Empty<object>());
        var utreexodHeight = utreexodInfo.GetProperty("blocks").GetInt32();
        var startHeight = Math.Max(1, utreexodHeight + 1);

        for (var height = startHeight; height <= bitcoinHeight; height++)
        {
            var response = await bitcoin.SendCommandAsync("getblockhash", height);
            if (string.IsNullOrWhiteSpace(response.ResultString))
                throw new InvalidOperationException($"bitcoind getblockhash {height} returned an empty result.");

            await SubmitBlocksToUtreexodAsync(bitcoin, new[] { response.ResultString });
        }
    }

    private static async Task<JsonElement> BitcoinRpcJsonAsync(RPCClient bitcoin, string method, params object[] parameters)
    {
        var response = await bitcoin.SendCommandAsync(method, parameters);
        using var doc = JsonDocument.Parse(response.Result.ToString());
        return doc.RootElement.Clone();
    }

    private static async Task WaitForBitcoinMempoolAsync(RPCClient bitcoin, uint256 txId)
    {
        await EventuallyAsync(async () =>
        {
            var mempool = await BitcoinRpcJsonAsync(bitcoin, "getrawmempool");
            Assert.Contains(
                mempool.EnumerateArray(),
                tx => string.Equals(tx.GetString(), txId.ToString(), StringComparison.OrdinalIgnoreCase));
        }, TimeSpan.FromSeconds(60), $"bitcoind mempool to contain {txId}");
    }

    private static async Task WaitForFlorestaChainAsync(int expectedHeight, string expectedBestBlock)
    {
        await EventuallyAsync(async () =>
        {
            var info = await FlorestaRpcAsync("getblockchaininfo", Array.Empty<object>());
            var chainInfo = FlorestaChainInfoParser.Parse(info);
            var rootCount = chainInfo.UtreexoRootCount ?? 0;
            var validated = chainInfo.ValidatedHeight ?? 0;
            var ibd = chainInfo.IsInitialBlockDownload ?? true;
            Assert.NotNull(chainInfo.Height);
            Assert.True(chainInfo.Height.Value >= expectedHeight, $"Floresta height {chainInfo.Height.Value} is below bitcoind height {expectedHeight}");
            Assert.Equal(expectedBestBlock, chainInfo.BestBlockHash);
            Assert.False(ibd, "Expected Floresta to leave IBD before continuing");
            Assert.True(rootCount > 0 || validated > 0, $"Expected Floresta to expose usable Utreexo state, got root_count={rootCount}, validated={validated}");
        }, TimeSpan.FromSeconds(180), "floresta to sync to bitcoind");
    }

    private static async Task WaitForUtreexodChainAsync(int expectedHeight, string expectedBestBlock)
    {
        await EventuallyAsync(async () =>
        {
            var info = await UtreexodRpcAsync("getblockchaininfo", Array.Empty<object>());
            var height = info.GetProperty("blocks").GetInt32();
            var bestBlock = info.GetProperty("bestblockhash").GetString();
            Assert.True(height >= expectedHeight, $"utreexod height {height} is below bitcoind height {expectedHeight}");
            Assert.Equal(expectedBestBlock, bestBlock);
        }, TimeSpan.FromSeconds(60), "utreexod to accept bitcoind blocks");
    }

    private static async Task WaitForDescriptorsAsync()
    {
        await EventuallyAsync(async () =>
        {
            var result = await FlorestaRpcAsync("listdescriptors", Array.Empty<object>());
            Assert.True(result.GetArrayLength() >= 2, "Expected receive/change descriptors to be registered in Floresta");
        }, TimeSpan.FromSeconds(60), "Floresta descriptor registration");
    }

    private async Task WaitForBtcpayFlorestaBackendAsync(IPage page, int expectedHeight)
    {
        await EventuallyAsync(async () =>
        {
            await page.GotoAsync(new Uri(_server.ServerUri, "/server/floresta").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.Commit });
            await AssertNoUiError(page);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Test Connection" }).ClickAsync();
            await Expect(page.GetByText(new Regex("Connection successful", RegexOptions.IgnoreCase))).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
            {
                Timeout = 20_000
            });
            await Expect(page.Locator("#FlorestaHealthElectrumStatus")).ToContainTextAsync("Reachable", new LocatorAssertionsToContainTextOptions { Timeout = 2_000 });
            await Expect(page.Locator("#FlorestaHealthRpcStatus")).ToContainTextAsync("Reachable", new LocatorAssertionsToContainTextOptions { Timeout = 2_000 });
            await Expect(page.Locator("#FlorestaHealthIbd")).ToContainTextAsync("No", new LocatorAssertionsToContainTextOptions { Timeout = 2_000 });

            var heightText = (await page.Locator("#FlorestaHealthHeight").InnerTextAsync()).Trim();
            Assert.True(
                int.TryParse(heightText, out var height) && height >= expectedHeight,
                $"BTCPay Floresta status height {heightText} is below expected height {expectedHeight}");
        }, TimeSpan.FromSeconds(90), "BTCPay Floresta backend status");
    }

    private static async Task<JsonElement> FlorestaRpcAsync(string method, object[] parameters)
    {
        var rpcUrl = Environment.GetEnvironmentVariable("FLORESTA_RPC_URL") ?? "http://127.0.0.1:18442";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Post, rpcUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                method,
                @params = parameters
            }), Encoding.UTF8, "application/json")
        };
        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        if (!response.IsSuccessStatusCode ||
            doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            throw new InvalidOperationException($"Floresta RPC {method} failed: HTTP {(int)response.StatusCode} {body}");

        return doc.RootElement.GetProperty("result").Clone();
    }

    private static async Task<bool> TryFlorestaRpcAsync(string method, object[] parameters)
    {
        try
        {
            await FlorestaRpcAsync(method, parameters);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<JsonElement> UtreexodRpcAsync(string method, object[] parameters)
    {
        var rpcUrl = Environment.GetEnvironmentVariable("E2E_UTREEXOD_RPC_URL") ?? "https://127.0.0.1:38332";
        var rpcUser = Environment.GetEnvironmentVariable("E2E_UTREEXOD_RPC_USER") ?? "test";
        var rpcPassword = Environment.GetEnvironmentVariable("E2E_UTREEXOD_RPC_PASSWORD") ?? "test";
        using var handler = new HttpClientHandler();
        if (new Uri(rpcUrl).Scheme == Uri.UriSchemeHttps)
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Post, rpcUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "1.0",
                id = 1,
                method,
                @params = parameters
            }), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{rpcUser}:{rpcPassword}")));

        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        if (!response.IsSuccessStatusCode ||
            doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            throw new InvalidOperationException($"utreexod RPC {method} failed: HTTP {(int)response.StatusCode} {body}");

        return doc.RootElement.GetProperty("result").Clone();
    }

    private async Task RegisterAdmin(IPage page)
    {
        await page.GotoAsync(new Uri(_server.ServerUri, "/register").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.Commit });
        await AssertNoUiError(page);

        var suffix = Guid.NewGuid().ToString("N")[..12];
        await page.Locator("#Email").FillAsync($"floresta-payment-{suffix}@example.com");
        await page.Locator("#Password").FillAsync("123456");
        await page.Locator("#ConfirmPassword").FillAsync("123456");
        if (await page.Locator("#IsAdmin").CountAsync() > 0)
            await page.Locator("#IsAdmin").SetCheckedAsync(true, new LocatorSetCheckedOptions { Force = true });
        await page.Locator("#RegisterButton").ClickAsync();
        await Expect(page).ToHaveURLAsync(new Regex(".*/(dashboard|stores/create)$", RegexOptions.IgnoreCase));
    }

    private async Task ConfigureFloresta(IPage page)
    {
        var electrumHost = Environment.GetEnvironmentVariable("FLORESTA_ELECTRUM_HOST") ?? "127.0.0.1";
        var electrumPort = Environment.GetEnvironmentVariable("FLORESTA_ELECTRUM_PORT") ?? "20001";
        var rpcUrl = Environment.GetEnvironmentVariable("FLORESTA_RPC_URL") ?? "http://127.0.0.1:18442";

        await page.GotoAsync(new Uri(_server.ServerUri, "/server/floresta").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.Commit });
        await AssertNoUiError(page);
        await page.Locator("#Enabled").SetCheckedAsync(true, new LocatorSetCheckedOptions { Force = true });
        await page.Locator("#CryptoCode").FillAsync("BTC");
        await page.Locator("#Network").SelectOptionAsync("regtest");
        await page.Locator("#ElectrumHost").FillAsync(electrumHost);
        await page.Locator("#ElectrumPort").FillAsync(electrumPort);
        await page.Locator("#RpcUrl").FillAsync(rpcUrl);
        await page.Locator("#GapLimit").FillAsync("100");
        await page.Locator("#DefaultRescanStartHeight").FillAsync("0");
        await page.Locator("#AutoRegisterDescriptors").SetCheckedAsync(true, new LocatorSetCheckedOptions { Force = true });
        await page.Locator("#AutoRescanOnNewDescriptor").SetCheckedAsync(true, new LocatorSetCheckedOptions { Force = true });
        await page.Locator("#UseFlorestaAsBitcoinBackend").SetCheckedAsync(true, new LocatorSetCheckedOptions { Force = true });
        await page.Locator("#SaveFlorestaSettings").ClickAsync();
    }

    private async Task<uint256> PayInvoiceAndWaitAsync(
        IPage page,
        string storeId,
        RPCClient bitcoin,
        RPCClient bitcoinWallet,
        string displayAmount,
        Money invoiceAmount)
    {
        var invoiceId = await CreateInvoice(page, storeId, displayAmount, "BTC");
        var invoiceUrl = new Uri(_server.ServerUri, $"/i/{invoiceId}").ToString();
        await page.GotoAsync(invoiceUrl, new PageGotoOptions { WaitUntil = WaitUntilState.Commit });
        await AssertNoUiError(page);

        var invoiceAddress = await page.Locator("#Address_BTC-CHAIN .truncate-center").GetAttributeAsync("data-text");
        Assert.False(string.IsNullOrWhiteSpace(invoiceAddress));

        var txId = await bitcoinWallet.SendToAddressAsync(BitcoinAddress.Create(invoiceAddress!, Network.RegTest), invoiceAmount);
        var rawTx = await bitcoin.GetRawTransactionAsync(txId);
        await FlorestaRpcAsync("sendrawtransaction", new object[] { rawTx.ToHex() });
        await bitcoinWallet.GenerateToAddressAsync(1, await bitcoinWallet.GetNewAddressAsync());
        await SubmitBitcoinChainToUtreexodAsync(bitcoin);
        await RefreshFlorestaUtreexoPeerAsync();
        await WaitForUtreexodChainAsync(await bitcoin.GetBlockCountAsync(), await GetBitcoinBestBlockHashAsync(bitcoin));
        await WaitForFlorestaChainAsync(await bitcoin.GetBlockCountAsync(), await GetBitcoinBestBlockHashAsync(bitcoin));
        try
        {
            await WaitForInvoiceSettledAsync(page, invoiceUrl);
        }
        catch (TimeoutException)
        {
            var strategy = GetWalletAccountKey();
            var walletRouteId = $"S-{storeId}-BTC";
            await StartWalletRescanAsync(page, walletRouteId);
            await WaitForPaymentCacheEntryAsync(strategy, txId);

            await bitcoinWallet.GenerateToAddressAsync(1, await bitcoinWallet.GetNewAddressAsync());
            await SubmitBitcoinChainToUtreexodAsync(bitcoin);
            await RefreshFlorestaUtreexoPeerAsync();
            await WaitForUtreexodChainAsync(await bitcoin.GetBlockCountAsync(), await GetBitcoinBestBlockHashAsync(bitcoin));
            await WaitForFlorestaChainAsync(await bitcoin.GetBlockCountAsync(), await GetBitcoinBestBlockHashAsync(bitcoin));
            await WaitForInvoiceSettledAsync(page, invoiceUrl);
        }

        return txId;
    }

    private async Task WaitForWalletTrackingReadyAsync(string strategy, int outputOffset)
    {
        await EventuallyAsync(async () =>
        {
            var output = _server.Output;
            var newOutput = output.Length > outputOffset ? output[outputOffset..] : string.Empty;
            Assert.True(
                newOutput.Contains("Now tracking wallet", StringComparison.Ordinal),
                $"BTCPay output did not show wallet tracking completion for the current import.\n{GetTail(output, 120)}");

            await using var connection = new NpgsqlConnection(GetE2ePostgresConnectionString());
            await connection.OpenAsync();
            var addressCount = await CountAsync(
                connection,
                "floresta.tracked_addresses",
                @"""WalletId"" = @walletId",
                strategy,
                string.Empty);
            Assert.True(
                addressCount >= FlorestaSettings.KnownWatchedDescriptorAddressCount * 2,
                $"Expected derived receive/change addresses for {strategy}, got {addressCount}");
        }, TimeSpan.FromSeconds(120), "Floresta wallet tracking to finish after wallet import");
    }

    private async Task WaitForInvoiceSettledAsync(IPage page, string invoiceUrl)
    {
        try
        {
            await EventuallyAsync(async () =>
            {
                await page.GotoAsync(invoiceUrl, new PageGotoOptions { WaitUntil = WaitUntilState.Commit });
                await AssertNoUiError(page);
                var settled = page.Locator("#settled");
                await settled.WaitForAsync(new LocatorWaitForOptions { Timeout = 5_000 });
                await Expect(settled).ToContainTextAsync("Invoice Paid", new LocatorAssertionsToContainTextOptions { Timeout = 5_000 });
            }, TimeSpan.FromSeconds(120), "invoice to become settled");
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"{ex.Message}\n\nBTCPay output tail:\n{GetTail(_server.Output, 120)}", ex);
        }
    }

    private static async Task WaitForPaymentCacheEntryAsync(string strategy, uint256 txId)
    {
        var txHash = txId.ToString();
        await EventuallyAsync(async () =>
        {
            var snapshot = await ReadWalletCacheSnapshotAsync(strategy, txHash);
            Assert.Equal(1, snapshot.PaymentTransactionCount);
            Assert.Equal(1, snapshot.PaymentUtxoCount);
            Assert.True(snapshot.PaymentUtxoValue > 0);
        }, TimeSpan.FromSeconds(120), $"Floresta wallet cache to contain payment {txHash}");
    }

    private static async Task AssertWalletTransactionVisibleAsync(
        IPage page,
        string walletUrl,
        uint256 txId,
        string? expectedText = null,
        bool amountShouldBeNegative = false)
    {
        await EventuallyAsync(async () =>
        {
            var response = await page.GotoAsync(walletUrl, new PageGotoOptions { WaitUntil = WaitUntilState.Commit });
            Assert.True(response?.Ok, $"Wallet page returned HTTP {response?.Status} at {page.Url}");
            await AssertNoUiError(page);

            var row = page.Locator($"tr.transaction-row[data-value='{txId}']");
            await Expect(row).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 5_000 });
            if (expectedText is not null)
                await Expect(row).ToContainTextAsync(expectedText, new LocatorAssertionsToContainTextOptions { Timeout = 5_000 });
            if (amountShouldBeNegative)
                await Expect(row.Locator(".amount-col")).ToContainTextAsync("-", new LocatorAssertionsToContainTextOptions { Timeout = 5_000 });
        }, TimeSpan.FromSeconds(120), $"wallet transaction {txId} to appear");
    }

    private async Task AssertWalletCacheCanBeRebuiltAsync(IPage page, string storeId, uint256 txId)
    {
        var walletRouteId = $"S-{storeId}-BTC";
        var strategy = GetWalletAccountKey();
        var txHash = txId.ToString();

        var before = await EventuallyAsync(async () =>
        {
            var snapshot = await ReadWalletCacheSnapshotAsync(strategy, txHash);
            Assert.Equal(FlorestaWalletDescriptorStatus.Registered, snapshot.DescriptorStatus);
            Assert.Equal(FlorestaWalletRescanStatus.Requested, snapshot.RescanStatus);
            Assert.True(snapshot.HasRescanRequestedAt);
            Assert.Equal(0, snapshot.RescanStartHeight);
            Assert.Null(snapshot.RescanError);
            Assert.True(snapshot.AddressCount > 0);
            Assert.True(snapshot.ReservedAddressCount > 0);
            Assert.True(snapshot.UsedAddressCount > 0);
            Assert.True(snapshot.UtxoCount > 0);
            Assert.True(snapshot.TransactionCount > 0);
            Assert.True(snapshot.PaymentTransactionCount > 0);
            Assert.True(snapshot.PaymentUtxoCount > 0);
            Assert.True(snapshot.PaymentUtxoValue > 0);
            return snapshot;
        }, TimeSpan.FromSeconds(60), "Floresta wallet cache to contain the paid invoice transaction");

        await WipeWalletCacheAsync(page, walletRouteId);

        await EventuallyAsync(async () =>
        {
            var snapshot = await ReadWalletCacheSnapshotAsync(strategy, txHash);
            Assert.Equal(before.AddressCount, snapshot.AddressCount);
            Assert.Equal(before.ReservedAddressCount, snapshot.ReservedAddressCount);
            Assert.Equal(before.UsedAddressCount, snapshot.UsedAddressCount);
            Assert.Equal(before.ReceiveGapIndex, snapshot.ReceiveGapIndex);
            Assert.Equal(before.ChangeGapIndex, snapshot.ChangeGapIndex);
            Assert.Equal(before.DescriptorStatus, snapshot.DescriptorStatus);
            Assert.Equal(before.RescanStatus, snapshot.RescanStatus);
            Assert.Equal(before.RescanStartHeight, snapshot.RescanStartHeight);
            Assert.Equal(before.RescanError, snapshot.RescanError);
            Assert.Equal(0, snapshot.UtxoCount);
            Assert.Equal(0, snapshot.TransactionCount);
        }, TimeSpan.FromSeconds(60), "Floresta wallet cache wipe to remove only transactions and UTXOs");

        await StartWalletRescanAsync(page, walletRouteId);

        WalletCacheSnapshot after;
        try
        {
            after = await EventuallyAsync(async () =>
            {
                var snapshot = await ReadWalletCacheSnapshotAsync(strategy, txHash);
                Assert.Equal(before.AddressCount, snapshot.AddressCount);
                Assert.Equal(before.ReservedAddressCount, snapshot.ReservedAddressCount);
                Assert.Equal(before.UsedAddressCount, snapshot.UsedAddressCount);
                Assert.Equal(before.ReceiveGapIndex, snapshot.ReceiveGapIndex);
                Assert.Equal(before.ChangeGapIndex, snapshot.ChangeGapIndex);
                Assert.Equal(before.DescriptorStatus, snapshot.DescriptorStatus);
                Assert.Equal(before.RescanStatus, snapshot.RescanStatus);
                Assert.Equal(before.RescanStartHeight, snapshot.RescanStartHeight);
                Assert.Equal(before.RescanError, snapshot.RescanError);
                Assert.Equal(before.TransactionCount, snapshot.TransactionCount);
                Assert.Equal(before.UtxoCount, snapshot.UtxoCount);
                Assert.Equal(1, snapshot.PaymentTransactionCount);
                Assert.Equal(1, snapshot.PaymentUtxoCount);
                Assert.Equal(before.PaymentUtxoValue, snapshot.PaymentUtxoValue);
                return snapshot;
            }, TimeSpan.FromSeconds(120), "Floresta wallet scan to rebuild the wiped payment cache");
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"{ex.Message}\n\nBTCPay output tail:\n{GetTail(_server.Output, 120)}", ex);
        }

        Assert.Equal(before.HasRescanRequestedAt, after.HasRescanRequestedAt);

        var walletUrl = new Uri(_server.ServerUri, $"/wallets/{walletRouteId}?loadTransactions=true").ToString();
        await EventuallyAsync(async () =>
        {
            var response = await page.GotoAsync(walletUrl, new PageGotoOptions { WaitUntil = WaitUntilState.Commit });
            Assert.True(response?.Ok, $"Wallet page returned HTTP {response?.Status} at {page.Url}");
            await AssertNoUiError(page);
            await Expect(page.Locator($"tr.transaction-row[data-value='{txHash}']"))
                .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 5_000 });
        }, TimeSpan.FromSeconds(60), "rebuilt payment transaction to reappear in the wallet UI");
    }

    private async Task AssertInterruptedWalletScanCanResumeAsync(
        IPage page,
        string storeId,
        int expectedHeight,
        params uint256[] txIds)
    {
        var walletRouteId = $"S-{storeId}-BTC";
        var strategy = GetWalletAccountKey();
        var txHashes = txIds.Select(txId => txId.ToString()).ToArray();

        var before = await EventuallyAsync(async () =>
        {
            var snapshot = await ReadWalletCacheSnapshotAsync(strategy, txHashes[0]);
            Assert.True(snapshot.TransactionCount >= txHashes.Length);
            Assert.True(snapshot.UtxoCount >= txHashes.Length);
            await AssertPaymentCacheEntriesAsync(strategy, txHashes, snapshot.TransactionCount, snapshot.UtxoCount);
            return snapshot;
        }, TimeSpan.FromSeconds(60), "Floresta wallet cache to contain all paid invoice transactions before interrupted scan");

        await WipeWalletCacheAsync(page, walletRouteId);
        await EventuallyAsync(async () =>
        {
            var snapshot = await ReadWalletCacheSnapshotAsync(strategy, txHashes[0]);
            AssertWalletShapePreserved(before, snapshot);
            Assert.Equal(0, snapshot.UtxoCount);
            Assert.Equal(0, snapshot.TransactionCount);
        }, TimeSpan.FromSeconds(60), "Floresta wallet cache wipe before interrupted scan");

        await StartWalletRescanAsync(page, walletRouteId);
        await _server.RestartAsync();
        await WaitForBtcpayFlorestaBackendAsync(page, expectedHeight);
        await StartWalletRescanAsync(page, walletRouteId);

        await EventuallyAsync(async () =>
        {
            var snapshot = await ReadWalletCacheSnapshotAsync(strategy, txHashes[0]);
            AssertWalletShapePreserved(before, snapshot);
            Assert.Equal(before.TransactionCount, snapshot.TransactionCount);
            Assert.Equal(before.UtxoCount, snapshot.UtxoCount);
            await AssertPaymentCacheEntriesAsync(strategy, txHashes, before.TransactionCount, before.UtxoCount);
        }, TimeSpan.FromSeconds(120), "interrupted Floresta wallet scan to rebuild after BTCPay restart");
    }

    private static async Task AssertWalletSnapshotPreservedAsync(
        string strategy,
        uint256 txId,
        WalletCacheSnapshot expected)
    {
        var actual = await ReadWalletCacheSnapshotAsync(strategy, txId.ToString());
        AssertWalletShapePreserved(expected, actual);
        Assert.Equal(expected.TransactionCount, actual.TransactionCount);
        Assert.Equal(expected.UtxoCount, actual.UtxoCount);
        Assert.Equal(expected.PaymentTransactionCount, actual.PaymentTransactionCount);
        Assert.Equal(expected.PaymentUtxoCount, actual.PaymentUtxoCount);
        Assert.Equal(expected.PaymentUtxoValue, actual.PaymentUtxoValue);
    }

    private static async Task AssertPaymentCacheEntriesAsync(
        string strategy,
        string[] txHashes,
        int expectedTransactionCount,
        int expectedUtxoCount)
    {
        foreach (var txHash in txHashes)
        {
            var snapshot = await ReadWalletCacheSnapshotAsync(strategy, txHash);
            Assert.Equal(expectedTransactionCount, snapshot.TransactionCount);
            Assert.Equal(expectedUtxoCount, snapshot.UtxoCount);
            Assert.Equal(1, snapshot.PaymentTransactionCount);
            Assert.Equal(1, snapshot.PaymentUtxoCount);
            Assert.True(snapshot.PaymentUtxoValue > 0);
        }
    }

    private static async Task AssertKnownPaymentsRemainActiveAsync(string strategy, params uint256[] txIds)
    {
        var txHashes = txIds.Select(txId => txId.ToString()).ToArray();
        await EventuallyAsync(async () =>
        {
            var snapshot = await ReadWalletCacheSnapshotAsync(strategy, txHashes[0]);
            Assert.True(snapshot.TransactionCount >= txHashes.Length);
            Assert.True(snapshot.UtxoCount >= txHashes.Length);
            await AssertPaymentCacheEntriesAsync(strategy, txHashes, snapshot.TransactionCount, snapshot.UtxoCount);
        }, TimeSpan.FromSeconds(120), "known payment transactions to remain active after Floresta restart");
    }

    private static void AssertWalletShapePreserved(WalletCacheSnapshot expected, WalletCacheSnapshot actual)
    {
        Assert.Equal(expected.AddressCount, actual.AddressCount);
        Assert.Equal(expected.ReservedAddressCount, actual.ReservedAddressCount);
        Assert.Equal(expected.UsedAddressCount, actual.UsedAddressCount);
        Assert.Equal(expected.ReceiveGapIndex, actual.ReceiveGapIndex);
        Assert.Equal(expected.ChangeGapIndex, actual.ChangeGapIndex);
        Assert.Equal(expected.DescriptorStatus, actual.DescriptorStatus);
        Assert.Equal(expected.RescanStatus, actual.RescanStatus);
        Assert.Equal(expected.HasRescanRequestedAt, actual.HasRescanRequestedAt);
        Assert.Equal(expected.RescanStartHeight, actual.RescanStartHeight);
        Assert.Equal(expected.RescanError, actual.RescanError);
    }

    private async Task WipeWalletCacheAsync(IPage page, string walletRouteId)
    {
        await page.GotoAsync(new Uri(_server.ServerUri, $"/wallets/{walletRouteId}/transactions?loadTransactions=true").ToString(),
            new PageGotoOptions { WaitUntil = WaitUntilState.Commit });
        await AssertNoUiError(page);
        await page.EvaluateAsync("""
            () => {
                const form = document.getElementById('WalletActions');
                if (!form) throw new Error('WalletActions form not found');
                const input = document.createElement('input');
                input.type = 'hidden';
                input.name = 'command';
                input.value = 'clear';
                form.appendChild(input);
                form.submit();
            }
            """);
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = 30_000 });
        await AssertNoUiError(page);
    }

    private async Task StartWalletRescanAsync(IPage page, string walletRouteId)
    {
        await page.GotoAsync(new Uri(_server.ServerUri, $"/wallets/{walletRouteId}/rescan").ToString(),
            new PageGotoOptions { WaitUntil = WaitUntilState.Commit });
        await AssertNoUiError(page);
        await page.Locator("#StartingIndex").FillAsync("0");
        await page.Locator("#GapLimit").FillAsync("100");
        await page.Locator("#BatchSize").FillAsync("100");
        await page.EvaluateAsync("""
            () => {
                const button = Array.from(document.querySelectorAll('button'))
                    .find(b => b.textContent?.trim() === 'Start scan');
                const form = button?.closest('form');
                if (!form) throw new Error('Wallet rescan form not found');
                form.submit();
            }
            """);
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = 30_000 });
        await AssertNoUiError(page);
    }

    private static async Task<WalletCacheSnapshot> ReadWalletCacheSnapshotAsync(string strategy, string txId)
    {
        await using var connection = new NpgsqlConnection(GetE2ePostgresConnectionString());
        await connection.OpenAsync();

        await using var walletCommand = new NpgsqlCommand("""
            SELECT
                "ReceiveGapIndex",
                "ChangeGapIndex",
                "DescriptorStatus",
                "RescanStatus",
                "RescanRequestedAt" IS NOT NULL,
                "RescanStartHeight",
                "RescanError"
            FROM floresta.tracked_wallets
            WHERE "Id" = @walletId
            """, connection);
        walletCommand.Parameters.AddWithValue("walletId", strategy);

        await using var reader = await walletCommand.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"Floresta tracked wallet was not found for {strategy}");
        var receiveGapIndex = reader.GetInt32(0);
        var changeGapIndex = reader.GetInt32(1);
        var descriptorStatus = reader.GetString(2);
        var rescanStatus = reader.GetString(3);
        var hasRescanRequestedAt = reader.GetBoolean(4);
        var rescanStartHeight = reader.IsDBNull(5) ? (int?)null : reader.GetInt32(5);
        var rescanError = reader.IsDBNull(6) ? null : reader.GetString(6);
        await reader.DisposeAsync();

        return new WalletCacheSnapshot(
            receiveGapIndex,
            changeGapIndex,
            descriptorStatus,
            rescanStatus,
            hasRescanRequestedAt,
            rescanStartHeight,
            rescanError,
            await CountAsync(connection, "floresta.tracked_addresses", @"""WalletId"" = @walletId", strategy, txId),
            await CountAsync(connection, "floresta.tracked_addresses", @"""WalletId"" = @walletId AND ""IsReserved""", strategy, txId),
            await CountAsync(connection, "floresta.tracked_addresses", @"""WalletId"" = @walletId AND ""IsUsed""", strategy, txId),
            await CountAsync(connection, "floresta.utxos", @"""WalletId"" = @walletId", strategy, txId),
            await CountAsync(connection, "floresta.utxos", @"""WalletId"" = @walletId AND ""Txid"" = @txId AND NOT ""IsSpent""", strategy, txId),
            await SumAsync(connection, "floresta.utxos", @"""Value""", @"""WalletId"" = @walletId AND ""Txid"" = @txId AND NOT ""IsSpent""", strategy, txId),
            await CountAsync(connection, "floresta.transactions", @"""WalletId"" = @walletId", strategy, txId),
            await CountAsync(connection, "floresta.transactions", @"""WalletId"" = @walletId AND ""Txid"" = @txId AND ""RemovedAt"" IS NULL", strategy, txId));
    }

    private static async Task<int> CountAsync(
        NpgsqlConnection connection,
        string table,
        string where,
        string walletId,
        string txId)
    {
        await using var command = new NpgsqlCommand($@"SELECT COUNT(*) FROM {table} WHERE {where}", connection);
        command.Parameters.AddWithValue("walletId", walletId);
        command.Parameters.AddWithValue("txId", txId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<long> SumAsync(
        NpgsqlConnection connection,
        string table,
        string column,
        string where,
        string walletId,
        string txId)
    {
        await using var command = new NpgsqlCommand($@"SELECT COALESCE(SUM({column}), 0) FROM {table} WHERE {where}", connection);
        command.Parameters.AddWithValue("walletId", walletId);
        command.Parameters.AddWithValue("txId", txId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static string GetE2ePostgresConnectionString()
    {
        return Environment.GetEnvironmentVariable("E2E_POSTGRES") ??
               "User ID=postgres;Password=postgres;Include Error Detail=true;Host=127.0.0.1;Port=39372;Database=btcpayserver_floresta_e2e";
    }

    private static string GetTail(string text, int lineCount)
    {
        var lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return string.Join('\n', lines.TakeLast(lineCount));
    }

    private async Task<string> CreateStore(IPage page)
    {
        await page.GotoAsync(new Uri(_server.ServerUri, "/stores/create").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.Commit });
        await AssertNoUiError(page);
        await page.Locator("#Name").FillAsync("Floresta payment E2E " + Guid.NewGuid().ToString("N")[..8]);
        await page.Locator("#Create").ClickAsync();
        await Expect(page).ToHaveURLAsync(new Regex(".*/stores/[^/?#]+.*", RegexOptions.IgnoreCase));
        var match = Regex.Match(page.Url, @"/stores/([^/?#]+)");
        Assert.True(match.Success, $"Could not extract store id from URL {page.Url}");
        return match.Groups[1].Value;
    }

    private async Task ConfigureStoreToSettleUnconfirmed(IPage page, string storeId)
    {
        await page.GotoAsync(new Uri(_server.ServerUri, $"/stores/{storeId}/settings").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.Commit });
        await AssertNoUiError(page);
        await page.Locator("#SpeedPolicy").SelectOptionAsync("0");
        await page.Locator("#page-primary").ClickAsync();
        await Expect(page.Locator(".alert-success")).ToContainTextAsync("Store successfully updated", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
    }

    private async Task AddWatchOnlyWallet(IPage page, string storeId)
    {
        await page.GotoAsync(new Uri(_server.ServerUri, $"/stores/{storeId}/onchain/BTC").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.Commit });
        await AssertNoUiError(page);
        await page.Locator("#ImportWalletOptionsLink").ClickAsync();
        await page.Locator("#ImportXpubLink").ClickAsync();
        await page.Locator("#DerivationScheme").FillAsync(GetWalletDerivationScheme());
        await page.Locator("#Continue").ClickAsync();
        var confirm = page.Locator("#Confirm");
        await Expect(confirm).ToBeVisibleAsync();
        await confirm.EvaluateAsync("button => button.click()");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = 30_000 });
        await Expect(page.Locator(".alert-success")).ToContainTextAsync("Wallet settings for BTC have been updated", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
    }

    private static string GetWalletDerivationScheme()
    {
        var rootKey = new Mnemonic(WalletSeedMnemonic).DeriveExtKey();
        var fingerprint = rootKey.GetPublicKey().GetHDFingerPrint();
        return $"wpkh([{fingerprint}/{WalletAccountKeyPath}]{GetWalletAccountKey()}/<0;1>/*)";
    }

    private static string GetWalletAccountKey()
    {
        var rootKey = new Mnemonic(WalletSeedMnemonic).DeriveExtKey();
        var accountKey = rootKey.Derive(WalletAccountKeyPath).Neuter().GetWif(Network.RegTest);
        return accountKey.ToString();
    }

    private async Task<uint256> SpendFromBtcpayWalletWithSeed(IPage page, string storeId, RPCClient bitcoinWallet)
    {
        var destination = await bitcoinWallet.GetNewAddressAsync();
        await page.GotoAsync(new Uri(_server.ServerUri, $"/wallets/S-{storeId}-BTC/send").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.Commit });
        await AssertNoUiError(page);

        await page.Locator("#Outputs_0__DestinationAddress").FillAsync(destination.ToString());
        await page.Locator("#Outputs_0__Amount").FillAsync("0.00040000");
        await page.Locator("#FeeSatoshiPerByte").FillAsync("1");
        await page.Locator("#SignTransaction").ClickAsync();
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = 30_000 });
        await AssertNoUiError(page);

        await page.Locator("#SignWithSeed").ClickAsync();
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = 30_000 });
        await AssertNoUiError(page);

        await page.Locator("#SeedOrKey").FillAsync(WalletSeedMnemonic);
        await page.Locator("#Submit").ClickAsync();
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = 30_000 });
        await AssertNoUiError(page);

        var broadcast = page.Locator("#BroadcastTransaction");
        await Expect(broadcast).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
        await broadcast.ClickAsync();
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = 30_000 });
        await AssertNoUiError(page);

        var success = page.Locator(".alert-success").First;
        await Expect(success).ToContainTextAsync("Transaction broadcasted successfully", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        var successText = await success.InnerTextAsync();
        var match = Regex.Match(successText, @"\b[0-9a-f]{64}\b", RegexOptions.IgnoreCase);
        Assert.True(match.Success, $"Could not extract broadcast transaction id from success message: {successText}");
        return uint256.Parse(match.Value);
    }

    private async Task<string> CreateInvoice(IPage page, string storeId, string amount, string currency)
    {
        await page.GotoAsync(new Uri(_server.ServerUri, $"/stores/{storeId}/invoices/create").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.Commit });
        await AssertNoUiError(page);
        await page.Locator("#Amount").FillAsync(amount);
        await page.Locator("#Currency").ClearAsync();
        await page.Locator("#Currency").FillAsync(currency);
        await page.Locator("#page-primary").ClickAsync();

        await AssertNoUiError(page);
        var status = page.Locator(".alert-success, .alert-danger").First;
        await status.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        var statusText = await status.InnerTextAsync();
        Assert.DoesNotContain("alert-danger", await status.GetAttributeAsync("class") ?? string.Empty);

        var match = Regex.Match(statusText, @"Invoice (\w+) just created!", RegexOptions.IgnoreCase);
        Assert.True(match.Success, $"Could not extract invoice id from status message at {page.Url}: {statusText}");
        return match.Groups[1].Value;
    }

    private static async Task EventuallyAsync(Func<Task> action, TimeSpan timeout, string description)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                await action();
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                await Task.Delay(1_000);
            }
        }

        throw new TimeoutException($"Timed out waiting for {description}. Last error: {lastError?.Message}", lastError);
    }

    private static async Task<T> EventuallyAsync<T>(Func<Task<T>> action, TimeSpan timeout, string description)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                return await action();
            }
            catch (Exception ex)
            {
                lastError = ex;
                await Task.Delay(1_000);
            }
        }

        throw new TimeoutException($"Timed out waiting for {description}. Last error: {lastError?.Message}", lastError);
    }

    private sealed record HistoricalPayment(string Descriptor, string ScriptHash, uint256 TxId, int Height);

    private sealed record WalletCacheSnapshot(
        int ReceiveGapIndex,
        int ChangeGapIndex,
        string DescriptorStatus,
        string RescanStatus,
        bool HasRescanRequestedAt,
        int? RescanStartHeight,
        string? RescanError,
        int AddressCount,
        int ReservedAddressCount,
        int UsedAddressCount,
        int UtxoCount,
        int PaymentUtxoCount,
        long PaymentUtxoValue,
        int TransactionCount,
        int PaymentTransactionCount);

    private static async Task AssertNoUiError(IPage page)
    {
        var errorPageMarker = page.Locator("[data-testid='ui-error-page']");
        Assert.Equal(0, await errorPageMarker.CountAsync());

        var visibleDanger = page.Locator(".alert-danger:visible, .validation-summary-errors:visible");
        if (await visibleDanger.CountAsync() > 0)
        {
            var text = await visibleDanger.First.InnerTextAsync();
            Assert.Fail($"Unexpected UI error at {page.Url}: {text}");
        }
    }
}
