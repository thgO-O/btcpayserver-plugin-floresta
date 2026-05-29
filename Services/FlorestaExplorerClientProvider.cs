using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using BTCPayServer.Configuration;
using BTCPayServer.HostedServices;
using BTCPayServer.Logging;
using Microsoft.Extensions.Options;
using NBXplorer;

namespace BTCPayServer.Plugins.Floresta.Services;

/// <summary>
/// Keeps the normal NBXplorer clients for non-BTC networks and replaces only
/// the BTC client with the Floresta HTTP shim.
/// </summary>
public class FlorestaExplorerClientProvider : ExplorerClientProvider
{
    public FlorestaExplorerClientProvider(
        IHttpClientFactory httpClientFactory,
        BTCPayNetworkProvider networkProvider,
        IOptions<NBXplorerOptions> nbXplorerOptions,
        NBXplorerDashboard dashboard,
        Logs logs,
        FlorestaHttpHandler handler)
        : base(httpClientFactory, networkProvider, nbXplorerOptions, dashboard, logs)
    {
        var network = networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        if (network is not null)
        {
            var httpClient = new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://electrum-shim.internal"),
                Timeout = TimeSpan.FromMinutes(5)
            };

            var explorerClient = network.NBXplorerNetwork.CreateExplorerClient(
                new Uri("http://electrum-shim.internal"));
            explorerClient.SetClient(httpClient);
            explorerClient.SetNoAuth();

            _Clients[network.CryptoCode.ToUpperInvariant()] = explorerClient;
        }
    }

    public override IEnumerable<(BTCPayNetwork, ExplorerClient)> GetAll()
    {
        return base.GetAll().Where(pair => !pair.Item1.IsBTC);
    }
}
