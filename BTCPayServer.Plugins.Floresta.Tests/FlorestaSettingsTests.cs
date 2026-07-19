using System;
using BTCPayServer.Plugins.Floresta.Controllers;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests;

public class FlorestaSettingsTests
{
    private const string FallbackFeeVariable = "FLORESTA_FALLBACK_FEE_SAT_PER_VB";
    private static readonly object EnvLock = new();

    [Fact]
    public void ReadsFallbackFeeRateFromEnvironment()
    {
        WithEnvironment(FallbackFeeVariable, "2.5", () =>
        {
            var settings = new FlorestaSettings();

            Assert.Equal(2.5m, settings.FallbackFeeRateSatsPerByte);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("-2")]
    public void InvalidFallbackFeeRateEnvironmentUsesDefault(string value)
    {
        WithEnvironment(FallbackFeeVariable, value, () =>
        {
            var settings = new FlorestaSettings();

            Assert.Equal(FlorestaSettings.DefaultFallbackFeeRateSatsPerByte, settings.FallbackFeeRateSatsPerByte);
        });
    }

    [Fact]
    public void PreserveSecretsFromKeepsExistingPasswordWhenPostedPasswordIsBlank()
    {
        var posted = new FlorestaSettings { RpcPassword = "" };
        var existing = new FlorestaSettings { RpcPassword = "secret" };

        posted.PreserveSecretsFrom(existing);

        Assert.Equal("secret", posted.RpcPassword);
    }

    [Fact]
    public void PreserveSecretsFromKeepsExistingPasswordWhenPostedPasswordIsNull()
    {
        var posted = new FlorestaSettings { RpcPassword = null };
        var existing = new FlorestaSettings { RpcPassword = "secret" };

        posted.PreserveSecretsFrom(existing);

        Assert.Equal("secret", posted.RpcPassword);
    }

    [Fact]
    public void IsBitcoinBackendActiveRequiresStartupGateAndSavedSettings()
    {
        EnvironmentVariableTestHelper.WithBackendReplacementEnvironment("true", () =>
        {
            Assert.True(new FlorestaSettings
            {
                Enabled = true,
                UseFlorestaAsBitcoinBackend = true,
                CryptoCode = "BTC"
            }.IsBitcoinBackendActive());

            Assert.False(new FlorestaSettings
            {
                Enabled = false,
                UseFlorestaAsBitcoinBackend = true,
                CryptoCode = "BTC"
            }.IsBitcoinBackendActive());

            Assert.False(new FlorestaSettings
            {
                Enabled = true,
                UseFlorestaAsBitcoinBackend = false,
                CryptoCode = "BTC"
            }.IsBitcoinBackendActive());

            Assert.False(new FlorestaSettings
            {
                Enabled = true,
                UseFlorestaAsBitcoinBackend = true,
                CryptoCode = "LTC"
            }.IsBitcoinBackendActive());
        });

        EnvironmentVariableTestHelper.WithBackendReplacementEnvironment(null, () =>
        {
            Assert.False(new FlorestaSettings
            {
                Enabled = true,
                UseFlorestaAsBitcoinBackend = true,
                CryptoCode = "BTC"
            }.IsBitcoinBackendActive());
        });
    }

    [Fact]
    public void ElectrumEndpointComparisonIgnoresHostCase()
    {
        var existing = new FlorestaSettings
        {
            ElectrumHost = "FLORESTA",
            ElectrumPort = 50001,
            ElectrumUseTls = true
        };
        var updated = new FlorestaSettings
        {
            ElectrumHost = "floresta",
            ElectrumPort = 50001,
            ElectrumUseTls = true
        };

        Assert.False(UIFlorestaController.HasElectrumEndpointChanged(existing, updated));
    }

    [Fact]
    public void ElectrumEndpointComparisonDetectsConnectionChanges()
    {
        var existing = new FlorestaSettings
        {
            ElectrumHost = "floresta",
            ElectrumPort = 50001,
            ElectrumUseTls = false
        };

        Assert.True(UIFlorestaController.HasElectrumEndpointChanged(
            existing,
            new FlorestaSettings { ElectrumHost = "other", ElectrumPort = 50001 }));
        Assert.True(UIFlorestaController.HasElectrumEndpointChanged(
            existing,
            new FlorestaSettings { ElectrumHost = "floresta", ElectrumPort = 50002 }));
        Assert.True(UIFlorestaController.HasElectrumEndpointChanged(
            existing,
            new FlorestaSettings { ElectrumHost = "floresta", ElectrumPort = 50001, ElectrumUseTls = true }));
    }

    private static void WithEnvironment(string variable, string value, Action action)
    {
        lock (EnvLock)
        {
            var previous = Environment.GetEnvironmentVariable(variable);
            try
            {
                Environment.SetEnvironmentVariable(variable, value);
                action();
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, previous);
            }
        }
    }
}
