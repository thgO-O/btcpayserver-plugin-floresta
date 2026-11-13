using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Floresta.Data;
using BTCPayServer.Plugins.Floresta.Services;
using BTCPayServer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;

namespace BTCPayServer.Plugins.Floresta;

public class FlorestaWalletTracker : IHostedService
{
    private readonly record struct WalletPreparationKey(long ConnectionGeneration,
        bool AutoRegisterDescriptors, bool AutoRescanOnNewDescriptor, int? RescanStartHeight,
        int GapLimit, string RpcUrl, string CryptoCode);

    internal const string ScanCanceledError = "Scan canceled.";
    internal const string ScanPartialError =
        "One or more wallet addresses could not be synchronized. Check the server logs and try the scan again.";

    internal sealed record WalletPreparationContext(
        FlorestaSettings Settings,
        FlorestaDescriptorRegistrationResult DescriptorRegistration,
        DateTimeOffset? DescriptorRegisteredAt);

    private sealed class WalletPreparationFlight
    {
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WalletPreparationContext Result { get; set; }
        public Exception Error { get; set; }
    }

    private readonly FlorestaElectrumClient _client;
    private readonly FlorestaFeeProvider _feeProvider;
    private readonly FlorestaRpcClient _rpcClient;
    private readonly FlorestaDescriptorService _descriptorService;
    private readonly FlorestaDescriptorRegistry _descriptorRegistry;
    private readonly FlorestaDbContextFactory _dbFactory;
    private readonly SettingsRepository _settingsRepository;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly ILogger<FlorestaWalletTracker> _logger;
    private readonly FlorestaAddressPool _addressPool;
    private readonly FlorestaUtxoCache _utxoCache;
    private readonly FlorestaWalletSync _walletSync;
    private readonly FlorestaTransactionBroadcaster _broadcaster;
    private readonly FlorestaBackgroundTasks _backgroundTasks = new();

    private readonly ConcurrentDictionary<string, DerivationStrategyBase> _trackedStrategies = new();
    private readonly ConcurrentDictionary<string, ScanUTXOInformation> _scanStates = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Exception>> _trackingPreparations = new();
    private readonly ConcurrentDictionary<string, WalletPreparationFlight> _walletPreparations = new();
    private readonly ConcurrentDictionary<string, WalletPreparationKey> _preparedWallets = new();
    private Network _network;
    private DerivationStrategyFactory _derivationFactory;
    private int _tipHeight;

    public FlorestaWalletTracker(
        FlorestaElectrumClient client,
        FlorestaFeeProvider feeProvider,
        FlorestaRpcClient rpcClient,
        FlorestaDescriptorService descriptorService,
        FlorestaDescriptorRegistry descriptorRegistry,
        FlorestaDbContextFactory dbFactory,
        SettingsRepository settingsRepository,
        BTCPayNetworkProvider networkProvider,
        ILogger<FlorestaWalletTracker> logger)
    {
        _client = client;
        _feeProvider = feeProvider;
        _rpcClient = rpcClient;
        _descriptorService = descriptorService;
        _descriptorRegistry = descriptorRegistry;
        _dbFactory = dbFactory;
        _settingsRepository = settingsRepository;
        _networkProvider = networkProvider;
        _logger = logger;
        ConfigureNetwork();
        _addressPool = new FlorestaAddressPool(
            _dbFactory,
            _network,
            ParseStrategy,
            _ => GetSettingsAsync(),
            _trackedStrategies);
        _utxoCache = new FlorestaUtxoCache(_client, _network, _logger);
        _walletSync = new FlorestaWalletSync(
            _dbFactory,
            _client,
            _rpcClient,
            _addressPool,
            _utxoCache,
            _trackedStrategies,
            () => Volatile.Read(ref _tipHeight),
            _network,
            _logger);
        _broadcaster = new FlorestaTransactionBroadcaster(_rpcClient, _client);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) =>
        _backgroundTasks.StopAsync(cancellationToken);

    private void ConfigureNetwork()
    {
        var settings = new FlorestaSettings();
        var btcNetwork = _networkProvider.GetNetwork<BTCPayNetwork>(settings.CryptoCode ?? "BTC");
        if (btcNetwork == null)
            return;

        _network = btcNetwork.NBitcoinNetwork;
        _derivationFactory = new DerivationStrategyFactory(_network);
    }

    public void SetTipHeight(int height)
    {
        Volatile.Write(ref _tipHeight, height);
    }

