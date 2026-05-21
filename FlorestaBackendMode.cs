using System;

namespace BTCPayServer.Plugins.Floresta;

public static class FlorestaBackendMode
{
    public const string ReplaceBackendEnvironmentVariable = "FLORESTA_REPLACE_BTCPAY_BACKEND";

    public static bool IsBackendReplacementEnabled()
    {
        var value = Environment.GetEnvironmentVariable(ReplaceBackendEnvironmentVariable);
        return value?.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            _ => false
        };
    }
}
