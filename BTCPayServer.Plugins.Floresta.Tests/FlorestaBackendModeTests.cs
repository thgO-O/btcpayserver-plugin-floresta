using System;
using static BTCPayServer.Plugins.Floresta.Tests.EnvironmentVariableTestHelper;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests;

public class FlorestaBackendModeTests
{
    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("on")]
    [InlineData(" true ")]
    public void EnablesBackendReplacementFromEnvironment(string? value)
    {
        WithEnvironment(value, () =>
        {
            Assert.True(FlorestaBackendMode.IsBackendReplacementEnabled());
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("no")]
    [InlineData("off")]
    [InlineData("invalid")]
    public void KeepsBackendReplacementDisabledByDefault(string? value)
    {
        WithEnvironment(value, () =>
        {
            Assert.False(FlorestaBackendMode.IsBackendReplacementEnabled());
        });
    }

    private static void WithEnvironment(string? value, Action action) =>
        WithBackendReplacementEnvironment(value, action);
}
