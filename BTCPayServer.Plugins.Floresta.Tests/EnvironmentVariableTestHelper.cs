using System;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Floresta.Tests;

internal static class EnvironmentVariableTestHelper
{
    private static readonly SemaphoreSlim EnvLock = new(1, 1);

    public static void WithBackendReplacementEnvironment(string? value, Action action)
    {
        EnvLock.Wait();
        var previous = Environment.GetEnvironmentVariable(FlorestaBackendMode.ReplaceBackendEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(FlorestaBackendMode.ReplaceBackendEnvironmentVariable, value);
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(FlorestaBackendMode.ReplaceBackendEnvironmentVariable, previous);
            EnvLock.Release();
        }
    }

    public static async Task<T> WithBackendReplacementEnvironmentAsync<T>(string? value, Func<Task<T>> action)
    {
        await EnvLock.WaitAsync();
        var previous = Environment.GetEnvironmentVariable(FlorestaBackendMode.ReplaceBackendEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(FlorestaBackendMode.ReplaceBackendEnvironmentVariable, value);
            return await action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(FlorestaBackendMode.ReplaceBackendEnvironmentVariable, previous);
            EnvLock.Release();
        }
    }
}
