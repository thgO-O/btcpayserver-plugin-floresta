using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client.Models;
using BTCPayServer.HostedServices;
using BTCPayServer.Payments;
using NBXplorer.Models;

namespace BTCPayServer.Plugins.Floresta.Services;

public class FlorestaNonBitcoinSyncSummaryProvider : ISyncSummaryProvider
{
    private readonly NBXplorerDashboard _dashboard;

    public FlorestaNonBitcoinSyncSummaryProvider(NBXplorerDashboard dashboard)
    {
        _dashboard = dashboard;
    }

    public bool AllAvailable()
    {
        return GetNonBitcoinSummaries().All(summary => summary.Status?.IsFullySynched is true);
    }

    public string Partial => "Floresta/NonBitcoinSyncSummary";

    public IEnumerable<ISyncStatus> GetStatuses()
    {
        return GetNonBitcoinSummaries()
            .Where(summary => summary.Network.ShowSyncSummary)
            .Select(summary => new FlorestaNonBitcoinSyncStatus
            {
                PaymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(summary.Network.CryptoCode).ToString(),
                NodeInformation = summary.Status?.BitcoinStatus is BitcoinStatus s ? new ServerInfoNodeData
                {
                    Headers = s.Headers,
                    Blocks = s.Blocks,
                    VerificationProgress = s.VerificationProgress
                } : null,
                ChainHeight = summary.Status?.ChainHeight ?? 0,
                SyncHeight = summary.Status?.SyncHeight ?? 0,
                Available = summary.Status?.IsFullySynched is true
            });
    }

    private IEnumerable<NBXplorerDashboard.NBXplorerSummary> GetNonBitcoinSummaries()
    {
        return _dashboard.GetAll().Where(summary => !summary.Network.IsBTC);
    }

}

public class FlorestaNonBitcoinSyncStatus : ServerInfoSyncStatusData, ISyncStatus
{
}
