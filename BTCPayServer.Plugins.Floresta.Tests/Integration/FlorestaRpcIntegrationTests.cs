using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Floresta.Services;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests.Integration;

[Collection(FlorestaIntegrationCollection.Name)]
[Trait("Integration", "Integration")]
public class FlorestaRpcIntegrationTests
{
    private readonly FlorestaIntegrationFixture _fixture;

    public FlorestaRpcIntegrationTests(FlorestaIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetBlockchainInfoAndPingWork()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await _fixture.Rpc.PingAsync(cts.Token);
        var info = await _fixture.Rpc.GetBlockchainInfoAsync(cts.Token);
        var chainInfo = FlorestaChainInfoParser.Parse(info);

        Assert.Equal(JsonValueKind.Object, info.ValueKind);
        Assert.True(chainInfo.Height >= 0);
        Assert.True(chainInfo.ValidatedHeight >= 0);
        Assert.False(string.IsNullOrWhiteSpace(chainInfo.BestBlockHash));
        var expectedNetwork = Network.GetNetwork(_fixture.Settings.Network);
        Assert.NotNull(expectedNetwork);
        Assert.Null(FlorestaChainInfoParser.GetNetworkValidationError(chainInfo, expectedNetwork.ChainName));
    }

    [Fact]
    public async Task LoadDescriptorAndListDescriptorsWork()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var xpub = new ExtKey().Neuter().GetWif(Network.RegTest).ToString();
        var descriptor = $"wpkh({xpub}/0/*)";

        var loaded = await _fixture.Rpc.LoadDescriptorAsync(descriptor, cts.Token);
        var descriptors = await _fixture.Rpc.ListDescriptorsAsync(cts.Token);

        Assert.True(loaded);
        Assert.Contains(descriptor, descriptors);
    }

    [Fact]
    public async Task RescanBlockchainReturnsSuccessOrExplicitNodeStateError()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var exception = await Record.ExceptionAsync(() =>
            _fixture.Rpc.RescanBlockchainAsync(0, null, false, "medium", cts.Token));

        if (exception is null)
            return;

        var message = exception.ToString();
        Assert.Contains("rescanblockchain", message, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            message.Contains("InitialBlockDownload", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("initial block download", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("IBD", StringComparison.OrdinalIgnoreCase),
            $"Unexpected rescan error: {message}");
    }
}
