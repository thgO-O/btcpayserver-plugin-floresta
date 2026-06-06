using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Services;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NBXplorer.Models;

namespace BTCPayServer.Plugins.Floresta.Services;

/// <summary>
/// Monitors the Floresta-backed BTC connection and publishes state changes via EventAggregator.
/// </summary>
public class FlorestaStatusMonitor : IHostedService
{
    private readonly FlorestaElectrumClient _client;
    private readonly FlorestaRpcClient _rpcClient;
    private readonly SettingsRepository _settingsRepository;
    private readonly StoreRepository _storeRepository;
    private readonly NBXplorerDashboard _dashboard;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly EventAggregator _eventAggregator;
    private readonly ILogger<FlorestaStatusMonitor> _logger;
    private readonly SemaphoreSlim _stepLock = new(1, 1);
    private int _tipHeight;
    private CancellationTokenSource _cts;
    private Task _monitorLoop;

    public NBXplorerState State { get; private set; } = NBXplorerState.NotConnected;
    public int TipHeight => Volatile.Read(ref _tipHeight);
    public string ServerVersion { get; private set; }
    public string BestBlockHash { get; private set; }
    public bool? IsInitialBlockDownload { get; private set; }
    public int? ValidatedHeight { get; private set; }
    public int? UtreexoRootCount { get; private set; }
    public bool? RpcReachable { get; private set; }
    public DateTimeOffset? LastUpdated { get; private set; }
    public string LastError { get; private set; }
    public FlorestaChainInfo ChainInfo { get; private set; } = new();

    public FlorestaStatusMonitor(
        FlorestaElectrumClient client,
        FlorestaRpcClient rpcClient,
        SettingsRepository settingsRepository,
        StoreRepository storeRepository,
        NBXplorerDashboard dashboard,
        BTCPayNetworkProvider networkProvider,
        EventAggregator eventAggregator,
        ILogger<FlorestaStatusMonitor> logger)
    {
        _client = client;
        _rpcClient = rpcClient;
        _settingsRepository = settingsRepository;
        _storeRepository = storeRepository;
        _dashboard = dashboard;
        _networkProvider = networkProvider;
        _eventAggregator = eventAggregator;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        DisableUnsupportedNetworkCapabilities(_networkProvider.GetNetwork<BTCPayNetwork>("BTC"));
        await DisableStorePayJoinAsync();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _monitorLoop = MonitorLoop(_cts.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_monitorLoop != null)
        {
            try { await _monitorLoop; } catch (OperationCanceledException) { }
        }
    }

