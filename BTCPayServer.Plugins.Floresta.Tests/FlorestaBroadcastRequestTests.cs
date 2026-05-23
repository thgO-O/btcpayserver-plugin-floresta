using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using NBXplorer;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests;

public class FlorestaBroadcastRequestTests
{
    private const string RawTransactionHex =
        "01000000000101cec969f70c85904dbe8d8191a97e55d96c22edb4276f13a029d7480dadc662b00100000000fdffffff0210270000000000001600143139d39e875a7659fdd7a7da3000ac4877d39cae0e5a01000000000016001472fd056908b302d2f650fe52295b0528553ac36702473044022065c155b6b9203ef4a5fea793361592a2d3ca92f4caa6e2f96950fc3991a03ebe02200960cd124522d4bf6be2cd560097540550c877c893d004003d7a078523ff15c90121035f7a41a81bbfdc34a77cd7996aced4631c886eb7a71e35a52ded10679e7bbd0e00000000";

    [Theory]
    [InlineData(RawTransactionHex)]
    [InlineData("\"" + RawTransactionHex + "\"")]
    [InlineData("{\"transaction\":\"" + RawTransactionHex + "\"}")]
    [InlineData("{\"Transaction\":\"" + RawTransactionHex + "\"}")]
    [InlineData("{\"hex\":\"" + RawTransactionHex + "\"}")]
    [InlineData("{\"Transaction\":{\"Hex\":\"" + RawTransactionHex + "\"}}")]
    public void ExtractsRawTransactionHexFromBroadcastBody(string body)
    {
        var extracted = FlorestaWalletTracker.TryExtractRawTransactionHex(body, out var rawTx, out var error);

        Assert.True(extracted, error);
        Assert.Equal(RawTransactionHex, rawTx);
    }

    [Fact]
    public void RejectsPsbtInsteadOfPassingItToFloresta()
    {
        var extracted = FlorestaWalletTracker.TryExtractRawTransactionHex(
            "cHNidP8BAHEBAAAAAc7JafcMhZBNvo2Bkal+VdlsIu20J28ToCnXSA2txmKwAQAAAAD9////",
            out _,
            out var error);

        Assert.False(extracted);
        Assert.Equal("Broadcast request did not contain raw transaction hex.", error);
    }

    [Fact]
    public async Task ExtractsRawTransactionHexFromExplorerClientBroadcastPayload()
    {
        var handler = new RecordingBroadcastHandler();
        var network = new NBXplorerNetworkProvider(ChainName.Regtest).GetFromCryptoCode("BTC");
        var explorerClient = network.CreateExplorerClient(new Uri("http://floresta.test"));
        explorerClient.SetClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://floresta.test")
        });
        explorerClient.SetNoAuth();

        try
        {
            await explorerClient.BroadcastAsync(Transaction.Parse(RawTransactionHex, Network.RegTest));
        }
        catch
        {
            // The fake handler only needs to capture the request body for this test.
        }

        var extracted = FlorestaWalletTracker.TryExtractRawTransactionHex(handler.BodyBytes, out var rawTx, out var error);

        Assert.True(extracted, $"Payload was: {handler.Body}. Error: {error}");
        Assert.Equal(RawTransactionHex, rawTx);
    }

    private sealed class RecordingBroadcastHandler : HttpMessageHandler
    {
        public byte[] BodyBytes { get; private set; } = [];
        public string Body => Encoding.UTF8.GetString(BodyBytes);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            BodyBytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"success\":true}")
            };
        }
    }
}
