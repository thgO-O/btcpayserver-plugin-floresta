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

    private sealed class RecordingRequestHandler : HttpMessageHandler
    {
        public HttpMethod Method { get; private set; } = null!;
        public string Path { get; private set; } = string.Empty;
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            Path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var bytes = request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Body = Encoding.UTF8.GetString(bytes);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
