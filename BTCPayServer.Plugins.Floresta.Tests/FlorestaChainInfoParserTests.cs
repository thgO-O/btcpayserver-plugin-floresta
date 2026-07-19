using System.Text.Json;
using BTCPayServer.Plugins.Floresta.Services;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests;

public class FlorestaChainInfoParserTests
{
    public static TheoryData<Network> ElectrumNetworks =>
    [
        Network.Main,
        Network.TestNet,
        Network.RegTest,
        NBitcoin.Bitcoin.Instance.Signet
    ];

    [Fact]
    public void ParsesFlorestaChainInfoFields()
    {
        using var document = JsonDocument.Parse("""
        {
          "height": 123,
          "best_block": "000000000000000000000000000000000000000000000000000000000000abcd",
          "ibd": false,
          "validated": 120,
          "root_count": 8,
          "chain": "regtest"
        }
        """);

        var result = FlorestaChainInfoParser.Parse(document.RootElement);

        Assert.Equal(123, result.Height);
        Assert.Equal("000000000000000000000000000000000000000000000000000000000000abcd", result.BestBlockHash);
        Assert.False(result.IsInitialBlockDownload);
        Assert.Equal(120, result.ValidatedHeight);
        Assert.Equal(8, result.UtreexoRootCount);
        Assert.Equal("regtest", result.Chain);
        Assert.Contains("chain: regtest", FlorestaChainInfoParser.Format(result));
    }

    [Fact]
    public void ParsesBitcoinCoreAliases()
    {
        using var document = JsonDocument.Parse("""
        {
          "headers": "456",
          "blocks": "450",
          "bestblockhash": "000000000000000000000000000000000000000000000000000000000000dcba",
          "initialblockdownload": "true",
          "chain": "bitcoin"
        }
        """);

        var result = FlorestaChainInfoParser.Parse(document.RootElement);

        Assert.Equal(456, result.Height);
        Assert.Equal("000000000000000000000000000000000000000000000000000000000000dcba", result.BestBlockHash);
        Assert.True(result.IsInitialBlockDownload);
        Assert.Equal(450, result.ValidatedHeight);
        Assert.Null(result.UtreexoRootCount);
        Assert.Equal("bitcoin", result.Chain);
    }

    [Theory]
    [InlineData("bitcoin", "mainnet")]
    [InlineData("main", "mainnet")]
    [InlineData("  BiTcOiN  ", "mainnet")]
    [InlineData("testnet", "testnet")]
    [InlineData("test", "testnet")]
    [InlineData("regtest", "regtest")]
    [InlineData("signet", "signet")]
    public void AcceptsMatchingFlorestaNetworks(string reportedChain, string expectedChain)
    {
        var error = FlorestaChainInfoParser.GetNetworkValidationError(
            new FlorestaChainInfo(Chain: reportedChain),
            ParseExpectedChain(expectedChain));

        Assert.Null(error);
    }

    [Fact]
    public void RejectsMismatchedFlorestaNetwork()
    {
        var error = FlorestaChainInfoParser.GetNetworkValidationError(
            new FlorestaChainInfo(Chain: "bitcoin"),
            ChainName.Regtest);

        Assert.Contains("expects Regtest", error);
        Assert.Contains("reports bitcoin", error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsMissingFlorestaNetwork(string? reportedChain)
    {
        var error = FlorestaChainInfoParser.GetNetworkValidationError(
            new FlorestaChainInfo(Chain: reportedChain),
            ChainName.Testnet);

        Assert.Contains("did not report", error);
        Assert.Contains("expects Testnet", error);
    }

    [Fact]
    public void RejectsUnknownFlorestaNetwork()
    {
        var error = FlorestaChainInfoParser.GetNetworkValidationError(
            new FlorestaChainInfo(Chain: "unknownnet"),
            ChainName.Mainnet);

        Assert.Contains("unsupported network 'unknownnet'", error);
        Assert.Contains("expects Mainnet", error);
    }

    [Theory]
    [MemberData(nameof(ElectrumNetworks))]
    public void AcceptsMatchingElectrumGenesis(Network network)
    {
        var features = JsonSerializer.SerializeToElement(new
        {
            genesis_hash = network.GetGenesis().GetHash().ToString()
        });

        var error = FlorestaChainInfoParser.GetElectrumNetworkValidationError(features, network);

        Assert.Null(error);
    }

    [Fact]
    public void AcceptsElectrumGenesisWithDifferentCaseAndSpaces()
    {
        var network = Network.RegTest;
        var features = JsonSerializer.SerializeToElement(new
        {
            genesis_hash = $"  {network.GetGenesis().GetHash().ToString().ToUpperInvariant()}  "
        });

        var error = FlorestaChainInfoParser.GetElectrumNetworkValidationError(features, network);

        Assert.Null(error);
    }

    [Fact]
    public void RejectsAbsentElectrumGenesis()
    {
        var features = JsonSerializer.SerializeToElement(new { hash_function = "sha256" });

        var error = FlorestaChainInfoParser.GetElectrumNetworkValidationError(features, Network.RegTest);

        Assert.Contains("did not report", error);
        Assert.Contains("expects Regtest", error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsMissingElectrumGenesis(string? reportedGenesis)
    {
        var features = JsonSerializer.SerializeToElement(new { genesis_hash = reportedGenesis });

        var error = FlorestaChainInfoParser.GetElectrumNetworkValidationError(features, Network.TestNet);

        Assert.Contains("did not report", error);
        Assert.Contains("expects Testnet", error);
    }

    [Fact]
    public void RejectsInvalidElectrumGenesis()
    {
        var features = JsonSerializer.SerializeToElement(new { genesis_hash = "not-a-hash" });

        var error = FlorestaChainInfoParser.GetElectrumNetworkValidationError(features, Network.Main);

        Assert.Contains("invalid genesis hash 'not-a-hash'", error);
        Assert.Contains("expects Mainnet", error);
    }

    [Fact]
    public void RejectsMismatchedElectrumGenesis()
    {
        var reportedGenesis = Network.Main.GetGenesis().GetHash().ToString();
        var features = JsonSerializer.SerializeToElement(new { genesis_hash = reportedGenesis });

        var error = FlorestaChainInfoParser.GetElectrumNetworkValidationError(features, Network.RegTest);

        Assert.Contains("expects Regtest", error);
        Assert.Contains(reportedGenesis, error);
    }

    private static ChainName ParseExpectedChain(string chain)
    {
        return chain switch
        {
            "mainnet" => ChainName.Mainnet,
            "testnet" => ChainName.Testnet,
            "regtest" => ChainName.Regtest,
            "signet" => NBitcoin.Bitcoin.Instance.Signet.ChainName,
            _ => throw new Xunit.Sdk.XunitException($"Unsupported test chain {chain}")
        };
    }
}
