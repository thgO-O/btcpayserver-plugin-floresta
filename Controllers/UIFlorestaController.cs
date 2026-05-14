using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Controllers;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace BTCPayServer.Plugins.Floresta.Controllers;

[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyServerSettings)]
public class UIFlorestaController : Controller
{
    private readonly SettingsRepository _settingsRepository;
    private readonly FlorestaElectrumClient _electrumClient;
    private readonly FlorestaDescriptorService _descriptorService;
    private readonly StoreRepository _storeRepository;
    private readonly PaymentMethodHandlerDictionary _handlers;

    public UIFlorestaController(
        SettingsRepository settingsRepository,
        FlorestaElectrumClient electrumClient,
        FlorestaDescriptorService descriptorService,
        StoreRepository storeRepository,
        PaymentMethodHandlerDictionary handlers)
    {
        _settingsRepository = settingsRepository;
        _electrumClient = electrumClient;
        _descriptorService = descriptorService;
        _storeRepository = storeRepository;
        _handlers = handlers;
    }

    [HttpGet("~/server/floresta")]
    public async Task<IActionResult> Settings()
    {
        var settings = await _settingsRepository.GetSettingAsync<FlorestaSettings>() ?? new FlorestaSettings();
        return View(settings);
    }

    [HttpPost("~/server/floresta")]
    public async Task<IActionResult> Settings(FlorestaSettings settings, string command)
    {
        if (command == "test")
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var testClient = new FlorestaElectrumClient(
                    settings,
                    NullLogger<FlorestaElectrumClient>.Instance);
                await testClient.ConnectAsync(cts.Token);
                var (sw, pv) = await testClient.ServerVersionAsync("BTCPayServer-Floresta", "1.4", cts.Token);
                await testClient.DisposeAsync();

                var rpcClient = new FlorestaRpcClient(settings, NullLogger<FlorestaRpcClient>.Instance);
                var blockchainInfo = await rpcClient.GetBlockchainInfoAsync(cts.Token);
                ViewBag.StatusMessage =
                    $"Connection successful. Electrum: {sw} protocol {pv}. RPC {FormatBlockchainInfo(blockchainInfo)}.";
            }
            catch (Exception ex)
            {
                ViewBag.StatusMessage = $"Connection failed: {ex.Message}";
            }
            return View(settings);
        }

        await _settingsRepository.UpdateSetting(settings);

        if (command == "register")
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var result = await RegisterDescriptorsForStores(settings, cts.Token);
                ViewBag.StatusMessage =
                    $"Descriptor registration completed. Stores scanned: {result.StoresScanned}. " +
                    $"BTC wallets found: {result.StoresWithWallet}. " +
                    $"Already registered: {result.AlreadyRegistered}. " +
                    $"New descriptors registered: {result.Registered}.";
                return View(settings);
            }
            catch (Exception ex)
            {
                ViewBag.StatusMessage = $"Descriptor registration failed: {ex.Message}";
                return View(settings);
            }
        }

        if (command == "rescan")
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var rpcClient = new FlorestaRpcClient(settings, NullLogger<FlorestaRpcClient>.Instance);
                await rpcClient.RescanBlockchainAsync(settings.DefaultRescanStartHeight, null, false, "medium", cts.Token);
                ViewBag.StatusMessage =
                    $"Rescan requested from height {settings.DefaultRescanStartHeight?.ToString() ?? "default"}. " +
                    "Floresta runs the rescan asynchronously; use Test Connection to follow RPC sync status.";
                return View(settings);
            }
            catch (Exception ex)
            {
                ViewBag.StatusMessage = $"Rescan request failed: {ex.Message}";
                return View(settings);
            }
        }

        TempData[WellKnownTempData.SuccessMessage] = "Floresta settings updated.";
        return RedirectToAction(nameof(Settings));
    }

    private async Task<DescriptorRegistrationResult> RegisterDescriptorsForStores(FlorestaSettings settings, CancellationToken ct)
    {
        var rpcClient = new FlorestaRpcClient(settings, NullLogger<FlorestaRpcClient>.Instance);
        var loaded = (await rpcClient.ListDescriptorsAsync(ct) ?? Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);
        var storesScanned = 0;
        var storesWithWallet = 0;
        var alreadyRegistered = 0;
        var registered = 0;

        foreach (var store in await _storeRepository.GetStores())
        {
            storesScanned++;
            var scheme = store.GetDerivationSchemeSettings(_handlers, settings.CryptoCode, true)?.AccountDerivation;
            if (scheme is null)
                continue;

            storesWithWallet++;
            var descriptors = _descriptorService.CreateDescriptors(settings.CryptoCode, scheme.ToString());
            foreach (var descriptor in new[] { descriptors.ReceiveDescriptor, descriptors.ChangeDescriptor })
            {
                if (loaded.Contains(descriptor))
                {
                    alreadyRegistered++;
                    continue;
                }
                await rpcClient.LoadDescriptorAsync(descriptor, ct);
                loaded.Add(descriptor);
                registered++;
            }
        }

        return new DescriptorRegistrationResult(storesScanned, storesWithWallet, alreadyRegistered, registered);
    }

    private static string FormatBlockchainInfo(JsonElement blockchainInfo)
    {
        var height = GetInt32(blockchainInfo, "blocks") ?? GetInt32(blockchainInfo, "height");
        var bestBlock = GetString(blockchainInfo, "bestblockhash") ?? GetString(blockchainInfo, "best_block");
        var ibd = GetBool(blockchainInfo, "initialblockdownload") ?? GetBool(blockchainInfo, "ibd");
        var validated = GetInt32(blockchainInfo, "validated");
        var rootCount = GetInt32(blockchainInfo, "root_count");

        var parts = new List<string>
        {
            $"height: {height?.ToString() ?? "unknown"}"
        };

        if (!string.IsNullOrEmpty(bestBlock))
            parts.Add($"best: {ShortHash(bestBlock)}");
        if (ibd is not null)
            parts.Add($"ibd: {ibd.Value.ToString().ToLowerInvariant()}");
        if (validated is not null)
            parts.Add($"validated: {validated.Value}");
        if (rootCount is not null)
            parts.Add($"roots: {rootCount.Value}");

        return string.Join(", ", parts);
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

    private static string ShortHash(string hash)
    {
        return hash.Length <= 16 ? hash : $"{hash[..8]}...{hash[^8..]}";
    }

    private sealed record DescriptorRegistrationResult(
        int StoresScanned,
        int StoresWithWallet,
        int AlreadyRegistered,
        int Registered);
}
