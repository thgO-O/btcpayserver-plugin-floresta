using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.IntegrationTests;

[Collection(FlorestaIntegrationCollection.Name)]
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

        Assert.Equal(JsonValueKind.Object, info.ValueKind);
        Assert.True(info.GetProperty("height").GetInt32() >= 0);
        Assert.True(info.GetProperty("validated").GetInt32() >= 0);
        Assert.False(string.IsNullOrWhiteSpace(info.GetProperty("best_block").GetString()));
        Assert.Equal(_fixture.Settings.Network, info.GetProperty("chain").GetString(), ignoreCase: true);
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
