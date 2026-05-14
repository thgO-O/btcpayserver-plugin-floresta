using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.IntegrationTests;

[Collection(FlorestaIntegrationCollection.Name)]
public class FlorestaElectrumIntegrationTests
{
    private readonly FlorestaIntegrationFixture _fixture;

    public FlorestaElectrumIntegrationTests(FlorestaIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ServerVersionFeaturesHeadersAndFeeWork()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var electrum = _fixture.CreateElectrumClient();
        await electrum.ConnectAsync(cts.Token);

        var version = await electrum.ServerVersionAsync("btcpay-floresta-tests", "1.4", cts.Token);
        var features = await electrum.ServerFeaturesAsync(cts.Token);
        var header = await electrum.HeadersSubscribeAsync(cts.Token);
        var fee = await electrum.EstimateFeeAsync(2, cts.Token);

        Assert.Contains("Floresta", version.serverSoftware, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("1.4", version.protocolVersion);
        Assert.Equal(JsonValueKind.Object, features.ValueKind);
        Assert.Equal("sha256", features.GetProperty("hash_function").GetString());
        Assert.True(header.Height >= 0);
        Assert.False(string.IsNullOrWhiteSpace(header.Hex));
        Assert.True(fee > 0m);
    }

    [Fact]
    public async Task EmptyScriptHashQueriesWork()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var electrum = _fixture.CreateElectrumClient();
        await electrum.ConnectAsync(cts.Token);
        var script = Script.FromHex("0014751e76e8199196d454941c45d1b3a323f1433bd6");
        var scriptHash = ScriptHashUtility.ComputeScriptHash(script);

        var status = await electrum.ScripthashSubscribeAsync(scriptHash, cts.Token);
        var history = await electrum.ScripthashGetHistoryAsync(scriptHash, cts.Token);
        var unspent = await electrum.ScripthashListUnspentAsync(scriptHash, cts.Token);
        var balance = await electrum.ScripthashGetBalanceAsync(scriptHash, cts.Token);

        Assert.Null(status);
        Assert.Empty(history);
        Assert.Empty(unspent);
        Assert.Equal(0, balance.Confirmed);
        Assert.Equal(0, balance.Unconfirmed);
    }

    [Fact]
    public async Task InvalidBroadcastReturnsExplicitElectrumError()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var electrum = _fixture.CreateElectrumClient();
        await electrum.ConnectAsync(cts.Token);

        await Assert.ThrowsAsync<FlorestaElectrumException>(() =>
            electrum.TransactionBroadcastAsync("00", cts.Token));
    }
}
