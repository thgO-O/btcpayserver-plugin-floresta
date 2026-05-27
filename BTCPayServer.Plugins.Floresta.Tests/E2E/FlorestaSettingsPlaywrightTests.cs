using System;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests.E2E;

[Collection(FlorestaE2ECollection.Name)]
public sealed class FlorestaSettingsPlaywrightTests : IAsyncLifetime
{
    private readonly BtcpayFlorestaWebAppFixture _server;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage? _page;

    public FlorestaSettingsPlaywrightTests(BtcpayFlorestaWebAppFixture server)
    {
        _server = server;
    }

    [Fact(Timeout = 180_000)]
    [Trait("Playwright", "Playwright")]
    public async Task ServerAdminCanConfigureAndTestFlorestaBackend()
    {
        var page = _page ?? throw new InvalidOperationException("Playwright page is not initialized.");

        await RegisterFirstAdmin(page);
        await page.GotoAsync(new Uri(_server.ServerUri, "/server/floresta").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.Commit });
        await AssertNoUiError(page);

        await Expect(page).ToHaveURLAsync(new Regex(".*/server/floresta$", RegexOptions.IgnoreCase));
        await Expect(page.GetByText("This mode uses Floresta as a lightweight Bitcoin backend")).ToBeVisibleAsync();
        await Expect(page.Locator("#FlorestaBackendActiveNotice")).ToBeVisibleAsync();
        await Expect(page.Locator("#FlorestaNav")).ToBeVisibleAsync();

        var electrumHost = Environment.GetEnvironmentVariable("FLORESTA_ELECTRUM_HOST") ?? "127.0.0.1";
        var electrumPort = Environment.GetEnvironmentVariable("FLORESTA_ELECTRUM_PORT") ?? "20001";
        var rpcUrl = Environment.GetEnvironmentVariable("FLORESTA_RPC_URL") ?? "http://127.0.0.1:18442";

        await page.Locator("#Enabled").SetCheckedAsync(true, new LocatorSetCheckedOptions { Force = true });
        await page.Locator("#CryptoCode").FillAsync("BTC");
        await page.Locator("#Network").SelectOptionAsync("regtest");
        await page.Locator("#ElectrumHost").FillAsync(electrumHost);
        await page.Locator("#ElectrumPort").FillAsync(electrumPort);
        await page.Locator("#RpcUrl").FillAsync(rpcUrl);
        await page.Locator("#GapLimit").FillAsync("123");
        await page.Locator("#FallbackFeeRateSatsPerByte").FillAsync("2.5");
        await page.Locator("#DefaultRescanStartHeight").FillAsync("0");
        await page.Locator("#UseFlorestaAsBitcoinBackend").SetCheckedAsync(true, new LocatorSetCheckedOptions { Force = true });

