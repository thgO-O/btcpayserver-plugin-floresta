using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests;

public class FlorestaRpcClientTests
{
    [Fact]
    public async Task ListDescriptorsAsyncParsesResult()
    {
        var handler = new RecordingRpcHandler(_ => new
        {
            result = new[] { "wpkh(xpub/0/*)", "wpkh(xpub/1/*)" }
        });
        var client = CreateClient(handler);

        var descriptors = await client.ListDescriptorsAsync(CancellationToken.None);

        Assert.Equal(new[] { "wpkh(xpub/0/*)", "wpkh(xpub/1/*)" }, descriptors);
        Assert.Single(handler.Requests);
        Assert.Equal("listdescriptors", handler.Requests[0].Method);
        Assert.Empty(handler.Requests[0].Params);
    }

    [Fact]
    public async Task LoadDescriptorAsyncSendsDescriptor()
    {
        var handler = new RecordingRpcHandler(_ => new { result = true });
        var client = CreateClient(handler);

        var result = await client.LoadDescriptorAsync("wpkh(xpub/0/*)", CancellationToken.None);

        Assert.True(result);
        Assert.Single(handler.Requests);
        Assert.Equal("loaddescriptor", handler.Requests[0].Method);
        Assert.Equal("wpkh(xpub/0/*)", Assert.Single(handler.Requests[0].Params));
    }

    [Fact]
    public async Task ThrowsOnRpcError()
    {
        var handler = new RecordingRpcHandler(_ => new
        {
            error = new { code = -1, message = "descriptor rejected" }
        });
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<FlorestaRpcException>(() =>
            client.LoadDescriptorAsync("invalid", CancellationToken.None));

        Assert.Contains("loaddescriptor", ex.Message);
        Assert.Contains("descriptor rejected", ex.Message);
    }

    private static FlorestaRpcClient CreateClient(HttpMessageHandler handler)
    {
        return new FlorestaRpcClient(
            new FlorestaSettings { RpcUrl = "http://floresta.test" },
            NullLogger<FlorestaRpcClient>.Instance,
            new HttpClient(handler));
    }

    private sealed class RecordingRpcHandler : HttpMessageHandler
    {
        private readonly Func<RpcRequest, object> _responseFactory;

        public List<RpcRequest> Requests { get; } = new();

        public RecordingRpcHandler(Func<RpcRequest, object> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var rpcRequest = new RpcRequest(
                root.GetProperty("method").GetString()!,
                root.GetProperty("params").EnumerateArray().Select(e => e.GetString()).ToArray());
            Requests.Add(rpcRequest);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(_responseFactory(rpcRequest)))
            };
        }
    }

    private sealed record RpcRequest(string Method, string?[] Params);
}
