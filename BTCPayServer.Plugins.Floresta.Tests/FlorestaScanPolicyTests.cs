using BTCPayServer.Plugins.Floresta.Services;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests;

public class FlorestaScanPolicyTests
{
    [Fact]
    public void ScanParametersDefaultToBoundedValues()
    {
        var scan = FlorestaHttpHandler.NormalizeScanParameters(null, null, null);

        Assert.Equal(FlorestaHttpHandler.DefaultScanGapLimit, scan.GapLimit);
        Assert.Equal(0, scan.From);
        Assert.Equal(FlorestaHttpHandler.DefaultScanBatchSize, scan.BatchSize);
    }

    [Fact]
    public void ScanParametersAreClamped()
    {
        var scan = FlorestaHttpHandler.NormalizeScanParameters(
            int.MaxValue,
            -12,
            int.MaxValue);

        Assert.Equal(FlorestaSettings.MaxGapLimit, scan.GapLimit);
        Assert.Equal(0, scan.From);
        Assert.Equal(FlorestaHttpHandler.MaxScanBatchSize, scan.BatchSize);
    }
}
