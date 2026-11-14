using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Services;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Floresta;

public class FlorestaRpcClient(
    SettingsRepository settingsRepository,
    ILogger<FlorestaRpcClient> logger,
    HttpClient httpClient)
{
    private readonly bool _explicitSettings;
    private readonly FlorestaSettings _settings;
    private int _nextId;

    public FlorestaRpcClient(SettingsRepository settingsRepository, ILogger<FlorestaRpcClient> logger)
        : this(settingsRepository, logger, new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
    {
    }

    public FlorestaRpcClient(FlorestaSettings settings, ILogger<FlorestaRpcClient> logger)
        : this((SettingsRepository)null, logger, new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
    {
        _settings = settings;
        _explicitSettings = true;
    }

    public FlorestaRpcClient(FlorestaSettings settings, ILogger<FlorestaRpcClient> logger, HttpClient httpClient)
        : this((SettingsRepository)null, logger, httpClient)
    {
        _settings = settings;
        _explicitSettings = true;
    }

    public async Task PingAsync(CancellationToken ct)
    {
        await CallAsync<JsonElement>("ping", [], ct);
    }

    public Task<JsonElement> GetBlockchainInfoAsync(CancellationToken ct)
    {
        return CallAsync<JsonElement>("getblockchaininfo", [], ct);
    }

    public Task<bool> LoadDescriptorAsync(string descriptor, CancellationToken ct)
    {
        return CallAsync<bool>("loaddescriptor", [descriptor], ct);
    }

    public Task<JsonElement> RescanBlockchainAsync(
        int? startHeight,
        int? stopHeight,
        bool useTimestamp,
        string confidence,
        CancellationToken ct)
    {
        var parameters = new List<object>();
        var hasCustomConfidence = !string.IsNullOrEmpty(confidence) &&
                                  !string.Equals(confidence, "medium", StringComparison.OrdinalIgnoreCase);
        var includeStopDependentArgs = stopHeight is not null || useTimestamp || hasCustomConfidence;

        if (startHeight is not null || includeStopDependentArgs)
        {
            parameters.Add(startHeight ?? 0);
            if (includeStopDependentArgs)
            {
                parameters.Add(stopHeight ?? 0);
                if (useTimestamp || hasCustomConfidence)
                {
                    parameters.Add(useTimestamp);
                    parameters.Add(hasCustomConfidence ? confidence : "medium");
                }
            }
        }

        return CallAsync<JsonElement>("rescanblockchain", parameters.ToArray(), ct);
    }

    public Task<string[]> ListDescriptorsAsync(CancellationToken ct)
    {
        return CallAsync<string[]>("listdescriptors", [], ct);
    }

    public Task<JsonElement> AddNodeAsync(string node, string command, bool v2Transport, CancellationToken ct)
    {
        return CallAsync<JsonElement>("addnode", [node, command, v2Transport], ct);
    }

    public Task<string> SendRawTransactionAsync(string hex, CancellationToken ct)
    {
        return CallAsync<string>("sendrawtransaction", [hex], ct);
    }

    private async Task<T> CallAsync<T>(string method, object[] parameters, CancellationToken ct)
    {
        var settings = await GetSettingsAsync();
        if (string.IsNullOrWhiteSpace(settings.RpcUrl))
            throw new InvalidOperationException("Floresta RPC URL is not configured.");

        using var request = new HttpRequestMessage(HttpMethod.Post, settings.RpcUrl);
        if (!string.IsNullOrEmpty(settings.RpcUser) || !string.IsNullOrEmpty(settings.RpcPassword))
        {
            var raw = $"{settings.RpcUser}:{settings.RpcPassword}";
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
        }

        var payload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = Interlocked.Increment(ref _nextId),
            method,
            @params = parameters
        });
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException) when (!response.IsSuccessStatusCode)
        {
            throw new FlorestaRpcException($"Floresta RPC {method} failed with HTTP {(int)response.StatusCode}: {body}");
        }
        using var responseDocument = doc;
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
        {
            int? code = error.ValueKind == JsonValueKind.Object &&
                        error.TryGetProperty("code", out var codeElement) &&
                        codeElement.ValueKind == JsonValueKind.Number && codeElement.TryGetInt32(out var value)
                ? value : null;
            throw new FlorestaRpcException($"Floresta RPC {method} error: {error}", code);
        }
        if (!response.IsSuccessStatusCode)
            throw new FlorestaRpcException($"Floresta RPC {method} failed with HTTP {(int)response.StatusCode}: {body}");

        if (!root.TryGetProperty("result", out var result))
            return default;

        try
        {
            return result.Deserialize<T>();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to deserialize Floresta RPC {Method} response: {Result}", method, result.ToString());
            throw;
        }
    }

    private async Task<FlorestaSettings> GetSettingsAsync()
    {
        if (_explicitSettings)
            return _settings;

        return await settingsRepository.GetSettingAsync<FlorestaSettings>() ?? new FlorestaSettings();
    }
}

public class FlorestaRpcException(string message, int? code = null) : Exception(message)
{
    public int? Code { get; } = code;
}
