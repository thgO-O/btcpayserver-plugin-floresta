using System.Text.Json;
using BTCPayServer.Plugins.Floresta.Services;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests;

public class FlorestaChainInfoParserTests
{
    [Fact]
    public void ParsesFlorestaChainInfoFields()
    {
        using var document = JsonDocument.Parse("""
        {
          "height": 123,
          "best_block": "000000000000000000000000000000000000000000000000000000000000abcd",
          "ibd": false,
          "validated": 120,
          "root_count": 8
        }
        """);

        var result = FlorestaChainInfoParser.Parse(document.RootElement);

        Assert.Equal(123, result.Height);
        Assert.Equal("000000000000000000000000000000000000000000000000000000000000abcd", result.BestBlockHash);
        Assert.False(result.IsInitialBlockDownload);
        Assert.Equal(120, result.ValidatedHeight);
        Assert.Equal(8, result.UtreexoRootCount);
    }

    [Fact]
    public void ParsesBitcoinCoreAliases()
    {
        using var document = JsonDocument.Parse("""
        {
          "blocks": "456",
          "bestblockhash": "000000000000000000000000000000000000000000000000000000000000dcba",
          "initialblockdownload": "true"
        }
        """);

        var result = FlorestaChainInfoParser.Parse(document.RootElement);

        Assert.Equal(456, result.Height);
        Assert.Equal("000000000000000000000000000000000000000000000000000000000000dcba", result.BestBlockHash);
        Assert.True(result.IsInitialBlockDownload);
        Assert.Null(result.ValidatedHeight);
        Assert.Null(result.UtreexoRootCount);
    }
}