    private async Task MonitorLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Floresta status monitor");
            }

            var delay = State == NBXplorerState.Ready ? TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(10);
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
        }
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        await _stepLock.WaitAsync(ct);
        try
        {
            await StepAsync(ct);
        }
        finally
        {
            _stepLock.Release();
        }
    }

    private async Task StepAsync(CancellationToken ct)
    {
        var oldState = State;
        if (!await IsBitcoinBackendActiveAsync())
        {
            if (_client.IsConnected)
                await _client.DisconnectAsync();

            RpcReachable = false;
            LastError = "Floresta Bitcoin backend is disabled.";
            LastUpdated = DateTimeOffset.UtcNow;
            ServerVersion = null;
            SetState(NBXplorerState.NotConnected, oldState, null, null);
            return;
        }

        if (!_client.IsConnected)
        {
            try
            {
                await _client.ConnectAsync(ct);
                var (sw, pv) = await _client.ServerVersionAsync("BTCPayServer-Floresta", "1.4", ct);
                ServerVersion = sw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Cannot connect to Floresta backend");
                RpcReachable = false;
                LastError = ex.Message;
                LastUpdated = DateTimeOffset.UtcNow;
                SetState(NBXplorerState.NotConnected, oldState, null, null);
                return;
            }
        }

        try
        {
            await _client.PingAsync(ct);
            if (string.IsNullOrEmpty(ServerVersion))
            {
                var (sw, _) = await _client.ServerVersionAsync("BTCPayServer-Floresta", "1.4", ct);
                ServerVersion = sw;
            }

            var blockchainInfo = await _rpcClient.GetBlockchainInfoAsync(ct);
            UpdateChainInfo(blockchainInfo);
            RpcReachable = true;
            LastError = null;
            LastUpdated = DateTimeOffset.UtcNow;

            _ = await _client.ServerFeaturesAsync(ct);

            State = IsInitialBlockDownload == true ? NBXplorerState.Synching : NBXplorerState.Ready;

            var status = BuildStatusResult();
            PublishDashboard(status, oldState);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Floresta health check failed");
            RpcReachable = false;
            LastError = ex.Message;
            LastUpdated = DateTimeOffset.UtcNow;
            State = NBXplorerState.NotConnected;
            SetState(NBXplorerState.NotConnected, oldState, null, null);
        }
    }

    public StatusResult GetStatusResult()
    {
        return BuildStatusResult();
    }

    private StatusResult BuildStatusResult()
    {
        var syncHeight = ValidatedHeight ?? TipHeight;
        return new StatusResult
        {
            IsFullySynched = State == NBXplorerState.Ready,
            ChainHeight = TipHeight,
            SyncHeight = syncHeight,
            Version = ServerVersion ?? "floresta-plugin",
            SupportedCryptoCodes = new[] { "BTC" },
            NetworkType = GetChainName(),
            BitcoinStatus = new BitcoinStatus
            {
                Blocks = syncHeight,
                Headers = TipHeight,
                VerificationProgress = 1.0,
                IsSynched = State == NBXplorerState.Ready,
                MinRelayTxFee = new NBitcoin.FeeRate(1.0m),
                IncrementalRelayFee = new NBitcoin.FeeRate(1.0m),
                Capabilities = CreateNodeCapabilities()
            }
        };
    }

    internal static NodeCapabilities CreateNodeCapabilities()
    {
        return new NodeCapabilities
        {
            CanScanTxoutSet = true,
            CanSupportSegwit = true,
            CanSupportTaproot = true,
            CanSupportTransactionCheck = false
        };
    }

    internal static void DisableUnsupportedNetworkCapabilities(BTCPayNetwork network)
    {
        if (network is null)
            return;

        network.SupportPayJoin = false;
    }

    internal static bool DisableStorePayJoin(StoreData store)
    {
        var blob = store.GetStoreBlob();
        if (!blob.PayJoinEnabled)
            return false;

        blob.PayJoinEnabled = false;
        return store.SetStoreBlob(blob);
    }

    private async Task DisableStorePayJoinAsync()
    {
        if (_storeRepository is null)
            return;

        StoreData[] stores;
        try
        {
            stores = await _storeRepository.GetStores();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load stores to disable PayJoin settings for the Floresta backend");
            return;
        }

        var disabled = 0;
        foreach (var store in stores)
        {
            try
            {
                if (!DisableStorePayJoin(store))
                    continue;
                await _storeRepository.UpdateStoreBlob(store);
                disabled++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not disable PayJoin for store {StoreId} while starting the Floresta backend",
                    store?.Id);
            }
        }

        if (disabled > 0)
        {
            _logger.LogInformation(
                "Disabled PayJoin on {StoreCount} stores because the Floresta backend does not support transaction checks",
                disabled);
        }
    }

    private void UpdateChainInfo(JsonElement blockchainInfo)
    {
        var parsed = FlorestaChainInfoParser.Parse(blockchainInfo);
        ChainInfo = parsed;
        if (parsed.Height is int height)
            SetTipHeight(height);
        BestBlockHash = parsed.BestBlockHash ?? BestBlockHash;
        IsInitialBlockDownload = parsed.IsInitialBlockDownload ?? IsInitialBlockDownload;
        ValidatedHeight = parsed.ValidatedHeight ?? ValidatedHeight;
        UtreexoRootCount = parsed.UtreexoRootCount ?? UtreexoRootCount;
    }

    private NBitcoin.ChainName GetChainName()
    {
        var network = _networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        return network?.NBitcoinNetwork?.ChainName ?? NBitcoin.ChainName.Mainnet;
    }

    public FlorestaHealthSnapshot GetHealthSnapshot()
    {
        return new FlorestaHealthSnapshot(
            State.ToString(),
            ServerVersion ?? "unknown",
            RpcReachable,
            ChainInfo.Height ?? (TipHeight > 0 ? TipHeight : null),
            ChainInfo.BestBlockHash ?? BestBlockHash,
            ChainInfo.IsInitialBlockDownload ?? IsInitialBlockDownload,
            ChainInfo.ValidatedHeight ?? ValidatedHeight,
            ChainInfo.UtreexoRootCount ?? UtreexoRootCount,
            LastUpdated,
            LastError);
    }

    private void SetState(NBXplorerState newState, NBXplorerState oldState, StatusResult status, GetMempoolInfoResponse mempoolInfo)
    {
        State = newState;
        PublishDashboard(status, oldState);
    }

    private void PublishDashboard(StatusResult status, NBXplorerState oldState)
    {
        var network = _networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        if (network is null)
            return;

        _dashboard.Publish(network, State, status, null, State == NBXplorerState.NotConnected ? "Floresta backend not connected" : null);

        if (oldState != State)
        {
            _eventAggregator.Publish(new NBXplorerStateChangedEvent(network, oldState, State));
        }
    }

    internal void UpdateTipHeight(int height)
    {
        while (true)
        {
            var current = TipHeight;
            if (height <= current)
                return;

            if (Interlocked.CompareExchange(ref _tipHeight, height, current) == current)
                return;
        }
    }

    internal void SetTipHeight(int height)
    {
        Volatile.Write(ref _tipHeight, height);
    }

    private async Task<bool> IsBitcoinBackendActiveAsync()
    {
        var settings = await _settingsRepository.GetSettingAsync<FlorestaSettings>() ?? new FlorestaSettings();
        return settings.IsBitcoinBackendActive();
    }
}
