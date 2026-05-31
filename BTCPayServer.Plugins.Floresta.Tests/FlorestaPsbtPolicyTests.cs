using BTCPayServer.Plugins.Floresta.Services;
using NBitcoin;
using NBXplorer;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests;

public class FlorestaPsbtPolicyTests
{
    [Fact]
    public void AppliesLockTimeAndTransactionVersionToCreatedPsbt()
    {
        var (builder, destinationScript) = CreateFundedBuilder();
        var request = new CreatePSBTRequest
        {
            LockTime = new LockTime(123),
            Version = 2
        };

        FlorestaHttpHandler.ApplyPsbtBuilderOptions(builder, request);
        FlorestaHttpHandler.ApplyPsbtDestination(builder, new CreatePSBTDestination
        {
            Destination = destinationScript,
            Amount = Money.Satoshis(50_000)
        });
        builder.SendFees(Money.Satoshis(1_000));

        var psbt = FlorestaHttpHandler.BuildPsbt(builder, request);
        var transaction = psbt.GetGlobalTransaction();

        Assert.Equal(2u, transaction.Version);
        Assert.Equal(new LockTime(123), transaction.LockTime);
    }

    [Fact]
    public void SubstractFeesReducesTheMarkedDestinationOutput()
    {
        var (builder, destinationScript) = CreateFundedBuilder();

        FlorestaHttpHandler.ApplyPsbtDestination(builder, new CreatePSBTDestination
        {
            Destination = destinationScript,
            Amount = Money.Satoshis(50_000),
            SubstractFees = true
        });
        builder.SendFees(Money.Satoshis(1_000));

        var transaction = builder.BuildTransaction(false);
        var destinationOutput = Assert.Single(transaction.Outputs, o => o.ScriptPubKey == destinationScript);

        Assert.Equal(Money.Satoshis(49_000), destinationOutput.Value);
    }

    [Fact]
    public void AddsGlobalXPubOnlyWhenRequested()
    {
        var (builder, destinationScript) = CreateFundedBuilder();
        FlorestaHttpHandler.ApplyPsbtDestination(builder, new CreatePSBTDestination
        {
            Destination = destinationScript,
            Amount = Money.Satoshis(50_000)
        });
        builder.SendFees(Money.Satoshis(1_000));
        var psbt = FlorestaHttpHandler.BuildPsbt(builder, new CreatePSBTRequest());

        var network = new NBXplorerNetworkProvider(ChainName.Regtest).GetFromCryptoCode("BTC");
        var xpub = new ExtKey().Neuter().GetWif(Network.RegTest);
        var strategy = network.DerivationStrategyFactory.CreateDirectDerivationStrategy(
            xpub,
            new DerivationStrategyOptions { ScriptPubKeyType = ScriptPubKeyType.Segwit });

        FlorestaHttpHandler.ApplyPsbtGlobalXPub(psbt, strategy, false, Network.RegTest);
        Assert.Empty(psbt.GlobalXPubs);

        FlorestaHttpHandler.ApplyPsbtGlobalXPub(psbt, strategy, true, Network.RegTest);
        Assert.Single(psbt.GlobalXPubs);
    }

    private static (TransactionBuilder Builder, Script DestinationScript) CreateFundedBuilder()
    {
        var network = Network.RegTest;
        var fundingScript = new Key().PubKey.WitHash.ScriptPubKey;
        var changeScript = new Key().PubKey.WitHash.ScriptPubKey;
        var destinationScript = new Key().PubKey.WitHash.ScriptPubKey;
        var previousTxId = uint256.Parse("0000000000000000000000000000000000000000000000000000000000000001");
        var coin = new Coin(
            new OutPoint(previousTxId, 0),
            new TxOut(Money.Satoshis(100_000), fundingScript));
        var builder = network.CreateTransactionBuilder();
        builder.AddCoins(coin);
        builder.SetChange(changeScript);
        return (builder, destinationScript);
    }
}
