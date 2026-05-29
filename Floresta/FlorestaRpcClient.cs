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

public class FlorestaRpcClient
{
    private readonly SettingsRepository _settingsRepository;
    private readonly ILogger<FlorestaRpcClient> _logger;
    private readonly HttpClient _httpClient;
    private readonly bool _explicitSettings;
    private FlorestaSettings _settings;
    private int _nextId;

    public FlorestaRpcClient(SettingsRepository settingsRepository, ILogger<FlorestaRpcClient> logger)
        : this(settingsRepository, logger, new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
    {
    }

    public FlorestaRpcClient(
        SettingsRepository settingsRepository,
        ILogger<FlorestaRpcClient> logger,
        HttpClient httpClient)
    {
        _settingsRepository = settingsRepository;
        _logger = logger;
        _httpClient = httpClient;
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
        await CallAsync<JsonElement>("ping", Array.Empty<object>(), ct);
    }

    public Task<JsonElement> GetBlockchainInfoAsync(CancellationToken ct)
    {
        return CallAsync<JsonElement>("getblockchaininfo", Array.Empty<object>(), ct);
    }

    public Task<bool> LoadDescriptorAsync(string descriptor, CancellationToken ct)
    {
        return CallAsync<bool>("loaddescriptor", new object[] { descriptor }, ct);
    }

    public Task<JsonElement> RescanBlockchainAsync(
        int? startHeight,
        int? stopHeight,
        bool useTimestamp,
        string confidence,
        CancellationToken ct)
    {
        var parameters = new List<object>();
        if (startHeight is not null || stopHeight is not null || useTimestamp || !string.IsNullOrEmpty(confidence))
        {
            parameters.Add(startHeight ?? 0);
            if (stopHeight is not null || useTimestamp || !string.IsNullOrEmpty(confidence))
            {
                if (stopHeight is not null)
                {
                    parameters.Add(stopHeight.Value);
                    if (useTimestamp || !string.IsNullOrEmpty(confidence))
                    {
                        parameters.Add(useTimestamp);
                        parameters.Add(string.IsNullOrEmpty(confidence) ? "medium" : confidence);
                    }
                }
            }
        }

        return CallAsync<JsonElement>("rescanblockchain", parameters.ToArray(), ct);
    }

    public Task<string[]> ListDescriptorsAsync(CancellationToken ct)
    {
        return CallAsync<string[]>("listdescriptors", Array.Empty<object>(), ct);
    }

    public Task<JsonElement> AddNodeAsync(string node, string command, bool v2Transport, CancellationToken ct)
    {
        return CallAsync<JsonElement>("addnode", new object[] { node, command, v2Transport }, ct);
    }

    public Task<string> SendRawTransactionAsync(string hex, CancellationToken ct)
    {
        return CallAsync<string>("sendrawtransaction", new object[] { hex }, ct);
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

        using var response = await _httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new FlorestaRpcException($"Floresta RPC {method} failed with HTTP {(int)response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            throw new FlorestaRpcException($"Floresta RPC {method} error: {error}");

        if (!root.TryGetProperty("result", out var result))
            return default;

        try
        {
            return result.Deserialize<T>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize Floresta RPC {Method} response: {Result}", method, result.ToString());
            throw;
        }
    }

    private async Task<FlorestaSettings> GetSettingsAsync()
    {
        if (_explicitSettings)
            return _settings;

        return await _settingsRepository.GetSettingAsync<FlorestaSettings>() ?? new FlorestaSettings();
    }
}

public class FlorestaRpcException : Exception
{
    public FlorestaRpcException(string message) : base(message)
    {
    }
}
