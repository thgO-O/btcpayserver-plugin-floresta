using System.Collections.Generic;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.HostedServices;

namespace BTCPayServer.Plugins.Floresta.Services;

public class FlorestaSyncSummaryProvider(
    NBXplorerDashboard dashboard,
    BTCPayNetworkProvider networkProvider)
    : ISyncSummaryProvider
{
    public bool AllAvailable()
    {
        return dashboard.IsFullySynched("BTC", out _);
    }

    public string Partial => "Floresta/FlorestaSyncSummary";

    public IEnumerable<ISyncStatus> GetStatuses()
    {
        var network = networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        if (network is null)
            yield break;

        var summary = dashboard.Get(network.CryptoCode);
        var available = summary?.State == NBXplorerState.Ready;
        yield return new FlorestaSyncStatus(available)
        {
            PaymentMethodId = network.CryptoCode + "-CHAIN",
            ChainHeight = summary?.Status?.ChainHeight ?? 0,
            SyncHeight = summary?.Status?.SyncHeight ?? 0
        };
    }
}

public class FlorestaSyncStatus(bool available) : ISyncStatus
{
    public string PaymentMethodId { get; set; }
    public bool Available { get; } = available;
    public int ChainHeight { get; set; }
    public int SyncHeight { get; set; }
}
