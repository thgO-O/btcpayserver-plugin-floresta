using System;
using System.Collections.Generic;
using System.Text.Json;
using NBitcoin;

namespace BTCPayServer.Plugins.Floresta.Services;

public sealed record FlorestaChainInfo(
    int? Height = null,
    string BestBlockHash = null,
    bool? IsInitialBlockDownload = null,
    int? ValidatedHeight = null,
    int? UtreexoRootCount = null,
    string Chain = null);

public sealed record FlorestaHealthSnapshot(
    string ElectrumStatus,
    string ElectrumVersion,
    bool? RpcReachable,
    string RpcNetwork,
    int? Height,
    string BestBlockHash,
    bool? IsInitialBlockDownload,
    int? ValidatedHeight,
    int? UtreexoRootCount,
    DateTimeOffset? LastUpdated,
    string LastError)
{
    public string ShortBestBlockHash => FlorestaChainInfoParser.ShortHash(BestBlockHash);
}

internal static class FlorestaChainInfoParser
{
    public static FlorestaChainInfo Parse(JsonElement blockchainInfo)
    {
        return new FlorestaChainInfo(
            GetInt32(blockchainInfo, "height") ?? GetInt32(blockchainInfo, "headers") ?? GetInt32(blockchainInfo, "blocks"),
            GetString(blockchainInfo, "best_block") ?? GetString(blockchainInfo, "bestblockhash"),
            GetBool(blockchainInfo, "ibd") ?? GetBool(blockchainInfo, "initialblockdownload"),
            GetInt32(blockchainInfo, "validated") ?? GetInt32(blockchainInfo, "blocks"),
            GetInt32(blockchainInfo, "root_count"),
            GetString(blockchainInfo, "chain"));
    }

    public static string Format(FlorestaChainInfo chainInfo)
    {
        var parts = new List<string>
        {
            $"height: {chainInfo.Height?.ToString() ?? "unknown"}"
        };

        if (!string.IsNullOrWhiteSpace(chainInfo.Chain))
            parts.Add($"chain: {chainInfo.Chain.Trim()}");
        if (!string.IsNullOrEmpty(chainInfo.BestBlockHash))
            parts.Add($"best: {ShortHash(chainInfo.BestBlockHash)}");
        if (chainInfo.IsInitialBlockDownload is not null)
            parts.Add($"ibd: {chainInfo.IsInitialBlockDownload.Value.ToString().ToLowerInvariant()}");
        if (chainInfo.ValidatedHeight is not null)
            parts.Add($"validated: {chainInfo.ValidatedHeight.Value}");
        if (chainInfo.UtreexoRootCount is not null)
            parts.Add($"roots: {chainInfo.UtreexoRootCount.Value}");

        return string.Join(", ", parts);
    }

    public static string GetNetworkValidationError(FlorestaChainInfo chainInfo, ChainName expectedChain)
    {
        var reportedChain = chainInfo?.Chain?.Trim();
        if (string.IsNullOrEmpty(reportedChain))
            return $"Floresta did not report its Bitcoin network; BTCPay expects {expectedChain}.";

        var actualChain = reportedChain.ToLowerInvariant() switch
        {
            "bitcoin" or "main" => ChainName.Mainnet,
            "testnet" or "test" => ChainName.Testnet,
            "regtest" => ChainName.Regtest,
            "signet" => NBitcoin.Bitcoin.Instance.Signet.ChainName,
            _ => null
        };

        if (actualChain is null)
        {
            return $"Floresta reported unsupported network '{reportedChain}'; " +
                   $"BTCPay expects {expectedChain}.";
        }

        return actualChain == expectedChain
            ? null
            : $"Floresta network mismatch: BTCPay expects {expectedChain}, " +
              $"but Floresta reports {reportedChain}.";
    }

    public static string GetElectrumNetworkValidationError(JsonElement serverFeatures, Network expectedNetwork)
    {
        var expectedGenesis = expectedNetwork.GetGenesis().GetHash().ToString();
        var reportedGenesis = serverFeatures.ValueKind == JsonValueKind.Object
            ? GetString(serverFeatures, "genesis_hash")?.Trim()
            : null;

        if (string.IsNullOrEmpty(reportedGenesis))
        {
            return $"Floresta Electrum did not report its genesis hash; " +
                   $"BTCPay expects {expectedNetwork.ChainName} ({expectedGenesis}).";
        }

        if (!IsHash(reportedGenesis))
        {
            return $"Floresta Electrum reported invalid genesis hash '{reportedGenesis}'; " +
                   $"BTCPay expects {expectedNetwork.ChainName} ({expectedGenesis}).";
        }

        return string.Equals(reportedGenesis, expectedGenesis, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"Floresta Electrum network mismatch: BTCPay expects {expectedNetwork.ChainName} " +
              $"genesis {expectedGenesis}, but Electrum reports {reportedGenesis}.";
    }

    public static string ShortHash(string hash)
    {
        if (string.IsNullOrEmpty(hash))
            return "unknown";
        return hash.Length <= 16 ? hash : $"{hash[..8]}...{hash[^8..]}";
    }

    private static int? GetInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
            return null;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value) ||
            property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out value))
            return value;

        return null;
    }

    private static bool? GetBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.True or JsonValueKind.False => property.GetBoolean(),
            JsonValueKind.String when bool.TryParse(property.GetString(), out var value) => value,
            _ => null
        };
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool IsHash(string value)
    {
        if (value.Length != 64)
            return false;

        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
                return false;
        }

        return true;
    }
}
