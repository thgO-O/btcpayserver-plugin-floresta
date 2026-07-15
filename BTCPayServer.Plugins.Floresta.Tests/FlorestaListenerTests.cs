using System;
using System.Collections.Generic;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Plugins.Floresta;
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

        Assert.Equal(-1, FlorestaListener.ClampConfirmations(-1, network));
        Assert.Equal(2, FlorestaListener.ClampConfirmations(2, network));
        Assert.Equal(3, FlorestaListener.ClampConfirmations(20, network));
    }

    [Fact]
    public void TransactionResultUsesNewTransactionInfoHeight()
    {
        var tx = Transaction.Create(Network.RegTest);
        var txHash = tx.GetHash();
        var seenAt = DateTimeOffset.UtcNow;

        var result = FlorestaListener.CreateTransactionResult(
            new FlorestaWalletTracker.NewTransactionInfo
            {
                TxId = txHash.ToString(),
                Transaction = tx,
                Confirmations = 2,
                Height = 120,
                SeenAt = seenAt
            },
            txHash);

        Assert.Equal(txHash, result.TransactionHash);
        Assert.Equal(tx, result.Transaction);
        Assert.Equal(2, result.Confirmations);
        Assert.Equal(120, result.Height);
        Assert.Equal(seenAt, result.Timestamp);
    }

    [Fact]
    public void PaymentOutpointUsesCanonicalPaymentIdFormat()
    {
        var txId = uint256.One.ToString();
        var outpoint = FlorestaListener.CreatePaymentOutPoint(txId, 2);

        Assert.Equal($"{txId}-2", outpoint.ToString());
        Assert.DoesNotContain(":", outpoint.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(120, 110, true, 120)]
    [InlineData(null, 110, true, 110)]
    [InlineData(120, 110, false, null)]
    public void ReconnectCatchUpUsesInitializedHeightOrCurrentTip(
        int? initializedHeight,
        int currentTipHeight,
        bool isNetworkCompatible,
        int? expectedHeight)
    {
        var height = FlorestaListener.GetReconnectCatchUpHeight(
            initializedHeight,
            currentTipHeight,
            isNetworkCompatible);

        Assert.Equal(expectedHeight, height);
    }

    [Fact]
    public void FlorestaCapabilitiesDoNotAdvertiseTransactionCheck()
    {
        var capabilities = FlorestaStatusMonitor.CreateNodeCapabilities();

        Assert.True(capabilities.CanScanTxoutSet);
        Assert.True(capabilities.CanSupportSegwit);
        Assert.True(capabilities.CanSupportTaproot);
        Assert.False(capabilities.CanSupportTransactionCheck);
    }

    [Fact]
    public void FlorestaBackendDisablesNetworkPayJoinSupport()
    {
        var network = CreateBitcoinNetwork();

        FlorestaStatusMonitor.DisableUnsupportedNetworkCapabilities(network);

        Assert.False(network.SupportPayJoin);
    }

    [Fact]
    public void FlorestaBackendDisablesStorePayJoin()
    {
        var store = new BTCPayServer.Data.StoreData();
        var blob = store.GetStoreBlob();
        blob.PayJoinEnabled = true;
        store.SetStoreBlob(blob);

        var changed = FlorestaStatusMonitor.DisableStorePayJoin(store);

        Assert.True(changed);
        Assert.False(store.GetStoreBlob().PayJoinEnabled);
    }

    [Fact]
    public void FlorestaBackendLeavesAlreadyDisabledStorePayJoinUnchanged()
    {
        var store = new BTCPayServer.Data.StoreData();

        var changed = FlorestaStatusMonitor.DisableStorePayJoin(store);

        Assert.False(changed);
        Assert.False(store.GetStoreBlob().PayJoinEnabled);
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
