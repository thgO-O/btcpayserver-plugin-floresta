using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.E2ETests;

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
        await page.Locator("#DefaultRescanStartHeight").FillAsync("0");
        await page.Locator("#UseFlorestaAsBitcoinBackend").SetCheckedAsync(true, new LocatorSetCheckedOptions { Force = true });

        await page.Locator("#SaveFlorestaSettings").ClickAsync();
        await page.GotoAsync(new Uri(_server.ServerUri, "/server/floresta").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.Commit });
        await Expect(page.Locator("#Enabled")).ToBeCheckedAsync();
        await Expect(page.Locator("#ElectrumHost")).ToHaveValueAsync(electrumHost);
        await Expect(page.Locator("#ElectrumPort")).ToHaveValueAsync(electrumPort);
        await Expect(page.Locator("#RpcUrl")).ToHaveValueAsync(rpcUrl);
        await Expect(page.Locator("#GapLimit")).ToHaveValueAsync("123");

        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Test Connection" }).ClickAsync();
        await Expect(page.GetByText(new Regex("Connection successful", RegexOptions.IgnoreCase))).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 20_000
        });
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
