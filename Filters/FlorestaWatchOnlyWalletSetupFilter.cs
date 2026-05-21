using System;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Models.StoreViewModels;
using BTCPayServer.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace BTCPayServer.Plugins.Floresta.Filters;

public sealed class FlorestaWatchOnlyWalletSetupFilter : IAsyncActionFilter
{
    private const string WatchOnlyMessage =
        "Floresta is watch-only in this plugin. Connect an existing xpub or descriptor; Floresta does not create or store private keys.";

    private readonly SettingsRepository _settingsRepository;
    private readonly ITempDataDictionaryFactory _tempDataFactory;

    public FlorestaWatchOnlyWalletSetupFilter(
        SettingsRepository settingsRepository,
        ITempDataDictionaryFactory tempDataFactory)
    {
        _settingsRepository = settingsRepository;
        _tempDataFactory = tempDataFactory;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!TryGetBlockedWalletSetupRequest(context, out var storeId, out var cryptoCode) ||
            !await IsFlorestaBitcoinActive(cryptoCode))
        {
            await next();
            return;
        }

        var tempData = _tempDataFactory.GetTempData(context.HttpContext);
        tempData.SetStatusMessageModel(new StatusMessageModel
        {
            Severity = StatusMessageModel.StatusSeverity.Warning,
            Message = WatchOnlyMessage
        });

        context.Result = new RedirectToActionResult(
            "ImportWallet",
            "UIStores",
            new
            {
                storeId,
                cryptoCode,
                method = WalletSetupMethod.Xpub.ToString()
            });
    }

    private async Task<bool> IsFlorestaBitcoinActive(string cryptoCode)
    {
        if (!string.Equals(cryptoCode, "BTC", StringComparison.OrdinalIgnoreCase))
            return false;

        var settings = await _settingsRepository.GetSettingAsync<FlorestaSettings>() ?? new FlorestaSettings();
        return settings.Enabled &&
               settings.UseFlorestaAsBitcoinBackend &&
               string.Equals(settings.CryptoCode, "BTC", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetBlockedWalletSetupRequest(
        ActionExecutingContext context,
        out string storeId,
        out string cryptoCode)
    {
        storeId = null;
        cryptoCode = null;

        context.ActionDescriptor.RouteValues.TryGetValue("controller", out var controller);
        context.ActionDescriptor.RouteValues.TryGetValue("action", out var action);
        if (!string.Equals(controller, "UIStores", StringComparison.Ordinal))
            return false;

        if (string.Equals(action, "GenerateWallet", StringComparison.Ordinal))
        {
            storeId = GetStringArgument(context, "storeId") ?? GetWalletSetupViewModel(context)?.StoreId;
            cryptoCode = GetStringArgument(context, "cryptoCode") ?? GetWalletSetupViewModel(context)?.CryptoCode;
            return !string.IsNullOrEmpty(storeId) && !string.IsNullOrEmpty(cryptoCode);
        }

        var vm = GetWalletSetupViewModel(context);
        if (vm?.Method != WalletSetupMethod.Seed)
        {
            return false;
        }

        var blocksSeedImportPage =
            HttpMethods.IsGet(context.HttpContext.Request.Method) &&
            string.Equals(action, "ImportWallet", StringComparison.Ordinal);
        var blocksDirectSeedImportPost =
            HttpMethods.IsPost(context.HttpContext.Request.Method) &&
            string.Equals(action, "UpdateWallet", StringComparison.Ordinal);
        if (!blocksSeedImportPage && !blocksDirectSeedImportPost)
            return false;

        storeId = vm.StoreId;
        cryptoCode = vm.CryptoCode;
        return !string.IsNullOrEmpty(storeId) && !string.IsNullOrEmpty(cryptoCode);
    }

    private static WalletSetupViewModel GetWalletSetupViewModel(ActionExecutingContext context)
    {
        return context.ActionArguments.TryGetValue("vm", out var value)
            ? value as WalletSetupViewModel
            : null;
    }

    private static string GetStringArgument(ActionExecutingContext context, string argumentName)
    {
        return context.ActionArguments.TryGetValue(argumentName, out var value)
            ? value?.ToString()
            : null;
    }
}