    public async Task<bool> InitializeAsync(CancellationToken ct, bool isInitialBlockDownload = false)
    {
        List<(string walletId, List<TrackedAddress> addresses)> walletsToSubscribe;
        await using (var ctx = _dbFactory.CreateContext())
        {
            var tipState = await ctx.SyncStates.FindAsync(new object[] { "tip_height" }, ct);
            if (tipState != null && int.TryParse(tipState.Value, out var savedTip))
                SetTipHeight(savedTip);

            var wallets = await ctx.TrackedWallets.ToListAsync(ct);
            walletsToSubscribe = new();

            foreach (var wallet in wallets)
            {
                if (!string.IsNullOrWhiteSpace(wallet.DerivationStrategy))
                {
                    var strategy = ParseStrategy(wallet.DerivationStrategy);
                    if (strategy != null)
                        _trackedStrategies[wallet.Id] = strategy;
                }

                var addresses = await GetTrackedAddressSourcesAsync(ctx, wallet.Id, ct);
                walletsToSubscribe.Add((wallet.Id, addresses));
            }

            _logger.LogInformation("Loaded {Count} tracked wallets from DB", wallets.Count);
        }

        // A pass that starts in IBD must be followed by a ready-node pass,
        // including preparation, even if IBD ends during wallet synchronization.
        var synchronizationDeferred = isInitialBlockDownload;
        var settings = await GetSettingsAsync();
        var preparationKey = CreatePreparationKey(_client.ConnectionGeneration, settings);
        foreach (var (walletId, addresses) in walletsToSubscribe)
        {
            var prepared = false;
            try
            {
                var currentAddresses = addresses;
                // Keep discovering new/failed wallets during IBD while reusing
                // successful preparation only for this Electrum connection.
                if (!isInitialBlockDownload ||
                    !_preparedWallets.TryGetValue(walletId, out var preparedKey) ||
                    preparedKey != preparationKey)
                {
                    var appliedKey = preparationKey;
                    if (_trackedStrategies.ContainsKey(walletId))
                    {
                        var preparation = await PrepareWalletTrackingCoreAsync(walletId, ct);
                        currentAddresses = preparation.Addresses;
                        appliedKey = CreatePreparationKey(preparationKey.ConnectionGeneration, preparation.Settings);
                    }

                    await _walletSync.SubscribeTrackedAddressesAsync(currentAddresses, ct);
                    _preparedWallets[walletId] = appliedKey;
                }
                prepared = true;
                var result = await _walletSync.SyncWalletStateWithResultAsync(walletId, currentAddresses, ct);
                synchronizationDeferred |= result.Deferred;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (!prepared)
                    _preparedWallets.TryRemove(walletId, out _);
                _logger.LogWarning(ex, "Failed to sync wallet state for wallet {WalletId}", LogSafeId.Hash(walletId));
            }
        }

        _logger.LogInformation("Initialized {Count} tracked wallets", walletsToSubscribe.Count);
        return synchronizationDeferred;
    }

    internal async Task<bool> HasPendingDescriptorRescansAsync(CancellationToken ct)
    {
        await using var ctx = _dbFactory.CreateContext();
        return await ctx.TrackedWallets.AnyAsync(
            wallet => wallet.RescanStatus == FlorestaWalletRescanStatus.Pending, ct);
    }

