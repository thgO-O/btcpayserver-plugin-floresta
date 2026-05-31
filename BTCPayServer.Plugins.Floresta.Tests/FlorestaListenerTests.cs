using System.Collections.Generic;
using BTCPayServer.Client.Models;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
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

    [Theory]
    [InlineData(SpeedPolicy.HighSpeed, false, 0, true)]
    [InlineData(SpeedPolicy.HighSpeed, true, 0, false)]
    [InlineData(SpeedPolicy.HighSpeed, true, 1, true)]
    [InlineData(SpeedPolicy.MediumSpeed, false, 0, false)]
    [InlineData(SpeedPolicy.MediumSpeed, false, 1, true)]
    [InlineData(SpeedPolicy.LowMediumSpeed, false, 1, false)]
    [InlineData(SpeedPolicy.LowMediumSpeed, false, 2, true)]
    [InlineData(SpeedPolicy.LowSpeed, false, 5, false)]
    [InlineData(SpeedPolicy.LowSpeed, false, 6, true)]
    public void PaymentSettlementMatchesBTCPayConfirmationPolicy(
        SpeedPolicy speedPolicy,
        bool rbf,
        long confirmations,
        bool expectedSettled)
    {
        var invoice = new InvoiceEntity { SpeedPolicy = speedPolicy };
        var paymentData = new BitcoinLikePaymentData
        {
            RBF = rbf,
            ConfirmationCount = confirmations
        };

        Assert.Equal(expectedSettled, FlorestaListener.IsSettled(invoice, paymentData));
    }

    [Fact]
    public void ConfirmationCountIsClampedToNetworkTrackingLimit()
    {
        var network = CreateBitcoinNetwork();
        network.MaxTrackedConfirmation = 3;

        Assert.Equal(0, FlorestaListener.ClampConfirmations(-1, network));
        Assert.Equal(2, FlorestaListener.ClampConfirmations(2, network));
        Assert.Equal(3, FlorestaListener.ClampConfirmations(20, network));
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
