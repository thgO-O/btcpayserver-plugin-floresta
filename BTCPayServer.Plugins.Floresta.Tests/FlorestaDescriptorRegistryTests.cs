using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Logging;
using BTCPayServer.Plugins.Floresta.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NBXplorer;
using NBXplorer.DerivationStrategy;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests;

public class FlorestaDescriptorRegistryTests
{
    [Fact]
    public async Task RegisterAsyncSkipsLoadedDescriptors()
    {
        var context = CreateContext();
        var handler = new RecordingRpcHandler(request => request.Method switch
        {
            "listdescriptors" => new { result = new[] { context.Descriptors.ReceiveDescriptor, context.Descriptors.ChangeDescriptor } },
            _ => throw new InvalidOperationException($"Unexpected RPC method {request.Method}")
        });
        var registry = CreateRegistry(context.NetworkProvider, handler);

        var result = await registry.RegisterAsync("BTC", context.DerivationStrategy, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.AlreadyRegistered);
        Assert.Equal(0, result.Registered);
        Assert.Equal(context.Descriptors.DescriptorHash, result.Descriptors.DescriptorHash);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RegisterAsyncLoadsMissingDescriptors()
    {
        var context = CreateContext();
        var handler = new RecordingRpcHandler(request => request.Method switch
        {
            "listdescriptors" => new { result = new[] { context.Descriptors.ReceiveDescriptor } },
            "loaddescriptor" => new { result = true },
            _ => throw new InvalidOperationException($"Unexpected RPC method {request.Method}")
        });
        var registry = CreateRegistry(context.NetworkProvider, handler);

        var result = await registry.RegisterAsync("BTC", context.DerivationStrategy, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.AlreadyRegistered);
        Assert.Equal(1, result.Registered);
        Assert.Equal(context.Descriptors.ChangeDescriptor, handler.Requests[1].Params.Single());
    }

    [Fact]
    public async Task RegisterAsyncReturnsErrorWhenLoadFails()
    {
        var context = CreateContext();
        var handler = new RecordingRpcHandler(request => request.Method switch
        {
            "listdescriptors" => new { result = Array.Empty<string>() },
            "loaddescriptor" => new { error = new { code = -1, message = "descriptor rejected" } },
            _ => throw new InvalidOperationException($"Unexpected RPC method {request.Method}")
        });
        var registry = CreateRegistry(context.NetworkProvider, handler);

        var result = await registry.RegisterAsync("BTC", context.DerivationStrategy, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.AlreadyRegistered);
        Assert.Equal(0, result.Registered);
        Assert.Contains("descriptor rejected", result.Error);
    }

    private static FlorestaDescriptorRegistry CreateRegistry(
        BTCPayNetworkProvider networkProvider,
        HttpMessageHandler handler)
    {
        var rpcClient = new FlorestaRpcClient(
            new FlorestaSettings { RpcUrl = "http://floresta.test" },
            NullLogger<FlorestaRpcClient>.Instance,
            new HttpClient(handler));
        return new FlorestaDescriptorRegistry(new FlorestaDescriptorService(networkProvider), rpcClient);
    }

    private static DescriptorContext CreateContext()
    {
        var networkProvider = CreateNetworkProvider();
        var btcpayNetwork = networkProvider.GetNetwork<BTCPayNetwork>("BTC")!;
        var xpub = new ExtKey().Neuter().GetWif(Network.Main);
        var derivation = btcpayNetwork.NBXplorerNetwork.DerivationStrategyFactory.CreateDirectDerivationStrategy(
            xpub,
            new DerivationStrategyOptions { ScriptPubKeyType = ScriptPubKeyType.Segwit });
        var derivationStrategy = derivation.ToString();
        var descriptors = new FlorestaDescriptorService(networkProvider).CreateDescriptors("BTC", derivationStrategy);
        return new DescriptorContext(networkProvider, derivationStrategy, descriptors);
    }

    private static BTCPayNetworkProvider CreateNetworkProvider()
    {
        var chainName = ChainName.Mainnet;
        var nbxplorerNetworkProvider = new NBXplorerNetworkProvider(chainName);
        var nbxplorerNetwork = nbxplorerNetworkProvider.GetFromCryptoCode("BTC");
        var btc = new BTCPayNetwork
        {
            CryptoCode = nbxplorerNetwork.CryptoCode,
            DisplayName = "Bitcoin",
            NBXplorerNetwork = nbxplorerNetwork,
            CoinType = new KeyPath("0'"),
            SupportRBF = true,
            SupportPayJoin = true,
            VaultSupported = true
        }.SetDefaultElectrumMapping(chainName);

        return new BTCPayNetworkProvider(
            new BTCPayNetworkBase[] { btc },
            nbxplorerNetworkProvider,
            new Logs());
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

    private sealed record DescriptorContext(
        BTCPayNetworkProvider NetworkProvider,
        string DerivationStrategy,
        FlorestaDescriptorSet Descriptors);
}