        await page.Locator("#SaveFlorestaSettings").ClickAsync();
        await page.GotoAsync(new Uri(_server.ServerUri, "/server/floresta").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.Commit });
        await Expect(page.Locator("#Enabled")).ToBeCheckedAsync();
        await Expect(page.Locator("#ElectrumHost")).ToHaveValueAsync(electrumHost);
        await Expect(page.Locator("#ElectrumPort")).ToHaveValueAsync(electrumPort);
        await Expect(page.Locator("#RpcUrl")).ToHaveValueAsync(rpcUrl);
        await Expect(page.Locator("#GapLimit")).ToHaveValueAsync("123");
        await Expect(page.Locator("#FallbackFeeRateSatsPerByte")).ToHaveValueAsync("2.5");
        await Expect(page.Locator("#FlorestaHealthPanel")).ToBeVisibleAsync();

        var testConnection = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Test Connection" });
        await Expect(testConnection).ToBeVisibleAsync();
        await testConnection.EvaluateAsync("button => button.click()");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = 30_000 });
        await Expect(page.GetByText(new Regex("Connection successful", RegexOptions.IgnoreCase))).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 20_000
        });
        await Expect(page.Locator("#FlorestaHealthRpcStatus")).ToContainTextAsync("Reachable");
        await Expect(page.Locator("#FlorestaHealthElectrumVersion")).ToBeVisibleAsync();

        var storeId = await CreateStore(page);
        await page.GotoAsync(new Uri(_server.ServerUri, $"/stores/{storeId}/onchain/BTC").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.Commit });
        await AssertNoUiError(page);
        await Expect(page.Locator("#FlorestaWatchOnlyWalletSetupNotice")).ToContainTextAsync("existing xpubs/descriptors");
        await Expect(page.Locator("#ImportWalletOptionsLink")).ToBeVisibleAsync();
        await Expect(page.Locator("#GenerateWalletLink")).ToBeHiddenAsync();

        await page.Locator("#ImportWalletOptionsLink").ClickAsync();
        await Expect(page.Locator("#ImportXpubLink")).ToBeVisibleAsync();
        await Expect(page.Locator("#ImportSeedLink")).ToBeHiddenAsync();

        await AssertBlockedWalletSetupGet(page, storeId, $"/stores/{storeId}/onchain/BTC/generate");
        await AssertBlockedWalletSetupGet(page, storeId, $"/stores/{storeId}/onchain/BTC/generate/HotWallet");
        await AssertBlockedWalletSetupGet(page, storeId, $"/stores/{storeId}/onchain/BTC/generate/WatchOnly");
        await AssertBlockedWalletSetupGet(page, storeId, $"/stores/{storeId}/onchain/BTC/import/Seed");

        await AssertBlockedWalletSetupPost(page, storeId, $"/stores/{storeId}/onchain/BTC/generate/HotWallet", "SavePrivateKeys=true&ScriptPubKeyType=Segwit");
        await AssertBlockedWalletSetupPost(page, storeId, $"/stores/{storeId}/onchain/BTC/generate/WatchOnly", "SavePrivateKeys=false&ScriptPubKeyType=Segwit");
        await AssertBlockedWalletSetupPost(page, storeId, $"/stores/{storeId}/onchain/BTC/generate/Seed", "SavePrivateKeys=true&ScriptPubKeyType=Segwit&ExistingMnemonic=abandon");
        await AssertBlockedWalletSetupPost(page, storeId, $"/stores/{storeId}/onchain/BTC/import/Seed", "DerivationScheme=xpub");
    }

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = Environment.GetEnvironmentVariable("PLAYWRIGHT_HEADLESS") != "false",
            Args = new[] { "--no-sandbox", "--disable-dev-shm-usage" }
        });
        var context = await _browser.NewContextAsync();
        _page = await context.NewPageAsync();
        _page.SetDefaultTimeout(15_000);
    }

    public async Task DisposeAsync()
    {
        if (_page is not null)
            await _page.CloseAsync();
        if (_browser is not null)
            await _browser.CloseAsync();
        _playwright?.Dispose();
    }

    private async Task RegisterFirstAdmin(IPage page)
    {
        await page.GotoAsync(new Uri(_server.ServerUri, "/register").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.Commit });
        await AssertNoUiError(page);

        var suffix = Guid.NewGuid().ToString("N")[..12];
        await page.Locator("#Email").FillAsync($"floresta-e2e-{suffix}@example.com");
        await page.Locator("#Password").FillAsync("123456");
        await page.Locator("#ConfirmPassword").FillAsync("123456");
        if (await page.Locator("#IsAdmin").CountAsync() > 0)
            await page.Locator("#IsAdmin").SetCheckedAsync(true, new LocatorSetCheckedOptions { Force = true });
        await page.Locator("#RegisterButton").ClickAsync();
        await Expect(page).ToHaveURLAsync(new Regex(".*/(dashboard|stores/create)$", RegexOptions.IgnoreCase));
    }

    private async Task<string> CreateStore(IPage page)
    {
        await page.GotoAsync(new Uri(_server.ServerUri, "/stores/create").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.Commit });
        await AssertNoUiError(page);
        await page.Locator("#Name").FillAsync("Floresta setup E2E " + Guid.NewGuid().ToString("N")[..8]);
        await page.Locator("#Create").ClickAsync();
        await Expect(page).ToHaveURLAsync(new Regex(".*/stores/[^/?#]+.*", RegexOptions.IgnoreCase));
        var match = Regex.Match(page.Url, @"/stores/([^/?#]+)");
        Assert.True(match.Success, $"Could not extract store id from URL {page.Url}");
        return match.Groups[1].Value;
    }

    private async Task AssertBlockedWalletSetupGet(IPage page, string storeId, string path)
    {
        await page.GotoAsync(new Uri(_server.ServerUri, path).ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.Commit });
        await AssertWatchOnlyRedirect(page, storeId);
    }

    private async Task AssertBlockedWalletSetupPost(IPage page, string storeId, string path, string body)
    {
        var token = await GetAntiForgeryToken(page, storeId);
        var response = await page.EvaluateAsync<string>(
            @"async ({ path, body, token }) => {
                const form = new URLSearchParams(body);
                form.set('__RequestVerificationToken', token);
                const response = await fetch(path, {
                    method: 'POST',
                    credentials: 'same-origin',
                    headers: { 'content-type': 'application/x-www-form-urlencoded' },
                    body: form.toString()
                });
                return JSON.stringify({
                    status: response.status,
                    url: response.url,
                    text: await response.text()
                });
            }",
            new { path, body, token });

        using var document = JsonDocument.Parse(response);
        var root = document.RootElement;
        Assert.Equal(200, root.GetProperty("status").GetInt32());
        Assert.Contains($"/stores/{storeId}/onchain/BTC/import/Xpub", root.GetProperty("url").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Floresta is watch-only in this plugin", root.GetProperty("text").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DerivationScheme", root.GetProperty("text").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> GetAntiForgeryToken(IPage page, string storeId)
    {
        await page.GotoAsync(new Uri(_server.ServerUri, $"/stores/{storeId}/onchain/BTC/import/Xpub").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.Commit });
        await AssertNoUiError(page);
        var token = await page.Locator("input[name='__RequestVerificationToken']").First.GetAttributeAsync("value");
        Assert.False(string.IsNullOrWhiteSpace(token), "Could not find antiforgery token on the xpub import page.");
        return token;
    }

    private static async Task AssertWatchOnlyRedirect(IPage page, string storeId)
    {
        await Expect(page).ToHaveURLAsync(new Regex($".*/stores/{Regex.Escape(storeId)}/onchain/BTC/import/Xpub.*", RegexOptions.IgnoreCase));
        await Expect(page.GetByText("Floresta is watch-only in this plugin")).ToBeVisibleAsync();
        await Expect(page.Locator("#DerivationScheme")).ToBeVisibleAsync();
    }

    private static async Task AssertNoUiError(IPage page)
    {
        var errorPageMarker = page.Locator("[data-testid='ui-error-page']");
        Assert.Equal(0, await errorPageMarker.CountAsync());

        var visibleDanger = page.Locator(".alert-danger:visible, .validation-summary-errors:visible");
        if (await visibleDanger.CountAsync() > 0)
        {
            var text = await visibleDanger.First.InnerTextAsync();
            Assert.Fail($"Unexpected UI error at {page.Url}: {text}");
        }
    }
}