    internal async Task RetryPendingDescriptorRescansAsync(CancellationToken ct)
    {
        List<string> pendingWallets;
        await using (var ctx = _dbFactory.CreateContext())
        {
            pendingWallets = await ctx.TrackedWallets
                .Where(wallet => wallet.RescanStatus == FlorestaWalletRescanStatus.Pending)
                .Select(wallet => wallet.Id).ToListAsync(ct);
        }

        foreach (var walletId in pendingWallets)
        {
            try
            {
                await PrepareAndStartTrackingAsync(walletId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to retry descriptor rescan for wallet {WalletId}", LogSafeId.Hash(walletId));
            }
        }
    }

    public async Task TrackWalletAsync(string strategyStr, CancellationToken ct)
    {
        var addresses = await PrepareWalletTrackingAsync(strategyStr, ct);
        await SubscribeAndSyncWalletAsync(strategyStr, addresses, ct);
    }

    public async Task<List<TrackedAddress>> PrepareWalletTrackingAsync(string strategyStr, CancellationToken ct)
    {
        return (await PrepareWalletTrackingCoreAsync(strategyStr, ct)).Addresses;
    }

    private static WalletPreparationKey CreatePreparationKey(long generation, FlorestaSettings settings) =>
        new(generation, settings.AutoRegisterDescriptors, settings.AutoRescanOnNewDescriptor,
            settings.DefaultRescanStartHeight, settings.GapLimit, settings.RpcUrl, settings.CryptoCode);

    private async Task<(List<TrackedAddress> Addresses, FlorestaSettings Settings)> PrepareWalletTrackingCoreAsync(
        string strategyStr, CancellationToken ct)
    {
        var preparation = await PrepareWalletAsync(strategyStr, ct);

        // Phase 1: derive addresses locally (fast, no network)
        var addresses = await _addressPool.EnsureAddressesDerivedAsync(
            strategyStr,
            preparation.Settings,
            preparation.DescriptorRegistration,
            preparation.DescriptorRegisteredAt,
            null,
            ct);
        if (addresses == null)
            addresses = await GetTrackedAddressesAsync(strategyStr, ct);

        return (addresses, preparation.Settings);
    }

    private Task<WalletPreparationContext> PrepareWalletAsync(string strategyStr, CancellationToken ct)
    {
        return GetOrStartWalletPreparationAsync(
            strategyStr,
            token => PrepareWalletCoreAsync(strategyStr, token),
            ct);
    }

    internal async Task<WalletPreparationContext> GetOrStartWalletPreparationAsync(
        string strategyStr,
        Func<CancellationToken, Task<WalletPreparationContext>> prepare,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var candidate = new WalletPreparationFlight();
        var preparation = _walletPreparations.GetOrAdd(strategyStr, candidate);
        if (ReferenceEquals(candidate, preparation))
        {
            var task = _backgroundTasks.TryRun(
                GetWalletPreparationJobKey(strategyStr),
                token => ExecuteWalletPreparationAsync(preparation, prepare, token),
                onCompleted: () => CompleteWalletPreparation(strategyStr, preparation));
            if (task is null)
            {
                preparation.Error = new InvalidOperationException("Floresta wallet tracker is stopping.");
                RemoveWalletPreparation(strategyStr, preparation);
                preparation.Completion.TrySetResult();
            }
        }

        await preparation.Completion.Task.WaitAsync(ct);
        if (preparation.Error is not null)
            ExceptionDispatchInfo.Capture(preparation.Error).Throw();

        return preparation.Result;
    }

    private async Task<WalletPreparationContext> PrepareWalletCoreAsync(
        string strategyStr,
        CancellationToken ct)
    {
        var settings = await GetSettingsAsync();
        var descriptorRegistration = await PrepareDescriptorRegistrationAsync(strategyStr, settings, ct);
        if (!descriptorRegistration.Succeeded)
        {
            await UpsertWalletDescriptorMetadataAsync(strategyStr, settings, descriptorRegistration, null,
                descriptorRegistration.Error, ct,
                !settings.AutoRegisterDescriptors || !settings.AutoRescanOnNewDescriptor
                    ? FlorestaWalletDescriptorMetadata.ApplyRescanNotRequested : null);
            throw new InvalidOperationException($"Floresta descriptor registration failed: {descriptorRegistration.Error}");
        }

        var descriptorRegisteredAt = settings.AutoRegisterDescriptors && descriptorRegistration.Registered > 0
            ? DateTimeOffset.UtcNow
            : (DateTimeOffset?)null;
        await UpsertWalletDescriptorMetadataAsync(strategyStr, settings, descriptorRegistration, descriptorRegisteredAt,
            null, ct);
        await StartDescriptorRescanIfNeededAsync(strategyStr, settings, descriptorRegistration, ct);

        return new WalletPreparationContext(settings, descriptorRegistration, descriptorRegisteredAt);
    }

    private static async Task ExecuteWalletPreparationAsync(
        WalletPreparationFlight preparation,
        Func<CancellationToken, Task<WalletPreparationContext>> prepare,
        CancellationToken ct)
    {
        try
        {
            preparation.Result = await prepare(ct);
        }
        catch (Exception ex)
        {
            preparation.Error = ex;
        }
    }

    private void CompleteWalletPreparation(
        string strategyStr,
        WalletPreparationFlight preparation)
    {
        RemoveWalletPreparation(strategyStr, preparation);
        preparation.Completion.TrySetResult();
    }

    private void RemoveWalletPreparation(
        string strategyStr,
        WalletPreparationFlight preparation)
    {
        if (_walletPreparations.TryGetValue(strategyStr, out var current) &&
            ReferenceEquals(current, preparation))
        {
            _walletPreparations.TryRemove(strategyStr, out _);
        }
    }

    public async Task<List<NewTransactionInfo>> TrackAddressAsync(string addressText, CancellationToken ct)
    {
        if (_network == null)
            throw new InvalidOperationException("Bitcoin network is not configured.");

        var address = BitcoinAddress.Create(addressText, _network);
        var walletId = address.ToString();
        var script = address.ScriptPubKey;
        var scripthash = ScriptHashUtility.ComputeScriptHash(script);
        var trackedAddress = new TrackedAddress
        {
            Scripthash = scripthash,
            WalletId = walletId,
            ScriptPubKey = script.ToBytes(),
            Address = walletId,
            IsChange = false,
            IsReserved = false
        };

        await using (var ctx = _dbFactory.CreateContext())
        {
            var wallet = await ctx.TrackedWallets.FindAsync(new object[] { walletId }, ct);
            if (wallet == null)
            {
                ctx.TrackedWallets.Add(new TrackedWallet
                {
                    Id = walletId,
                    CryptoCode = "BTC",
                    DerivationStrategy = null,
                    ReceiveGapIndex = 0,
                    ChangeGapIndex = 0
                });
            }

            var trackedScript = await ctx.TrackedAddresses.FindAsync(new object[] { scripthash }, ct);
            if (trackedScript == null)
            {
                ctx.TrackedAddresses.Add(trackedAddress);
                trackedScript = trackedAddress;
            }
            else if (trackedScript.WalletId == walletId)
            {
                trackedScript.ScriptPubKey = trackedAddress.ScriptPubKey;
                trackedScript.Address = trackedAddress.Address;
            }
            else
            {
                _logger.LogWarning(
                    "Address source {Address} shares scripthash {Scripthash} with existing wallet {WalletId}; linking an additional owner",
                    walletId,
                    scripthash,
                    LogSafeId.Hash(trackedScript.WalletId));
            }

            var owner = await ctx.TrackedAddressOwners.FindAsync(new object[] { walletId, scripthash }, ct);
            if (owner == null)
            {
                ctx.TrackedAddressOwners.Add(new TrackedAddressOwner
                {
                    WalletId = walletId,
                    Scripthash = scripthash
                });
            }

            await ctx.SaveChangesAsync(ct);
            trackedAddress = CreateAddressSource(trackedScript, walletId);
        }

        await _walletSync.SubscribeTrackedAddressesAsync(new[] { trackedAddress }, ct);
        return await _walletSync.SyncWalletStateAsync(trackedAddress.WalletId, new List<TrackedAddress> { trackedAddress }, ct);
    }

    public async Task SubscribeAndSyncWalletAsync(
        string strategyStr,
        List<TrackedAddress> addresses,
        CancellationToken ct)
    {
        addresses ??= await GetTrackedAddressesAsync(strategyStr, ct);

        await _walletSync.SubscribeTrackedAddressesAsync(addresses, ct);
        await _walletSync.SyncWalletStateAsync(strategyStr, addresses, ct);
        _logger.LogInformation("Now tracking wallet {WalletId}", LogSafeId.Hash(strategyStr));
    }

    public Task PrepareAndStartTrackingAsync(string strategyStr, CancellationToken ct)
    {
        return PrepareAndStartTrackingAsync(
            strategyStr,
            token => PrepareWalletTrackingAsync(strategyStr, token),
            (addresses, token) => SubscribeAndSyncWalletAsync(strategyStr, addresses, token),
            ct);
    }

    internal async Task PrepareAndStartTrackingAsync(
        string strategyStr,
        Func<CancellationToken, Task<List<TrackedAddress>>> prepare,
        Func<List<TrackedAddress>, CancellationToken, Task> sync,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var candidate = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparation = _trackingPreparations.GetOrAdd(strategyStr, candidate);
        var ownsPreparation = ReferenceEquals(candidate, preparation);

        if (ownsPreparation)
        {
            var task = _backgroundTasks.TryRun(
                GetTrackingJobKey(strategyStr),
                token => RunTrackingJobAsync(strategyStr, preparation, prepare, sync, token),
                onCompleted: () => RemoveTrackingPreparation(strategyStr, preparation));
            if (task is null)
            {
                RemoveTrackingPreparation(strategyStr, preparation);
                preparation.TrySetResult(new InvalidOperationException("Floresta wallet tracker is stopping."));
            }
        }

        var error = await preparation.Task.WaitAsync(ct);
        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static string GetTrackingJobKey(string strategyStr) => $"tracking:{strategyStr}";
    private static string GetWalletPreparationJobKey(string strategyStr) => $"preparation:{strategyStr}";

    private async Task RunTrackingJobAsync(
        string strategyStr,
        TaskCompletionSource<Exception> preparation,
        Func<CancellationToken, Task<List<TrackedAddress>>> prepare,
        Func<List<TrackedAddress>, CancellationToken, Task> sync,
        CancellationToken ct)
    {
        try
        {
            var addresses = await prepare(ct);
            ct.ThrowIfCancellationRequested();
            preparation.TrySetResult(null);
            await sync(addresses, ct);
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            preparation.TrySetResult(ex);
            _logger.LogDebug("Background tracking canceled for wallet {WalletId}", LogSafeId.Hash(strategyStr));
        }
        catch (Exception ex)
        {
            var failedDuringPreparation = preparation.TrySetResult(ex);
            _logger.LogError(
                ex,
                failedDuringPreparation
                    ? "Wallet preparation failed for wallet {WalletId}"
                    : "Background tracking failed for wallet {WalletId}",
                LogSafeId.Hash(strategyStr));
        }
    }

    private void RemoveTrackingPreparation(
        string strategyStr,
        TaskCompletionSource<Exception> preparation)
    {
        if (_trackingPreparations.TryGetValue(strategyStr, out var current) &&
            ReferenceEquals(current, preparation))
        {
            _trackingPreparations.TryRemove(strategyStr, out _);
        }
    }

    private async Task<List<TrackedAddress>> GetTrackedAddressesAsync(string strategyStr, CancellationToken ct)
    {
        await using var ctx = _dbFactory.CreateContext();
        return await GetTrackedAddressSourcesAsync(ctx, strategyStr, ct);
    }

    internal static async Task<List<TrackedAddress>> GetTrackedAddressSourcesAsync(
        FlorestaDbContext ctx,
        string walletId,
        CancellationToken ct)
    {
        var primary = await ctx.TrackedAddresses
            .Where(a => a.WalletId == walletId)
            .ToListAsync(ct);

        var owned = await ctx.TrackedAddressOwners
            .Where(o => o.WalletId == walletId)
            .Join(
                ctx.TrackedAddresses,
                owner => owner.Scripthash,
                address => address.Scripthash,
                (_, address) => address)
            .ToListAsync(ct);

        return primary
            .Concat(owned.Select(a => CreateAddressSource(a, walletId)))
            .GroupBy(a => a.Scripthash)
            .Select(g => g.First())
            .ToList();
    }

    internal static async Task<List<TrackedAddress>> GetTrackedAddressSourcesByScripthashAsync(
        FlorestaDbContext ctx,
        string scripthash,
        CancellationToken ct)
    {
        var address = await ctx.TrackedAddresses.FindAsync(new object[] { scripthash }, ct);
        if (address == null)
            return new List<TrackedAddress>();

        var sources = new List<TrackedAddress> { address };
        var ownerWalletIds = await ctx.TrackedAddressOwners
            .Where(o => o.Scripthash == scripthash)
            .Select(o => o.WalletId)
            .ToListAsync(ct);

        foreach (var walletId in ownerWalletIds)
        {
            if (walletId == address.WalletId)
                continue;

            sources.Add(CreateAddressSource(address, walletId));
        }

        return sources
            .GroupBy(a => a.WalletId)
            .Select(g => g.First())
            .ToList();
    }

    internal static async Task<List<TrackedAddress>> GetAllTrackedAddressSourcesAsync(
        FlorestaDbContext ctx,
        CancellationToken ct)
    {
        var primary = await ctx.TrackedAddresses
            .AsNoTracking()
            .ToListAsync(ct);
        var byScripthash = primary.ToDictionary(a => a.Scripthash);
        var sources = new List<TrackedAddress>(primary);

        var owners = await ctx.TrackedAddressOwners
            .AsNoTracking()
            .ToListAsync(ct);
        foreach (var owner in owners)
        {
            if (byScripthash.TryGetValue(owner.Scripthash, out var address))
                sources.Add(CreateAddressSource(address, owner.WalletId));
        }

        return sources
            .GroupBy(a => new { a.WalletId, a.Scripthash })
            .Select(g => g.First())
            .ToList();
    }

    internal static TrackedAddress CreateAddressSource(TrackedAddress address, string walletId)
    {
        if (address.WalletId == walletId)
            return address;

        return new TrackedAddress
        {
            Scripthash = address.Scripthash,
            WalletId = walletId,
            KeyPath = null,
            ScriptPubKey = address.ScriptPubKey,
            Address = address.Address,
            IsChange = false,
            IsReserved = false,
            IsUsed = address.IsUsed
        };
    }

    private async Task<FlorestaDescriptorRegistrationResult> PrepareDescriptorRegistrationAsync(
        string strategyStr,
        FlorestaSettings settings,
        CancellationToken ct)
    {
        if (settings.AutoRegisterDescriptors)
            return await _descriptorRegistry.RegisterAsync(settings.CryptoCode ?? "BTC", strategyStr, ct,
                settings.AutoRescanOnNewDescriptor
                    ? token => UpdateWalletRescanStateAsync(strategyStr, settings,
                        (wallet, now) => FlorestaWalletDescriptorMetadata.ApplyRescanPending(
                            wallet, settings.DefaultRescanStartHeight, now), token)
                    : null);

        return new FlorestaDescriptorRegistrationResult(
            _descriptorService.CreateDescriptors(settings.CryptoCode ?? "BTC", strategyStr),
            0,
            0,
            null);
    }

    private async Task StartDescriptorRescanIfNeededAsync(
        string strategyStr,
        FlorestaSettings settings,
        FlorestaDescriptorRegistrationResult descriptorRegistration,
        CancellationToken ct)
    {
        if (!settings.AutoRegisterDescriptors ||
            !settings.AutoRescanOnNewDescriptor)
        {
            await UpdateWalletRescanStateAsync(
                strategyStr,
                settings,
                FlorestaWalletDescriptorMetadata.ApplyRescanNotRequested,
                ct);
            return;
        }

        int? startHeight;
        await using (var ctx = _dbFactory.CreateContext())
        {
            var wallet = await ctx.TrackedWallets.FindAsync(new object[] { strategyStr }, ct);
            if (wallet?.RescanStatus != FlorestaWalletRescanStatus.Pending)
                return;
            startHeight = wallet.RescanStartHeight;
        }

        // Keep the durable intent while IBD or an unavailable readiness RPC prevents
        // a request. Registration may already have completed on a previous process.
        try
        {
            if (FlorestaChainInfoParser.Parse(await _rpcClient.GetBlockchainInfoAsync(ct)).IsInitialBlockDownload != false)
                return;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Floresta descriptor rescan deferred because node readiness could not be checked");
            return;
        }

        try
        {
            var result = await _rpcClient.RescanBlockchainAsync(
                startHeight,
                null,
                false,
                "medium",
                ct);
            if (result.ValueKind != System.Text.Json.JsonValueKind.True)
                throw new InvalidOperationException("Floresta did not accept the descriptor rescan.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (FlorestaRpcException ex) when (ex.Code == -32093)
        {
            // IBD can restart between the readiness check and rescanblockchain.
            _logger.LogInformation("Floresta descriptor rescan deferred during initial block download");
            return;
        }
        catch (Exception ex)
        {
            await UpdateWalletRescanStateAsync(
                strategyStr,
                settings,
                (wallet, now) => FlorestaWalletDescriptorMetadata.ApplyRescanFailed(
                    wallet,
                    startHeight,
                    ex.Message,
                    now),
                ct);
            throw;
        }

        await UpdateWalletRescanStateAsync(
            strategyStr,
            settings,
            (wallet, now) => FlorestaWalletDescriptorMetadata.ApplyRescanRequested(
                wallet,
                startHeight,
                now),
            ct);
        _logger.LogInformation(
            "Started Floresta rescan for descriptor {DescriptorHash} from height {Height}",
            descriptorRegistration.Descriptors.DescriptorHash,
            startHeight);
    }

    private async Task UpsertWalletDescriptorMetadataAsync(
        string strategyStr,
        FlorestaSettings settings,
        FlorestaDescriptorRegistrationResult descriptorRegistration,
        DateTimeOffset? descriptorRegisteredAt,
        string descriptorRegistrationError,
        CancellationToken ct,
        Action<TrackedWallet, DateTimeOffset> applyRescanState = null)
    {
        await using var ctx = _dbFactory.CreateContext();
        var wallet = await GetOrCreateWalletAsync(ctx, strategyStr, settings, ct);
        var now = DateTimeOffset.UtcNow;

        FlorestaWalletDescriptorMetadata.Apply(
            wallet,
            descriptorRegistration,
            descriptorRegisteredAt,
            descriptorRegistrationError,
            settings.AutoRegisterDescriptors,
            now);
        applyRescanState?.Invoke(wallet, now);
        await ctx.SaveChangesAsync(ct);
    }

    private async Task UpdateWalletRescanStateAsync(
        string strategyStr,
        FlorestaSettings settings,
        Action<TrackedWallet, DateTimeOffset> applyRescanState,
        CancellationToken ct)
    {
        await using var ctx = _dbFactory.CreateContext();
        var wallet = await GetOrCreateWalletAsync(ctx, strategyStr, settings, ct);
        applyRescanState(wallet, DateTimeOffset.UtcNow);
        await ctx.SaveChangesAsync(ct);
    }

    private static async Task<TrackedWallet> GetOrCreateWalletAsync(
        FlorestaDbContext ctx,
        string strategyStr,
        FlorestaSettings settings,
        CancellationToken ct)
    {
        var wallet = await ctx.TrackedWallets.FindAsync(new object[] { strategyStr }, ct);
        if (wallet != null)
            return wallet;

        wallet = new TrackedWallet
        {
            Id = strategyStr,
            CryptoCode = settings.CryptoCode ?? "BTC",
            DerivationStrategy = strategyStr,
            ReceiveGapIndex = settings.GapLimit - 1,
            ChangeGapIndex = settings.GapLimit - 1
        };
        ctx.TrackedWallets.Add(wallet);
        return wallet;
    }

    public async Task<List<NewTransactionInfo>> HandleScripthashNotificationAsync(
        string scripthash, string status, CancellationToken ct)
    {
        return await _walletSync.HandleScripthashNotificationAsync(scripthash, ct);
    }

    public async Task<List<NewTransactionInfo>> HandleNewBlockAsync(int height, CancellationToken ct)
    {
        var tipHeight = UpdateTipHeight(height);
        return await _walletSync.HandleNewBlockAsync(tipHeight, ct);
    }

    // ─────────────────────────────────────────────
    // Methods called by FlorestaHttpHandler
    // ─────────────────────────────────────────────

    public async Task<KeyPathInformation> GetNextUnusedAddressAsync(
        string strategyStr, bool isChange, bool reserve, CancellationToken ct)
    {
        var settings = await GetSettingsAsync();
        var descriptorRegistration = new FlorestaDescriptorRegistrationResult(
            _descriptorService.CreateDescriptors(settings.CryptoCode ?? "BTC", strategyStr),
            0,
            0,
            null);
        var reservation = await _addressPool.GetNextUnusedAddressAsync(
            strategyStr,
            isChange,
            reserve,
            settings,
            descriptorRegistration,
            ct);
        await _walletSync.SubscribeTrackedAddressesAsync(reservation.AddressesToSubscribe, ct);
        return reservation.Address;
    }

    public Task CancelReservationsAsync(string strategyStr, IEnumerable<KeyPath> keyPaths, CancellationToken ct)
    {
        return _addressPool.CancelReservationsAsync(strategyStr, keyPaths, ct);
    }

    public async Task<UTXOChanges> GetUTXOChangesAsync(string strategyStr, CancellationToken ct)
    {
        await using var ctx = _dbFactory.CreateContext();

        var utxos = await ctx.Utxos
            .Where(u => u.WalletId == strategyStr && !u.IsSpent)
            .ToListAsync(ct);

        var confirmed = new List<UTXO>();
        var unconfirmed = new List<UTXO>();
        var tipHeight = Volatile.Read(ref _tipHeight);

        foreach (var u in utxos)
        {
            var confirmations = GetConfirmations(u.BlockHeight, tipHeight);
            var utxo = new UTXO
            {
                Outpoint = OutPoint.Parse(u.Outpoint),
                Value = Money.Satoshis(u.Value),
                ScriptPubKey = Script.FromBytesUnsafe(u.ScriptPubKey),
                KeyPath = KeyPath.Parse(u.KeyPath),
                Timestamp = u.SeenAt,
                Confirmations = confirmations
            };

            if (confirmations > 0)
                confirmed.Add(utxo);
            else
                unconfirmed.Add(utxo);
        }

        var result = new UTXOChanges
        {
            CurrentHeight = tipHeight,
            Confirmed = new UTXOChange { UTXOs = confirmed },
            Unconfirmed = new UTXOChange { UTXOs = unconfirmed }
        };

        return result;
    }

    public async Task<GetBalanceResponse> GetBalanceAsync(string strategyStr, CancellationToken ct)
    {
        await using var ctx = _dbFactory.CreateContext();

        var utxos = await ctx.Utxos
            .Where(u => u.WalletId == strategyStr && !u.IsSpent)
            .ToListAsync(ct);

        var tipHeight = Volatile.Read(ref _tipHeight);
        var confirmed = utxos.Where(u => GetConfirmations(u.BlockHeight, tipHeight) > 0).Sum(u => u.Value);
        var unconfirmed = utxos.Where(u => GetConfirmations(u.BlockHeight, tipHeight) == 0).Sum(u => u.Value);

        return new GetBalanceResponse
        {
            Confirmed = Money.Satoshis(confirmed),
            Unconfirmed = Money.Satoshis(unconfirmed),
            Available = Money.Satoshis(confirmed + unconfirmed),
            Total = Money.Satoshis(confirmed + unconfirmed)
        };
    }

    public async Task WipeAsync(string strategyStr, CancellationToken ct)
    {
        await _walletSync.RunWithWalletLockAsync(strategyStr, async () =>
        {
            await using var ctx = _dbFactory.CreateContext();
            var utxos = await ctx.Utxos
                .Where(u => u.WalletId == strategyStr)
                .ToListAsync(ct);
            var transactions = await ctx.Transactions
                .Where(t => t.WalletId == strategyStr)
                .ToListAsync(ct);

            ctx.Utxos.RemoveRange(utxos);
            ctx.Transactions.RemoveRange(transactions);
            await ctx.SaveChangesAsync(ct);
            _logger.LogInformation(
                "Wiped Floresta wallet cache for wallet {WalletId}: {UtxoCount} UTXOs, {TxCount} transactions",
                LogSafeId.Hash(strategyStr),
                utxos.Count,
                transactions.Count);
        }, ct);
    }

    public async Task<TransactionResult> GetTransactionResultAsync(string txId, CancellationToken ct)
    {
        await using var ctx = _dbFactory.CreateContext();

        var tx = await ctx.Transactions
            .FirstOrDefaultAsync(t => t.Txid == txId, ct);

        if (tx == null)
        {
            // Try fetching from Electrum directly
            try
            {
                var rawHex = await _client.TransactionGetAsync(txId, ct);
                var parsed = Transaction.Parse(rawHex, _network);
                return new TransactionResult
                {
                    TransactionHash = parsed.GetHash(),
                    Transaction = parsed,
                    Confirmations = 0,
                    Timestamp = DateTimeOffset.UtcNow
                };
            }
            catch
            {
                return null;
            }
        }

        var transaction = tx.RawTx != null ? Transaction.Load(tx.RawTx, _network) : null;
        var confirmations = GetTransactionConfirmations(tx, Volatile.Read(ref _tipHeight));

        return new TransactionResult
        {
            TransactionHash = uint256.Parse(tx.Txid),
            Transaction = transaction,
            Confirmations = confirmations,
            Height = GetConfirmedHeight(tx.BlockHeight, confirmations),
            Timestamp = tx.SeenAt
        };
    }

    public async Task<TransactionInformation> GetTransactionInfoAsync(
        string strategyStr, string txId, CancellationToken ct)
    {
        await using var ctx = _dbFactory.CreateContext();

        var tx = await ctx.Transactions
            .FirstOrDefaultAsync(t => t.Txid == txId && t.WalletId == strategyStr, ct);

        if (tx == null) return null;

        var transaction = tx.RawTx != null ? Transaction.Load(tx.RawTx, _network) : null;
        var confirmations = GetTransactionConfirmations(tx, Volatile.Read(ref _tipHeight));

        return new TransactionInformation
        {
            TransactionId = uint256.Parse(tx.Txid),
            Transaction = transaction,
            Confirmations = confirmations,
            Height = GetConfirmedHeight(tx.BlockHeight, confirmations),
            Timestamp = tx.SeenAt,
            BalanceChange = Money.Satoshis(tx.BalanceChange)
        };
    }

    public async Task<GetTransactionsResponse> GetTransactionsResponseAsync(
        string strategyStr, CancellationToken ct)
    {
        await using var ctx = _dbFactory.CreateContext();

        var txs = await ctx.Transactions
            .Where(t => t.WalletId == strategyStr)
            .OrderByDescending(t => t.SeenAt)
            .ToListAsync(ct);

        var confirmed = new List<TransactionInformation>();
        var unconfirmed = new List<TransactionInformation>();
        var replaced = new List<TransactionInformation>();
        var tipHeight = Volatile.Read(ref _tipHeight);

        foreach (var tx in txs)
        {
            var info = CreateTransactionInformation(tx, tipHeight);

            if (info.Confirmations == -1)
                replaced.Add(info);
            else if (info.Confirmations > 0)
                confirmed.Add(info);
            else
                unconfirmed.Add(info);
        }

        return new GetTransactionsResponse
        {
            Height = tipHeight,
            ConfirmedTransactions = new TransactionInformationSet { Transactions = confirmed },
            UnconfirmedTransactions = new TransactionInformationSet { Transactions = unconfirmed },
            ReplacedTransactions = new TransactionInformationSet { Transactions = replaced }
        };
    }

    public async Task<BroadcastResult> BroadcastAsync(string body, CancellationToken ct)
    {
        return await _broadcaster.BroadcastAsync(body, ct);
    }

    public async Task<BroadcastResult> BroadcastAsync(string body, bool testMempoolAccept, CancellationToken ct)
    {
        return await _broadcaster.BroadcastAsync(body, testMempoolAccept, ct);
    }

    public async Task<BroadcastResult> BroadcastAsync(byte[] body, CancellationToken ct)
    {
        return await _broadcaster.BroadcastAsync(body, ct);
    }

    public async Task<BroadcastResult> BroadcastAsync(byte[] body, bool testMempoolAccept, CancellationToken ct)
    {
        return await _broadcaster.BroadcastAsync(body, testMempoolAccept, ct);
    }

    internal static bool TryExtractRawTransactionHex(string body, out string rawTx, out string error)
    {
        return FlorestaTransactionBroadcaster.TryExtractRawTransactionHex(body, out rawTx, out error);
    }

    internal static bool TryExtractRawTransactionHex(byte[] body, out string rawTx, out string error)
    {
        return FlorestaTransactionBroadcaster.TryExtractRawTransactionHex(body, out rawTx, out error);
    }

    public async Task<GetFeeRateResult> GetFeeRateAsync(int blockTarget, CancellationToken ct)
    {
        return new GetFeeRateResult
        {
            FeeRate = await _feeProvider.GetFeeRateAsync(blockTarget),
            BlockCount = blockTarget
        };
    }

    // ─────────────────────────────────────────────
    // UTXO Set Scan (replaces Bitcoin Core's scantxoutset)
    // ─────────────────────────────────────────────

    public ScanUTXOInformation GetScanState(string strategyStr)
    {
        _scanStates.TryGetValue(strategyStr, out var state);
        return state;
    }

    public bool StartScan(string strategyStr, int gapLimit, int startingIndex)
    {
        return StartScan(
            strategyStr,
            gapLimit,
            startingIndex,
            (info, ct) => RescanWalletAsync(strategyStr, gapLimit, startingIndex, info, ct));
    }

    internal bool StartScan(
        string strategyStr,
        int gapLimit,
        int startingIndex,
        Func<ScanUTXOInformation, CancellationToken, Task> scan)
    {
        var info = new ScanUTXOInformation
        {
            Status = ScanUTXOStatus.Queued,
            QueuedAt = DateTimeOffset.UtcNow,
            Progress = new ScanUTXOProgress
            {
                StartedAt = DateTimeOffset.UtcNow,
                From = startingIndex,
                Count = gapLimit * 2,
                OverallProgress = 0
            }
        };

        var task = _backgroundTasks.TryRun($"scan:{strategyStr}", async ct =>
        {
            try
            {
                await RunScanAsync(info, token => scan(info, token), ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scan failed for wallet {WalletId}", LogSafeId.Hash(strategyStr));
            }
        }, () => _scanStates[strategyStr] = info);

        return task is not null;
    }

    internal static async Task RunScanAsync(
        ScanUTXOInformation info,
        Func<CancellationToken, Task> scan,
        CancellationToken ct)
    {
        try
        {
            info.Status = ScanUTXOStatus.Pending;
            info.Progress.StartedAt = DateTimeOffset.UtcNow;
            await scan(ct);
            ct.ThrowIfCancellationRequested();
            info.Status = ScanUTXOStatus.Complete;
            info.Progress.CompletedAt = DateTimeOffset.UtcNow;
            info.Progress.OverallProgress = 100;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            info.Status = ScanUTXOStatus.Error;
            info.Error = ScanCanceledError;
            info.Progress.CompletedAt = DateTimeOffset.UtcNow;
            info.Progress.OverallProgress = Math.Min(info.Progress.OverallProgress, 99);
        }
        catch (Exception ex)
        {
            info.Status = ScanUTXOStatus.Error;
            info.Error = ex.Message;
            info.Progress.CompletedAt = DateTimeOffset.UtcNow;
            info.Progress.OverallProgress = Math.Min(info.Progress.OverallProgress, 99);
            throw;
        }
    }

    internal static void ApplyScanSyncResult(
        ScanUTXOInformation scanInfo,
        FlorestaWalletSync.WalletAddressSyncSummary summary,
        int found)
    {
        scanInfo.Progress.Found = found;
        if (!summary.IsComplete)
            throw new InvalidOperationException(ScanPartialError);
    }

    private async Task RescanWalletAsync(
        string strategyStr, int gapLimit, int startingIndex,
        ScanUTXOInformation scanInfo, CancellationToken ct)
    {
        if (!_client.IsConnected)
            throw new InvalidOperationException("Floresta Electrum is not connected.");

        var preparation = await PrepareWalletAsync(strategyStr, ct);

        var newAddresses = await _addressPool.EnsureScanAddressesAsync(
            strategyStr,
            preparation.Settings,
            preparation.DescriptorRegistration,
            preparation.DescriptorRegisteredAt,
            startingIndex,
            gapLimit,
            ct);
        await _walletSync.SubscribeTrackedAddressesAsync(newAddresses, ct);

        var allAddresses = new List<TrackedAddress>();
        await using (var ctx = _dbFactory.CreateContext())
        {
            allAddresses = await ctx.TrackedAddresses
                .Where(a => a.WalletId == strategyStr)
                .ToListAsync(ct);
        }

        scanInfo.Progress.Count = allAddresses.Count;

        var syncResult = await _walletSync.SyncWalletStateForScanAsync(
            strategyStr,
            allAddresses,
            ct,
            (processed, total) =>
            {
                scanInfo.Progress.Count = total;
                scanInfo.Progress.TotalSearched = processed;
                scanInfo.Progress.OverallProgress = total == 0 ? 100 : processed * 100 / total;
            });

        int found;
        await using (var ctx = _dbFactory.CreateContext())
        {
            found = await ctx.Utxos
                .CountAsync(u => u.WalletId == strategyStr && !u.IsSpent, ct);
            if (scanInfo.Progress.TotalSearched == 0 && allAddresses.Count == 0)
            {
                scanInfo.Progress.OverallProgress = 100;
            }
        }

        ApplyScanSyncResult(scanInfo, syncResult.Summary, found);
    }

    // ─────────────────────────────────────────────
    // Metadata storage (replaces NBXplorer metadata)
    // ─────────────────────────────────────────────

    public async Task SetMetadataAsync(string derivationScheme, string key, string value, CancellationToken ct)
    {
        await using var ctx = _dbFactory.CreateContext();
        var metaKey = $"metadata:{derivationScheme}:{key}";
        var existing = await ctx.SyncStates.FindAsync(new object[] { metaKey }, ct);
        if (existing != null)
        {
            existing.Value = value;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            ctx.SyncStates.Add(new Data.SyncState
            {
                Key = metaKey,
                Value = value,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<string> GetMetadataAsync(string derivationScheme, string key, CancellationToken ct)
    {
        await using var ctx = _dbFactory.CreateContext();
        var metaKey = $"metadata:{derivationScheme}:{key}";
        var state = await ctx.SyncStates.FindAsync(new object[] { metaKey }, ct);
        return state?.Value;
    }

    // ─────────────────────────────────────────────
    // Private helpers
    // ─────────────────────────────────────────────

    private DerivationStrategyBase ParseStrategy(string strategyStr)
    {
        if (_derivationFactory == null) return null;
        try
        {
            return _derivationFactory.Parse(strategyStr);
        }
        catch
        {
            _logger.LogWarning("Failed to parse derivation strategy for wallet {WalletId}", LogSafeId.Hash(strategyStr));
            return null;
        }
    }

    private int UpdateTipHeight(int height)
    {
        while (true)
        {
            var current = Volatile.Read(ref _tipHeight);
            if (height <= current)
                return current;

            if (Interlocked.CompareExchange(ref _tipHeight, height, current) == current)
                return height;
        }
    }

    internal static int GetConfirmations(long? blockHeight, int tipHeight)
    {
        if (blockHeight is not > 0 || tipHeight <= 0)
            return 0;

        return Math.Max(0, tipHeight - (int)blockHeight.Value + 1);
    }

    internal static int GetTransactionConfirmations(TrackedTransaction transaction, int tipHeight)
    {
        return transaction.RemovedAt is not null ? -1 : GetConfirmations(transaction.BlockHeight, tipHeight);
    }

    internal static int GetConfirmedHeight(long? blockHeight, int confirmations)
    {
        return confirmations > 0 && blockHeight is > 0 ? (int)blockHeight.Value : 0;
    }

    private TransactionInformation CreateTransactionInformation(TrackedTransaction tx, int tipHeight)
    {
        var confirmations = GetTransactionConfirmations(tx, tipHeight);
        return new TransactionInformation
        {
            TransactionId = uint256.Parse(tx.Txid),
            Transaction = tx.RawTx != null ? Transaction.Load(tx.RawTx, _network) : null,
            Confirmations = confirmations,
            Height = GetConfirmedHeight(tx.BlockHeight, confirmations),
            Timestamp = tx.SeenAt,
            BalanceChange = Money.Satoshis(tx.BalanceChange)
        };
    }

    internal static FlorestaElectrumUnspentItem NormalizeUtxoPosition(
        FlorestaElectrumUnspentItem utxo,
        Transaction tx,
        byte[] scriptPubKey)
    {
        return FlorestaUtxoCache.NormalizeUtxoPosition(utxo, tx, scriptPubKey);
    }

    // ─────────────────────────────────────────────
    // NewTransactionInfo for the listener
    // ─────────────────────────────────────────────

    public class NewTransactionInfo
    {
        public string TxId { get; set; }
        public Transaction Transaction { get; set; }
        public TrackedSource TrackedSource { get; set; }
        public DerivationStrategyBase DerivationStrategy { get; set; }
        public int Confirmations { get; set; }
        public int Height { get; set; }
        public bool IsRbf { get; set; }
        public DateTimeOffset SeenAt { get; set; }
        public List<OutputInfo> Outputs { get; set; } = new();
    }

    public class OutputInfo
    {
        public string Address { get; set; }
        public string TrackedDestination { get; set; }
        public int Index { get; set; }
        public Money Value { get; set; }
        public Script ScriptPubKey { get; set; }
        public string KeyPath { get; set; }
        public int KeyIndex { get; set; }
    }

    private async Task<FlorestaSettings> GetSettingsAsync()
    {
        return await _settingsRepository.GetSettingAsync<FlorestaSettings>() ?? new FlorestaSettings();
    }
}
