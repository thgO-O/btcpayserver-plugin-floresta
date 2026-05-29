using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NBXplorer.Models;

namespace BTCPayServer.Plugins.Floresta;

internal sealed class FlorestaTransactionBroadcaster
{
    private readonly FlorestaRpcClient _rpcClient;
    private readonly FlorestaElectrumClient _electrumClient;

    public FlorestaTransactionBroadcaster(
        FlorestaRpcClient rpcClient,
        FlorestaElectrumClient electrumClient)
    {
        _rpcClient = rpcClient;
        _electrumClient = electrumClient;
    }

    public Task<BroadcastResult> BroadcastAsync(string body, CancellationToken ct)
    {
        return BroadcastAsync(Encoding.UTF8.GetBytes(body), ct);
    }

    public async Task<BroadcastResult> BroadcastAsync(byte[] body, CancellationToken ct)
    {
        if (!TryExtractRawTransactionHex(body, out var rawTx, out var parseError))
        {
            return new BroadcastResult(false)
            {
                RPCMessage = parseError
            };
        }

        try
        {
            await _rpcClient.SendRawTransactionAsync(rawTx, ct);
            return new BroadcastResult(true);
        }
        catch (Exception rpcEx) when (rpcEx is FlorestaRpcException or InvalidOperationException or HttpRequestException)
        {
            try
            {
                await _electrumClient.TransactionBroadcastAsync(rawTx, ct);
                return new BroadcastResult(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new BroadcastResult(false)
                {
                    RPCMessage = $"RPC broadcast failed: {rpcEx.Message}; Electrum broadcast failed: {ex.Message}"
                };
            }
        }
        catch (FlorestaElectrumException ex)
        {
            return new BroadcastResult(false)
            {
                RPCMessage = ex.Message
            };
        }
    }

    public static bool TryExtractRawTransactionHex(string body, out string rawTx, out string error)
    {
        return TryExtractRawTransactionHex(Encoding.UTF8.GetBytes(body), out rawTx, out error);
    }

    public static bool TryExtractRawTransactionHex(byte[] body, out string rawTx, out string error)
    {
        rawTx = string.Empty;
        error = string.Empty;

        if (body.Length == 0)
        {
            error = "Broadcast request body is empty.";
            return false;
        }

        var bodyText = Encoding.UTF8.GetString(body);
        var trimmed = bodyText.Trim();
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (TryExtractRawTransactionHex(doc.RootElement, out rawTx))
                return true;
        }
        catch (JsonException)
        {
            if (TryNormalizeRawTransactionHex(trimmed.Trim('"'), out rawTx))
                return true;
        }

        if (!LooksLikeText(body))
        {
            rawTx = Convert.ToHexString(body).ToLowerInvariant();
            return true;
        }

        error = "Broadcast request did not contain raw transaction hex.";
        return false;
    }

    private static bool TryExtractRawTransactionHex(JsonElement element, out string rawTx)
    {
        rawTx = string.Empty;

        if (element.ValueKind == JsonValueKind.String)
            return TryNormalizeRawTransactionHex(element.GetString(), out rawTx);

        if (element.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var propertyName in new[] { "transaction", "hex", "rawTransaction", "rawTx" })
        {
            if (TryGetPropertyIgnoreCase(element, propertyName, out var property) &&
                TryExtractRawTransactionHex(property, out rawTx))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement property)
    {
        foreach (var candidate in element.EnumerateObject())
        {
            if (string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                property = candidate.Value;
                return true;
            }
        }

        property = default;
        return false;
    }

    private static bool LooksLikeText(byte[] bytes)
    {
        foreach (var b in bytes)
        {
            if (b is 9 or 10 or 13)
                continue;
            if (b < 32)
                return false;
        }

        return true;
    }

    private static bool TryNormalizeRawTransactionHex(string value, out string rawTx)
    {
        rawTx = (value ?? string.Empty).Trim();
        if (rawTx.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            rawTx = rawTx[2..];

        return rawTx.Length > 0 &&
               rawTx.Length % 2 == 0 &&
               rawTx.All(Uri.IsHexDigit);
    }
}
