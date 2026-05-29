using System;
using BTCPayServer.Services;
using BTCPayServer.Services.Fees;

namespace BTCPayServer.Plugins.Floresta.Services;

/// <summary>
/// Returns FlorestaFeeProvider for BTC and delegates non-BTC networks to BTCPay's default factory.
/// </summary>
public class FlorestaFeeProviderFactory : IFeeProviderFactory
{
    private readonly string _btcCryptoCode;
    private readonly FlorestaFeeProvider _btcFeeProvider;
    private readonly FeeProviderFactory _fallbackFactory;

    public FlorestaFeeProviderFactory(
        BTCPayNetworkProvider networkProvider,
        FlorestaFeeProvider feeProvider,
        FeeProviderFactory fallbackFactory)
    {
        var network = networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        _btcCryptoCode = network?.CryptoCode.ToUpperInvariant() ?? "BTC";
        _btcFeeProvider = feeProvider;
        _fallbackFactory = fallbackFactory;
    }

    public IFeeProvider CreateFeeProvider(BTCPayNetworkBase network)
    {
        if (string.Equals(network.CryptoCode, _btcCryptoCode, StringComparison.OrdinalIgnoreCase))
            return _btcFeeProvider;

        return _fallbackFactory.CreateFeeProvider(network);
    }
}
