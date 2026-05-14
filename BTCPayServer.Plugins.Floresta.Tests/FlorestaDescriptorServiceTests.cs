using System;
using BTCPayServer.Logging;
using NBXplorer;
using NBXplorer.DerivationStrategy;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests;

public class FlorestaDescriptorServiceTests
{
    [Theory]
    [InlineData(ScriptPubKeyType.Segwit, "wpkh")]
    [InlineData(ScriptPubKeyType.Legacy, "pkh")]
    [InlineData(ScriptPubKeyType.SegwitP2SH, "sh-wpkh")]
    public void CreatesReceiveAndChangeDescriptorsForSingleSig(ScriptPubKeyType scriptPubKeyType, string wrapper)
    {
        var networkProvider = CreateNetworkProvider("mainnet");
        var btcpayNetwork = networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        var factory = btcpayNetwork.NBXplorerNetwork.DerivationStrategyFactory;
        var xpub = new ExtKey().Neuter().GetWif(Network.Main);
        var derivation = factory.CreateDirectDerivationStrategy(xpub, new DerivationStrategyOptions
        {
            ScriptPubKeyType = scriptPubKeyType
        });
        var service = new FlorestaDescriptorService(networkProvider);

        var descriptors = service.CreateDescriptors("BTC", derivation.ToString());
        var xpubString = xpub.ToString();

        Assert.Equal(derivation.ToString(), descriptors.DerivationStrategy);
        Assert.Equal(ExpectedDescriptor(wrapper, xpubString, 0), descriptors.ReceiveDescriptor);
        Assert.Equal(ExpectedDescriptor(wrapper, xpubString, 1), descriptors.ChangeDescriptor);
        Assert.Matches("^[0-9a-f]{64}$", descriptors.DescriptorHash);
    }

    [Theory]
    [InlineData("mainnet", "Mainnet")]
    [InlineData("testnet", "Testnet")]
    [InlineData("regtest", "Regtest")]
    public void SupportsBitcoinNetworks(string chainName, string expectedChainName)
    {
        var networkProvider = CreateNetworkProvider(chainName);
        var service = new FlorestaDescriptorService(networkProvider);
        var network = networkProvider.GetNetwork<BTCPayNetwork>("BTC").NBitcoinNetwork;
        var xpub = new ExtKey().Neuter().GetWif(network);
        var derivation = networkProvider
            .GetNetwork<BTCPayNetwork>("BTC")
            .NBXplorerNetwork
            .DerivationStrategyFactory
            .CreateDirectDerivationStrategy(xpub, new DerivationStrategyOptions
            {
                ScriptPubKeyType = ScriptPubKeyType.Segwit
            });

        var descriptors = service.CreateDescriptors("BTC", derivation.ToString());

        Assert.Equal(expectedChainName, network.ChainName.ToString());
        Assert.StartsWith("wpkh(", descriptors.ReceiveDescriptor);
    }

    [Fact]
    public void RejectsMultisigInMvp()
    {
        var networkProvider = CreateNetworkProvider("mainnet");
        var network = networkProvider.GetNetwork<BTCPayNetwork>("BTC").NBitcoinNetwork;
        var factory = networkProvider.GetNetwork<BTCPayNetwork>("BTC").NBXplorerNetwork.DerivationStrategyFactory;
        var first = new ExtKey().Neuter().GetWif(network);
        var second = new ExtKey().Neuter().GetWif(network);
        var derivation = factory.CreateMultiSigDerivationStrategy(
            new[] { first, second },
            2,
            new DerivationStrategyOptions { ScriptPubKeyType = ScriptPubKeyType.Segwit });
        var service = new FlorestaDescriptorService(networkProvider);

        var exception = Assert.Throws<NotSupportedException>(() => service.CreateDescriptors("BTC", derivation.ToString()));
        Assert.Contains("single-sig", exception.Message);
    }

    private static string ExpectedDescriptor(string wrapper, string xpub, int branch)
    {
        return wrapper switch
        {
            "wpkh" => $"wpkh({xpub}/{branch}/*)",
            "pkh" => $"pkh({xpub}/{branch}/*)",
            "sh-wpkh" => $"sh(wpkh({xpub}/{branch}/*))",
            _ => throw new ArgumentOutOfRangeException(nameof(wrapper), wrapper, null)
        };
    }

    private static BTCPayNetworkProvider CreateNetworkProvider(string network)
    {
        var chainName = network.ToLowerInvariant() switch
        {
            "mainnet" => ChainName.Mainnet,
            "testnet" => ChainName.Testnet,
            "regtest" => ChainName.Regtest,
            _ => throw new ArgumentOutOfRangeException(nameof(network), network, null)
        };
        var nbxplorerNetworkProvider = new NBXplorerNetworkProvider(chainName);
        var nbxplorerNetwork = nbxplorerNetworkProvider.GetFromCryptoCode("BTC");
        var btc = new BTCPayNetwork
        {
            CryptoCode = nbxplorerNetwork.CryptoCode,
            DisplayName = "Bitcoin",
            NBXplorerNetwork = nbxplorerNetwork,
            CoinType = chainName == ChainName.Mainnet ? new KeyPath("0'") : new KeyPath("1'"),
            SupportRBF = true,
            SupportPayJoin = true,
            VaultSupported = true
        }.SetDefaultElectrumMapping(chainName);

        return new BTCPayNetworkProvider(
            new BTCPayNetworkBase[] { btc },
            nbxplorerNetworkProvider,
            new Logs());
    }
}
