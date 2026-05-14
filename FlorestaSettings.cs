using System;

namespace BTCPayServer.Plugins.Floresta;

public class FlorestaSettings
{
    public FlorestaSettings()
    {
        CryptoCode = GetString("FLORESTA_CRYPTO_CODE", CryptoCode);
        Network = GetString("FLORESTA_NETWORK", Network);
        ElectrumHost = GetString("FLORESTA_ELECTRUM_HOST", ElectrumHost);
        ElectrumPort = GetInt("FLORESTA_ELECTRUM_PORT", ElectrumPort);
        ElectrumUseTls = GetBool("FLORESTA_ELECTRUM_TLS", ElectrumUseTls);
        RpcUrl = GetString("FLORESTA_RPC_URL", RpcUrl);
        RpcUser = GetString("FLORESTA_RPC_USER", RpcUser);
        RpcPassword = GetString("FLORESTA_RPC_PASSWORD", RpcPassword);
        GapLimit = GetInt("FLORESTA_GAP_LIMIT", GapLimit);
        DefaultRescanStartHeight = GetNullableInt("FLORESTA_FILTERS_START_HEIGHT", DefaultRescanStartHeight);
    }

    public bool Enabled { get; set; } = true;
    public string CryptoCode { get; set; } = "BTC";
    public string Network { get; set; } = "mainnet";
    public string ElectrumHost { get; set; } = "floresta";
    public int ElectrumPort { get; set; } = 50001;
    public bool ElectrumUseTls { get; set; }
    public string RpcUrl { get; set; } = "http://floresta:8332";
    public string RpcUser { get; set; }
    public string RpcPassword { get; set; }
    public int? DefaultRescanStartHeight { get; set; }
    public bool AutoRegisterDescriptors { get; set; } = true;
    public bool AutoRescanOnNewDescriptor { get; set; }
    public bool UseFlorestaAsBitcoinBackend { get; set; } = true;

    public const int MaxGapLimit = 1000;
    private int _gapLimit = 100;
    public int GapLimit
    {
        get => _gapLimit;
        set => _gapLimit = Math.Clamp(value, 1, MaxGapLimit);
    }

    public string Server
    {
        get => $"{ElectrumHost}:{ElectrumPort}";
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            var parts = value.Split(':', 2);
            ElectrumHost = parts[0];
            if (parts.Length == 2 && int.TryParse(parts[1], out var port))
                ElectrumPort = port;
        }
    }

    public bool UseTls
    {
        get => ElectrumUseTls;
        set => ElectrumUseTls = value;
    }

    private static string GetString(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static int GetInt(string name, int fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static int? GetNullableInt(string name, int? fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static bool GetBool(string name, bool fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return bool.TryParse(value, out var parsed) ? parsed : fallback;
    }
}
