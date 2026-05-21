using System;
using System.Collections.Generic;
using BTCPayServer.Services;

namespace BTCPayServer.Plugins.Floresta.Services;

/// <summary>
/// Replaces FeeProviderFactory. Returns FlorestaFeeProvider for BTC only.
/// </summary>
public class FlorestaFeeProviderFactory : IFeeProviderFactory
{
    private readonly Dictionary<string, IFeeProvider> _providers = new();

    public FlorestaFeeProviderFactory(
        BTCPayNetworkProvider networkProvider,
        FlorestaFeeProvider feeProvider)
    {
        var network = networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        if (network is not null)
            _providers[network.CryptoCode.ToUpperInvariant()] = feeProvider;
    }

    public IFeeProvider CreateFeeProvider(BTCPayNetworkBase network)
    {
        if (_providers.TryGetValue(network.CryptoCode.ToUpperInvariant(), out var provider))
            return provider;
        throw new NotSupportedException($"No fee provider for {network.CryptoCode}");
    }
}
