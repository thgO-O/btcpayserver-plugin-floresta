using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Wallets;
using StoreData = BTCPayServer.Data.StoreData;

namespace BTCPayServer.Plugins.Floresta.Services;

public class FlorestaWalletHistogramService : WalletHistogramService
{
    private readonly SettingsRepository _settingsRepository;

    public FlorestaWalletHistogramService(
        PaymentMethodHandlerDictionary handlers,
        NBXplorerConnectionFactory connectionFactory,
        SettingsRepository settingsRepository)
        : base(handlers, connectionFactory)
    {
        _settingsRepository = settingsRepository;
    }

    public override async Task<HistogramData> GetHistogram(StoreData store, WalletId walletId, HistogramType type)
    {
        if (string.Equals(walletId.CryptoCode, "BTC", System.StringComparison.OrdinalIgnoreCase) &&
            await IsFlorestaBitcoinBackendActiveAsync())
        {
            return null;
        }

        return await base.GetHistogram(store, walletId, type);
    }

    private async Task<bool> IsFlorestaBitcoinBackendActiveAsync()
    {
        var settings = await _settingsRepository.GetSettingAsync<FlorestaSettings>() ?? new FlorestaSettings();
        return settings.IsBitcoinBackendActive();
    }
}
