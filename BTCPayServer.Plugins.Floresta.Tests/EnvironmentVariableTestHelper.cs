using System;

namespace BTCPayServer.Plugins.Floresta.Tests;

internal static class EnvironmentVariableTestHelper
{
    private static readonly object EnvLock = new();

    public static void WithBackendReplacementEnvironment(string? value, Action action)
    {
        lock (EnvLock)
        {
            var previous = Environment.GetEnvironmentVariable(FlorestaBackendMode.ReplaceBackendEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(FlorestaBackendMode.ReplaceBackendEnvironmentVariable, value);
                action();
            }
            finally
            {
                Environment.SetEnvironmentVariable(FlorestaBackendMode.ReplaceBackendEnvironmentVariable, previous);
            }
        }
    }
}
