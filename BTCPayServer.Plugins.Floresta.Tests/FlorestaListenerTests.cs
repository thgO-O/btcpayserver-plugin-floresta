using System.Collections.Generic;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Floresta.Services;
using BTCPayServer.Services.Invoices;
using NBitcoin;
using NBXplorer;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests;

public class FlorestaListenerTests
{
    [Fact]
    public void InvoiceTracksScriptPubKeyOnlyForIndexedInvoiceAddress()
    {
        var network = CreateBitcoinNetwork();
        var pmi = PaymentTypes.CHAIN.GetPaymentMethodId("BTC");
        var invoiceScript = new Key().PubKey.WitHash.ScriptPubKey;
        var otherScript = new Key().PubKey.WitHash.ScriptPubKey;
        var invoice = new InvoiceEntity
        {
            Addresses = new HashSet<(PaymentMethodId PaymentMethodId, string Address)>
            {
                (pmi, network.GetTrackedDestination(invoiceScript))
            }
        };

        Assert.True(FlorestaListener.InvoiceTracksScriptPubKey(invoice, pmi, network, invoiceScript));
        Assert.False(FlorestaListener.InvoiceTracksScriptPubKey(invoice, pmi, network, otherScript));
    }

    private static BTCPayNetwork CreateBitcoinNetwork()
    {
        var chainName = ChainName.Regtest;
        var nbxplorerNetworkProvider = new NBXplorerNetworkProvider(chainName);
        var nbxplorerNetwork = nbxplorerNetworkProvider.GetFromCryptoCode("BTC");
        return new BTCPayNetwork
        {
            CryptoCode = nbxplorerNetwork.CryptoCode,
            DisplayName = "Bitcoin",
            NBXplorerNetwork = nbxplorerNetwork,
            CoinType = new KeyPath("1'"),
            SupportRBF = true,
            SupportPayJoin = true,
            VaultSupported = true
        }.SetDefaultElectrumMapping(chainName);
    }
}
