using System;
using System.Collections.Generic;
using NBitcoin;

namespace BTCPayServer.Plugins.Floresta.Services;

internal static class FlorestaFeePolicy
{
    internal const decimal MaxReasonableFeeRateSatsPerByte = 10_000m;

    public static bool TryCreateFeeRateFromEstimate(
        decimal btcPerKb,
        decimal fallbackSatsPerByte,
        out FeeRate feeRate)
    {
        feeRate = null;
        var satPerByte = btcPerKb * 100_000m;
        if (satPerByte <= 0 || satPerByte > MaxReasonableFeeRateSatsPerByte)
            return false;

        feeRate = new FeeRate(Math.Max(satPerByte, NormalizeFallback(fallbackSatsPerByte)));
        return true;
    }

    public static FeeRate SelectFallbackFeeRate(
        int blockTarget,
        IEnumerable<KeyValuePair<int, (FeeRate Rate, DateTimeOffset CachedAt)>> cache,
        decimal fallbackSatsPerByte)
    {
        FeeRate freshestRate = null;
        var freshestAt = DateTimeOffset.MinValue;

        foreach (var cached in cache)
        {
            if (cached.Value.Rate is null)
                continue;

            if (cached.Key == blockTarget)
                return ClampToFallback(cached.Value.Rate, fallbackSatsPerByte);

            if (cached.Value.CachedAt > freshestAt)
            {
                freshestRate = cached.Value.Rate;
                freshestAt = cached.Value.CachedAt;
            }
        }

        return freshestRate is null
            ? CreateFallbackFeeRate(fallbackSatsPerByte)
            : ClampToFallback(freshestRate, fallbackSatsPerByte);
    }

    public static FeeRate ClampToFallback(FeeRate feeRate, decimal fallbackSatsPerByte)
    {
        var fallback = NormalizeFallback(fallbackSatsPerByte);
        return feeRate.SatoshiPerByte < fallback ? new FeeRate(fallback) : feeRate;
    }

    public static FeeRate CreateFallbackFeeRate(decimal fallbackSatsPerByte)
    {
        return new FeeRate(NormalizeFallback(fallbackSatsPerByte));
    }

    public static decimal NormalizeFallback(decimal fallbackSatsPerByte)
    {
        return fallbackSatsPerByte > 0
            ? fallbackSatsPerByte
            : FlorestaSettings.DefaultFallbackFeeRateSatsPerByte;
    }
}
