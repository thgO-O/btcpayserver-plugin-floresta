using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Controllers;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Floresta.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
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
    private readonly FlorestaStatusMonitor _statusMonitor;
    private readonly ILogger<UIFlorestaController> _logger;

    public UIFlorestaController(
        SettingsRepository settingsRepository,
        FlorestaElectrumClient electrumClient,
        FlorestaDescriptorService descriptorService,
        StoreRepository storeRepository,
        PaymentMethodHandlerDictionary handlers,
        FlorestaStatusMonitor statusMonitor,
        ILogger<UIFlorestaController> logger)
    {
        _settingsRepository = settingsRepository;
        _electrumClient = electrumClient;
        _descriptorService = descriptorService;
        _storeRepository = storeRepository;
        _handlers = handlers;
        _statusMonitor = statusMonitor;
        _logger = logger;
    }

    [HttpGet("~/server/floresta")]
    public async Task<IActionResult> Settings()
    {
        var settings = await _settingsRepository.GetSettingAsync<FlorestaSettings>() ?? new FlorestaSettings();
        SetHealthViewBag();
        return View(settings);
    }

    [HttpPost("~/server/floresta")]
    public async Task<IActionResult> Settings(FlorestaSettings settings, string command)
    {
        SetHealthViewBag();

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
                var chainInfo = FlorestaChainInfoParser.Parse(blockchainInfo);
                ViewBag.Health = new FlorestaHealthSnapshot(
                    "Reachable",
                    sw,
                    true,
                    chainInfo.Height,
                    chainInfo.BestBlockHash,
                    chainInfo.IsInitialBlockDownload,
                    chainInfo.ValidatedHeight,
                    chainInfo.UtreexoRootCount,
                    DateTimeOffset.UtcNow,
                    null);
                ViewBag.StatusMessage =
                    $"Connection successful. Electrum: {sw} protocol {pv}. RPC {FlorestaChainInfoParser.Format(chainInfo)}.";
            }
            catch (Exception ex)
            {
                ViewBag.StatusMessage = $"Connection failed: {ex.Message}";
            }
            return View(settings);
        }

        await _settingsRepository.UpdateSetting(settings);
        await RefreshStatusAfterSettingsUpdate();

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

    private void SetHealthViewBag()
    {
        ViewBag.Health = _statusMonitor.GetHealthSnapshot();
    }

    private async Task RefreshStatusAfterSettingsUpdate()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _statusMonitor.RefreshAsync(cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not refresh Floresta status after settings update");
        }
    }

    private sealed record DescriptorRegistrationResult(
        int StoresScanned,
        int StoresWithWallet,
        int AlreadyRegistered,
        int Registered);
}
