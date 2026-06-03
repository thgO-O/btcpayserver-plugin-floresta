using System.Linq;
using BTCPayServer.Plugins.Floresta.Data;
using Microsoft.EntityFrameworkCore;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests;

public class FlorestaWalletTrackerTests
{
    [Fact]
    public void TrackedAddressOwnersUseWalletAndScripthashKey()
    {
        var options = new DbContextOptionsBuilder<FlorestaDbContext>()
            .UseNpgsql("Host=localhost;Database=btcpayserver")
            .Options;
        using var ctx = new FlorestaDbContext(options);

        var entity = ctx.Model.FindEntityType(typeof(TrackedAddressOwner));
        var key = entity!.FindPrimaryKey()!.Properties.Select(p => p.Name).ToArray();

        Assert.Equal(new[] { "WalletId", "Scripthash" }, key);
    }

    [Fact]
    public void AddressSourceKeepsRequestedWalletOwnerForKnownScript()
    {
        var address = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        var script = address.ScriptPubKey;
        var existing = new TrackedAddress
        {
            Scripthash = ScriptHashUtility.ComputeScriptHash(script),
            WalletId = "derivation-wallet",
            KeyPath = "0/4",
            ScriptPubKey = script.ToBytes(),
            Address = address.ToString(),
            IsChange = false,
            IsReserved = true,
            IsUsed = true
        };

        var source = FlorestaWalletTracker.CreateAddressSource(existing, address.ToString());

        Assert.Equal(existing.Scripthash, source.Scripthash);
        Assert.Equal(address.ToString(), source.WalletId);
        Assert.Null(source.KeyPath);
        Assert.False(source.IsReserved);
        Assert.True(source.IsUsed);
        Assert.Equal(existing.ScriptPubKey, source.ScriptPubKey);
    }

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
