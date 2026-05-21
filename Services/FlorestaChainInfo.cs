using System;
using System.Collections.Generic;
using System.Text.Json;

namespace BTCPayServer.Plugins.Floresta.Services;

public sealed record FlorestaChainInfo(
    int? Height = null,
    string BestBlockHash = null,
    bool? IsInitialBlockDownload = null,
    int? ValidatedHeight = null,
    int? UtreexoRootCount = null);

public sealed record FlorestaHealthSnapshot(
    string ElectrumStatus,
    string ElectrumVersion,
    bool? RpcReachable,
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
            GetInt32(blockchainInfo, "height") ?? GetInt32(blockchainInfo, "blocks"),
            GetString(blockchainInfo, "best_block") ?? GetString(blockchainInfo, "bestblockhash"),
            GetBool(blockchainInfo, "ibd") ?? GetBool(blockchainInfo, "initialblockdownload"),
            GetInt32(blockchainInfo, "validated"),
            GetInt32(blockchainInfo, "root_count"));
    }

    public static string Format(FlorestaChainInfo chainInfo)
    {
        var parts = new List<string>
        {
            $"height: {chainInfo.Height?.ToString() ?? "unknown"}"
        };

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

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value))
            return value;

        if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out value))
            return value;

        return null;
    }

    private static bool? GetBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
            return null;

        if (property.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return property.GetBoolean();

        if (property.ValueKind == JsonValueKind.String && bool.TryParse(property.GetString(), out var value))
            return value;

        return null;
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }
}
