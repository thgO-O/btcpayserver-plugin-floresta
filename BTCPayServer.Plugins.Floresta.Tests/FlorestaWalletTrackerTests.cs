using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests;

public class FlorestaWalletTrackerTests
{
    [Fact]
    public void NormalizeUtxoPositionCorrectsWrongElectrumTxPos()
    {
        var tx = Transaction.Create(Network.RegTest);
        var changeScript = BitcoinAddress
            .Create("bcrt1qwt7s26ggkvpd9ajslefzjkc99p2n4sm8sv6sne", Network.RegTest)
            .ScriptPubKey;
        var receiveScript = BitcoinAddress
            .Create("bcrt1q4pyhtepalxulsur92xcm9hhl0lv9wrnfkfmf02", Network.RegTest)
            .ScriptPubKey;

        tx.Outputs.Add(Money.Satoshis(49_998_590), changeScript);
        tx.Outputs.Add(Money.Satoshis(50_000_000), receiveScript);

        var utxo = new FlorestaElectrumUnspentItem
        {
            TxHash = tx.GetHash().ToString(),
            TxPos = 1,
            Value = 49_998_590,
            Height = 110
        };

        var normalized = FlorestaWalletTracker.NormalizeUtxoPosition(
            utxo,
            tx,
            changeScript.ToBytes());

        Assert.Equal(0, normalized.TxPos);
        Assert.Equal(49_998_590, normalized.Value);
        Assert.Equal(utxo.TxHash, normalized.TxHash);
        Assert.Equal(utxo.Height, normalized.Height);
    }

    [Fact]
    public void NormalizeUtxoPositionUsesValueWhenScriptAppearsMoreThanOnce()
    {
        var tx = Transaction.Create(Network.RegTest);
        var script = BitcoinAddress
            .Create("bcrt1qwt7s26ggkvpd9ajslefzjkc99p2n4sm8sv6sne", Network.RegTest)
            .ScriptPubKey;

        tx.Outputs.Add(Money.Satoshis(1_000), script);
        tx.Outputs.Add(Money.Satoshis(2_000), script);

        var utxo = new FlorestaElectrumUnspentItem
        {
            TxHash = tx.GetHash().ToString(),
            TxPos = 0,
            Value = 2_000,
            Height = 110
        };

        var normalized = FlorestaWalletTracker.NormalizeUtxoPosition(
            utxo,
            tx,
            script.ToBytes());

        Assert.Equal(1, normalized.TxPos);
        Assert.Equal(2_000, normalized.Value);
    }
}
