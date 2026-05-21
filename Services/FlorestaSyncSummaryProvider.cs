using System.Collections.Generic;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.HostedServices;

namespace BTCPayServer.Plugins.Floresta.Services;

public class FlorestaSyncSummaryProvider : ISyncSummaryProvider
{
    private readonly NBXplorerDashboard _dashboard;
    private readonly BTCPayNetworkProvider _networkProvider;

    public FlorestaSyncSummaryProvider(
        NBXplorerDashboard dashboard,
        BTCPayNetworkProvider networkProvider)
    {
        _dashboard = dashboard;
        _networkProvider = networkProvider;
    }

    public bool AllAvailable()
    {
        return _dashboard.IsFullySynched("BTC", out _);
    }

    public string Partial => "Floresta/FlorestaSyncSummary";

    public IEnumerable<ISyncStatus> GetStatuses()
    {
        var network = _networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        if (network is null)
            yield break;

        var summary = _dashboard.Get(network.CryptoCode);
        var available = summary?.State == NBXplorerState.Ready;
        yield return new FlorestaSyncStatus(available)
        {
            PaymentMethodId = network.CryptoCode + "-CHAIN",
            ChainHeight = summary?.Status?.ChainHeight ?? 0,
            SyncHeight = summary?.Status?.SyncHeight ?? 0
        };
    }
}

public class FlorestaSyncStatus : ISyncStatus
{
    public string PaymentMethodId { get; set; }
    public bool Available { get; }
    public int ChainHeight { get; set; }
    public int SyncHeight { get; set; }

    public FlorestaSyncStatus(bool available)
    {
        Available = available;
    }
}
