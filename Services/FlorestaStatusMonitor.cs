using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NBXplorer.Models;

namespace BTCPayServer.Plugins.Floresta.Services;

/// <summary>
/// Replaces NBXplorerWaiters. Monitors the Electrum server connection status
/// and publishes state changes via EventAggregator.
/// </summary>
public class FlorestaStatusMonitor : IHostedService
{
    private readonly FlorestaElectrumClient _client;
    private readonly FlorestaRpcClient _rpcClient;
    private readonly NBXplorerDashboard _dashboard;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly EventAggregator _eventAggregator;
    private readonly ILogger<FlorestaStatusMonitor> _logger;
    private CancellationTokenSource _cts;
    private Task _monitorLoop;

    public NBXplorerState State { get; private set; } = NBXplorerState.NotConnected;
    public int TipHeight { get; private set; }
    public string ServerVersion { get; private set; }
    public string BestBlockHash { get; private set; }
    public bool? IsInitialBlockDownload { get; private set; }
    public int? ValidatedHeight { get; private set; }
    public int? UtreexoRootCount { get; private set; }

    public FlorestaStatusMonitor(
        FlorestaElectrumClient client,
        FlorestaRpcClient rpcClient,
        NBXplorerDashboard dashboard,
        BTCPayNetworkProvider networkProvider,
        EventAggregator eventAggregator,
        ILogger<FlorestaStatusMonitor> logger)
    {
        _client = client;
        _rpcClient = rpcClient;
        _dashboard = dashboard;
        _networkProvider = networkProvider;
        _eventAggregator = eventAggregator;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _monitorLoop = MonitorLoop(_cts.Token);
        return Task.CompletedTask;
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
                await StepAsync(ct);
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

    private async Task StepAsync(CancellationToken ct)
    {
        var oldState = State;

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

            _ = await _client.ServerFeaturesAsync(ct);

            State = IsInitialBlockDownload == true ? NBXplorerState.Synching : NBXplorerState.Ready;

            var status = BuildStatusResult();
            PublishDashboard(status, oldState);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Floresta health check failed");
            State = NBXplorerState.NotConnected;
            SetState(NBXplorerState.NotConnected, oldState, null, null);
        }
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
                Capabilities = new NodeCapabilities
                {
                    CanScanTxoutSet = true
                }
            }
        };
    }

    private void UpdateChainInfo(JsonElement blockchainInfo)
    {
        TipHeight = GetInt32(blockchainInfo, "blocks") ?? GetInt32(blockchainInfo, "height") ?? TipHeight;
        BestBlockHash = GetString(blockchainInfo, "bestblockhash") ?? GetString(blockchainInfo, "best_block") ?? BestBlockHash;
        IsInitialBlockDownload = GetBool(blockchainInfo, "initialblockdownload") ?? GetBool(blockchainInfo, "ibd") ?? IsInitialBlockDownload;
        ValidatedHeight = GetInt32(blockchainInfo, "validated") ?? ValidatedHeight;
        UtreexoRootCount = GetInt32(blockchainInfo, "root_count") ?? UtreexoRootCount;
    }

    private static int? GetInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
            return null;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value))
            return value;

        if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out value))
            return value;

        return null;
    }

    private static bool? GetBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
            return null;

        if (property.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return property.GetBoolean();

        if (property.ValueKind == JsonValueKind.String && bool.TryParse(property.GetString(), out var value))
            return value;

        return null;
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private NBitcoin.ChainName GetChainName()
    {
        var network = _networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        return network?.NBitcoinNetwork?.ChainName ?? NBitcoin.ChainName.Mainnet;
    }

    private void SetState(NBXplorerState newState, NBXplorerState oldState, StatusResult status, GetMempoolInfoResponse mempoolInfo)
    {
        State = newState;
        PublishDashboard(status, oldState);
    }

    private void PublishDashboard(StatusResult status, NBXplorerState oldState)
    {
        foreach (var network in _networkProvider.GetAll().OfType<BTCPayNetwork>())
        {
            _dashboard.Publish(network, State, status, null, State == NBXplorerState.NotConnected ? "Floresta backend not connected" : null);

            if (oldState != State)
            {
                _eventAggregator.Publish(new NBXplorerStateChangedEvent(network, oldState, State));
            }
        }
    }

    internal void UpdateTipHeight(int height)
    {
        TipHeight = height;
    }
}
