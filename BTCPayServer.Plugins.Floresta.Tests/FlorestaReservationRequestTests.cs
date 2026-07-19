using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Floresta.Services;
using NBitcoin;
using NBXplorer;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests;

public class FlorestaReservationRequestTests
{
    [Fact]
    public async Task ParsesExplorerClientCancelReservationPayload()
    {
        var handler = new RecordingRequestHandler();
        var network = new NBXplorerNetworkProvider(ChainName.Regtest).GetFromCryptoCode("BTC");
        var explorerClient = network.CreateExplorerClient(new Uri("http://floresta.test"));
        explorerClient.SetClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://floresta.test")
        });
        explorerClient.SetNoAuth();

        var xpub = new ExtKey().Neuter().GetWif(Network.RegTest);
        var strategy = network.DerivationStrategyFactory.CreateDirectDerivationStrategy(
            xpub,
            new DerivationStrategyOptions { ScriptPubKeyType = ScriptPubKeyType.Segwit });
        var keyPaths = new[] { KeyPath.Parse("0/2"), KeyPath.Parse("1/3") };

        await explorerClient.CancelReservationAsync(strategy, keyPaths);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.EndsWith("/addresses/cancelreservation", handler.Path);

        var parsed = FlorestaHttpHandler.DeserializeReservationKeyPaths(
            handler.Body,
            network.JsonSerializerSettings);

        Assert.Equal(keyPaths.Select(k => k.ToString()), parsed.Select(k => k.ToString()));
    }

    [Fact]
    public void EmptyCancelReservationPayloadYieldsNoKeyPaths()
    {
        var network = new NBXplorerNetworkProvider(ChainName.Regtest).GetFromCryptoCode("BTC");
        var parsed = FlorestaHttpHandler.DeserializeReservationKeyPaths(
            string.Empty,
            network.JsonSerializerSettings);

        Assert.Empty(parsed);
    }

    [Fact]
    public async Task TrackAddressUsesAddressRoute()
    {
        var handler = new RecordingRequestHandler();
        var network = new NBXplorerNetworkProvider(ChainName.Regtest).GetFromCryptoCode("BTC");
        var explorerClient = network.CreateExplorerClient(new Uri("http://floresta.test"));
        explorerClient.SetClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://floresta.test")
        });
        explorerClient.SetNoAuth();
        var address = new Key().PubKey.WitHash.GetAddress(Network.RegTest);

        await explorerClient.TrackAsync(TrackedSource.Create(address));

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.EndsWith($"/addresses/{address}", handler.Path);
        Assert.Equal(string.Empty, handler.Body);
    }

    [Fact]
    public void SanitizesTrackedAddressRouteForLogs()
    {
        var address = new Key().PubKey.WitHash.GetAddress(Network.RegTest).ToString();
        var path = $"/v1/cryptos/BTC/addresses/{address}";

        var sanitized = FlorestaHttpHandler.SanitizePathForLog(path);

        Assert.Equal("/v1/cryptos/BTC/addresses/{address}", sanitized);
        Assert.DoesNotContain(address, sanitized);
    }

    [Fact]
    public void InvalidNetworkBlocksWalletRoutesButKeepsStatusAvailable()
    {
        Assert.True(FlorestaHttpHandler.ShouldBlockForInvalidNetwork(
            HttpMethod.Get,
            "/v1/cryptos/BTC/derivations/wallet/utxos",
            networkCompatible: false));
        Assert.False(FlorestaHttpHandler.ShouldBlockForInvalidNetwork(
            HttpMethod.Get,
            "/v1/cryptos/BTC/status",
            networkCompatible: false));
        Assert.False(FlorestaHttpHandler.ShouldBlockForInvalidNetwork(
            HttpMethod.Post,
            "/v1/cryptos/BTC/transactions",
            networkCompatible: true));
    }

    [Fact]
    public async Task BroadcastTestMempoolAcceptUsesQueryParameter()
    {
        var handler = new RecordingRequestHandler();
        var network = new NBXplorerNetworkProvider(ChainName.Regtest).GetFromCryptoCode("BTC");
        var explorerClient = network.CreateExplorerClient(new Uri("http://floresta.test"));
        explorerClient.SetClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://floresta.test")
        });
        explorerClient.SetNoAuth();

        var tx = Network.RegTest.CreateTransaction();
        tx.Inputs.Add(new OutPoint(uint256.One, 0));
        tx.Outputs.Add(Money.Satoshis(1_000), new Key().PubKey.WitHash.ScriptPubKey);

        try
        {
            await explorerClient.BroadcastAsync(tx, true);
        }
        catch
        {
            // The fake handler only needs to capture the request route for this test.
        }

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.EndsWith("/transactions", handler.Path);
        Assert.Contains("testMempoolAccept=True", handler.Query);
    }

    private sealed class RecordingRequestHandler : HttpMessageHandler
    {
        public HttpMethod Method { get; private set; } = null!;
        public string Path { get; private set; } = string.Empty;
        public string Query { get; private set; } = string.Empty;
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            Path = request.RequestUri?.AbsolutePath ?? string.Empty;
            Query = request.RequestUri?.Query ?? string.Empty;
            var bytes = request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Body = Encoding.UTF8.GetString(bytes);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = Path.EndsWith("/transactions", StringComparison.Ordinal)
                    ? new StringContent("{\"success\":true}", Encoding.UTF8, "application/json")
                    : null
            };
        }
    }
}
