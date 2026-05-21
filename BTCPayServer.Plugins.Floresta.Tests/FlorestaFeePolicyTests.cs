using System;
using System.Collections.Generic;
using BTCPayServer.Plugins.Floresta.Services;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests;

public class FlorestaFeePolicyTests
{
    [Fact]
    public void ConvertsValidEstimateFromBtcPerKbToSatPerByte()
    {
        var success = FlorestaFeePolicy.TryCreateFeeRateFromEstimate(0.0002m, 1.0m, out var result);

        Assert.True(success);
        Assert.Equal(20.0m, result.SatoshiPerByte);
    }

    [Fact]
    public void ClampsValidEstimateToConfiguredFallback()
    {
        var success = FlorestaFeePolicy.TryCreateFeeRateFromEstimate(0.000005m, 2.0m, out var result);

        Assert.True(success);
        Assert.Equal(2.0m, result.SatoshiPerByte);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.0001)]
    [InlineData(1)]
    public void RejectsInvalidEstimates(decimal btcPerKb)
    {
        var success = FlorestaFeePolicy.TryCreateFeeRateFromEstimate(btcPerKb, 1.0m, out var result);

        Assert.False(success);
        Assert.Null(result);
    }

    [Fact]
    public void UsesSameTargetCachedFeeBeforeFresherOtherTarget()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new Dictionary<int, (FeeRate Rate, DateTimeOffset CachedAt)>
        {
            [6] = (new FeeRate(4.0m), now.AddMinutes(-10)),
            [12] = (new FeeRate(9.0m), now)
        };

        var result = FlorestaFeePolicy.SelectFallbackFeeRate(6, cache, 1.0m);

        Assert.Equal(4.0m, result.SatoshiPerByte);
    }

    [Fact]
    public void UsesFreshestCachedFeeWhenTargetCacheIsEmpty()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new Dictionary<int, (FeeRate Rate, DateTimeOffset CachedAt)>
        {
            [6] = (new FeeRate(4.0m), now.AddMinutes(-10)),
            [12] = (new FeeRate(9.0m), now)
        };

        var result = FlorestaFeePolicy.SelectFallbackFeeRate(2, cache, 1.0m);

        Assert.Equal(9.0m, result.SatoshiPerByte);
    }

    [Fact]
    public void UsesConfiguredFallbackWhenCacheIsEmpty()
    {
        var result = FlorestaFeePolicy.SelectFallbackFeeRate(
            2,
            new Dictionary<int, (FeeRate Rate, DateTimeOffset CachedAt)>(),
            3.5m);

        Assert.Equal(3.5m, result.SatoshiPerByte);
    }
}
