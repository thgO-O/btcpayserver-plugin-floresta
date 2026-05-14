using System;
using System.Linq;
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
                var blocks = blockchainInfo.TryGetProperty("blocks", out var b) ? b.GetInt32().ToString() : "unknown";
                ViewBag.StatusMessage = $"Connection successful. Electrum: {sw} protocol {pv}. RPC blocks: {blocks}.";
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
                var count = await RegisterDescriptorsForStores(settings, cts.Token);
                ViewBag.StatusMessage = $"Descriptor registration completed. New descriptors registered: {count}.";
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
                ViewBag.StatusMessage = $"Rescan requested from height {settings.DefaultRescanStartHeight?.ToString() ?? "default"}.";
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

    private async Task<int> RegisterDescriptorsForStores(FlorestaSettings settings, CancellationToken ct)
    {
        var rpcClient = new FlorestaRpcClient(settings, NullLogger<FlorestaRpcClient>.Instance);
        var loaded = (await rpcClient.ListDescriptorsAsync(ct) ?? Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);
        var registered = 0;

        foreach (var store in await _storeRepository.GetStores())
        {
            var scheme = store.GetDerivationSchemeSettings(_handlers, settings.CryptoCode, true)?.AccountDerivation;
            if (scheme is null)
                continue;

            var descriptors = _descriptorService.CreateDescriptors(settings.CryptoCode, scheme.ToString());
            foreach (var descriptor in new[] { descriptors.ReceiveDescriptor, descriptors.ChangeDescriptor })
            {
                if (loaded.Contains(descriptor))
                    continue;
                await rpcClient.LoadDescriptorAsync(descriptor, ct);
                loaded.Add(descriptor);
                registered++;
            }
        }

        return registered;
    }
}
