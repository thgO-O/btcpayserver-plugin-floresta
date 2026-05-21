using System;
using System.Security.Cryptography;
using System.Text;

namespace BTCPayServer.Plugins.Floresta.Services;

internal static class LogSafeId
{
    public static string Hash(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "empty";

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }
}
