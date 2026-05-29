using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using BTCPayServer.Services;
using NBitcoin;

namespace BTCPayServer.Plugins.Floresta.Services;

/// <summary>
/// Fee estimation via Electrum's blockchain.estimatefee.
/// Caches results for 30 seconds.
/// </summary>
public class FlorestaFeeProvider : IFeeProvider
{
    private readonly FlorestaElectrumClient _client;
    private readonly SettingsRepository _settingsRepository;
    private readonly ConcurrentDictionary<int, (FeeRate Rate, DateTimeOffset CachedAt)> _cache = new();
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    public FlorestaFeeProvider(
        FlorestaElectrumClient client,
        SettingsRepository settingsRepository)
    {
        _client = client;
        _settingsRepository = settingsRepository;
    }

    public async Task<FeeRate> GetFeeRateAsync(int blockTarget = 20)
    {
        var settings = await _settingsRepository.GetSettingAsync<FlorestaSettings>() ?? new FlorestaSettings();
        if (!settings.IsBitcoinBackendActive())
            throw new InvalidOperationException("Floresta Bitcoin backend is disabled.");

        var fallback = settings.FallbackFeeRateSatsPerByte;

        if (_cache.TryGetValue(blockTarget, out var cached) &&
            DateTimeOffset.UtcNow - cached.CachedAt < CacheDuration)
        {
            return FlorestaFeePolicy.ClampToFallback(cached.Rate, fallback);
        }

        try
        {
            var btcPerKb = await _client.EstimateFeeAsync(blockTarget, default);
            if (!FlorestaFeePolicy.TryCreateFeeRateFromEstimate(btcPerKb, fallback, out var rate))
                return FlorestaFeePolicy.SelectFallbackFeeRate(blockTarget, _cache, fallback);

            _cache[blockTarget] = (rate, DateTimeOffset.UtcNow);
            return rate;
        }
        catch
        {
            return FlorestaFeePolicy.SelectFallbackFeeRate(blockTarget, _cache, fallback);
        }
    }
}
