using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;
using NBitcoin;
using NBitcoin.RPC;
using static Microsoft.Playwright.Assertions;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests.E2E;

[Collection(FlorestaE2ECollection.Name)]
public sealed class InvoicePaymentPlaywrightTests : IAsyncLifetime
{
    private const string WalletXpubBip84 =
        "vpub5ZrpbMUWLCJ6MbpU1RzocWBddAQnk2XYry9JSXrtzxSqoicei28CzqUhiN2HJ8z2VjY6rsUNf4qxjym43ydhAFQJ7BDDcC2bK6et6x9hc4D";

    private readonly BtcpayFlorestaWebAppFixture _server;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage? _page;

    public InvoicePaymentPlaywrightTests(BtcpayFlorestaWebAppFixture server)
    {
        _server = server;
    }

    [Fact(Timeout = 360_000)]
    [Trait("Playwright", "Playwright")]
    public async Task BTCPayInvoiceCanBePaidThroughFlorestaAndCreditsWalletBalance()
    {
        var page = _page ?? throw new InvalidOperationException("Playwright page is not initialized.");

        await RegisterAdmin(page);
        await ConfigureFloresta(page);

        var bitcoin = CreateBitcoinRpcClient();
        var bitcoinWallet = await PrepareBitcoinWalletAsync(bitcoin);
        await ConnectRegtestMeshAsync(bitcoin);
        await FundBitcoinMinerAsync(bitcoinWallet);
        await SubmitBitcoinChainToUtreexodAsync(bitcoin);
        var chainHeight = await bitcoin.GetBlockCountAsync();
        var bestBlockHash = await GetBitcoinBestBlockHashAsync(bitcoin);
        await WaitForUtreexodChainAsync(chainHeight, bestBlockHash);
        await WaitForFlorestaChainAsync(chainHeight, bestBlockHash);
        await WaitForBtcpayFlorestaBackendAsync(page, chainHeight);

        var storeId = await CreateStore(page);
        await ConfigureStoreToSettleUnconfirmed(page, storeId);
        await AddWatchOnlyWallet(page, storeId);
        await WaitForDescriptorsAsync();

        var invoiceId = await CreateInvoice(page, storeId, "0.00010000", "BTC");
        await page.GotoAsync(new Uri(_server.ServerUri, $"/i/{invoiceId}").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.Commit });
        await AssertNoUiError(page);

        var invoiceAddress = await page.Locator("#Address_BTC-CHAIN .truncate-center").GetAttributeAsync("data-text");
        Assert.False(string.IsNullOrWhiteSpace(invoiceAddress));

        var txId = await bitcoinWallet.SendToAddressAsync(BitcoinAddress.Create(invoiceAddress!, Network.RegTest), Money.Coins(0.0001m));
        var rawTx = await bitcoin.GetRawTransactionAsync(txId);
        await FlorestaRpcAsync("sendrawtransaction", new object[] { rawTx.ToHex() });
        await bitcoinWallet.GenerateToAddressAsync(1, await bitcoinWallet.GetNewAddressAsync());
        await SubmitBitcoinChainToUtreexodAsync(bitcoin);
        await WaitForUtreexodChainAsync(await bitcoin.GetBlockCountAsync(), await GetBitcoinBestBlockHashAsync(bitcoin));
        await WaitForFlorestaChainAsync(await bitcoin.GetBlockCountAsync(), await GetBitcoinBestBlockHashAsync(bitcoin));

        await EventuallyAsync(async () =>
        {
            await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.Commit });
            await AssertNoUiError(page);
            var settled = page.Locator("#settled");
            await settled.WaitForAsync(new LocatorWaitForOptions { Timeout = 5_000 });
            await Expect(settled).ToContainTextAsync("Invoice Paid", new LocatorAssertionsToContainTextOptions { Timeout = 5_000 });
        }, TimeSpan.FromSeconds(120), "invoice to become settled");

        var walletUrl = new Uri(_server.ServerUri, $"/wallets/S-{storeId}-BTC?loadTransactions=true").ToString();
        await page.GotoAsync(walletUrl, new PageGotoOptions { WaitUntil = WaitUntilState.Commit });
        await AssertNoUiError(page);

        await EventuallyAsync(async () =>
        {
            var response = await page.GotoAsync(walletUrl, new PageGotoOptions { WaitUntil = WaitUntilState.Commit });
            Assert.True(response?.Ok, $"Wallet page returned HTTP {response?.Status} at {page.Url}");
            await AssertNoUiError(page);

            var row = page.Locator($"tr.transaction-row[data-value='{txId}']");
            await Expect(row).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 5_000 });
            await Expect(row).ToContainTextAsync("0.00010000", new LocatorAssertionsToContainTextOptions { Timeout = 5_000 });
        }, TimeSpan.FromSeconds(120), "wallet balance transaction to appear");
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

    private static RPCClient CreateBitcoinRpcClient()
    {
        var connectionString = Environment.GetEnvironmentVariable("E2E_BITCOIND_RPC") ??
                               "server=http://127.0.0.1:43782;ceiwHEbqWI83:DwubwWsoo3";
        return new RPCClient(RPCCredentialString.Parse(connectionString), Network.RegTest);
    }

    private static async Task<RPCClient> PrepareBitcoinWalletAsync(RPCClient rpc)
    {
        return await GetOrCreateWalletAsync(rpc);
    }

    private static async Task<string[]> FundBitcoinMinerAsync(RPCClient wallet)
    {
        var address = await wallet.GetNewAddressAsync();
        var blocks = await wallet.GenerateToAddressAsync(101, address);
        return blocks.Select(blockHash => blockHash.ToString()).ToArray();
    }

    private static async Task<RPCClient> GetOrCreateWalletAsync(RPCClient rpc)
    {
        try
        {
            return await rpc.CreateWalletAsync("floresta-e2e", new CreateWalletOptions
            {
                LoadOnStartup = true,
                Descriptors = true
            });
        }
        catch (RPCException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
        }

        try
        {
            return await rpc.LoadWalletAsync("floresta-e2e", true);
        }
        catch (RPCException ex) when (ex.Message.Contains("already loaded", StringComparison.OrdinalIgnoreCase))
        {
            return rpc.SetWalletContext("floresta-e2e");
        }
    }

    private static async Task ConnectRegtestMeshAsync(RPCClient bitcoin)
    {
        var bitcoindEndpoint = await ResolveP2PEndpointAsync("E2E_BITCOIND_P2P_HOST", "E2E_BITCOIND_P2P_PORT", "127.0.0.1", 39388);
        var utreexodEndpoint = await ResolveP2PEndpointAsync("E2E_UTREEXOD_P2P_HOST", "E2E_UTREEXOD_P2P_PORT", "127.0.0.1", 38333);

        await AddFlorestaPeerAsync(utreexodEndpoint);
        await AddBitcoinPeerAsync(bitcoin, utreexodEndpoint);
        await AddFlorestaPeerAsync(bitcoindEndpoint);
        await WaitForRegtestMeshAsync(bitcoin, bitcoindEndpoint, utreexodEndpoint);
    }

    private static async Task<string> ResolveP2PEndpointAsync(string hostVariable, string portVariable, string defaultHost, int defaultPort)
    {
        var host = Environment.GetEnvironmentVariable(hostVariable) ?? defaultHost;
        var port = int.TryParse(Environment.GetEnvironmentVariable(portVariable), out var parsedPort)
            ? parsedPort
            : defaultPort;
        var ip = (await Dns.GetHostAddressesAsync(host))
            .First(a => a.AddressFamily == AddressFamily.InterNetwork)
            .ToString();

        return $"{ip}:{port}";
    }

    private static async Task AddFlorestaPeerAsync(string endpoint)
    {
        await FlorestaRpcAsync("addnode", new object[] { endpoint, "add", false });
    }

    private static async Task AddBitcoinPeerAsync(RPCClient bitcoin, string endpoint)
    {
        try
        {
            await bitcoin.SendCommandAsync("addnode", endpoint, "add");
        }
        catch (RPCException ex) when (ex.Message.Contains("already added", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    private static async Task WaitForRegtestMeshAsync(RPCClient bitcoin, string bitcoindEndpoint, string utreexodEndpoint)
    {
        await EventuallyAsync(async () =>
        {
            var florestaPeers = await FlorestaRpcAsync("getpeerinfo", Array.Empty<object>());
            Assert.True(florestaPeers.GetArrayLength() >= 2, $"Expected Floresta to have at least 2 peers, got {florestaPeers.GetArrayLength()}");
            Assert.True(florestaPeers.EnumerateArray().Any(peer => PeerMatches(peer, "address", bitcoindEndpoint) || PeerMatches(peer, "user_agent", "Satoshi")),
                "Expected Floresta to be connected to bitcoind");
            Assert.True(florestaPeers.EnumerateArray().Any(peer => PeerMatches(peer, "address", utreexodEndpoint) || PeerMatches(peer, "user_agent", "utreexo")),
                "Expected Floresta to be connected to utreexod");

            var bitcoindPeers = await BitcoinRpcJsonAsync(bitcoin, "getpeerinfo");
            Assert.True(bitcoindPeers.EnumerateArray().Any(peer => PeerMatches(peer, "addr", utreexodEndpoint) || PeerMatches(peer, "subver", "utreexo")),
                "Expected bitcoind to be connected to utreexod");
        }, TimeSpan.FromSeconds(90), "regtest P2P mesh");
    }

    private static bool PeerMatches(JsonElement peer, string propertyName, string expected)
    {
        return peer.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String &&
               property.GetString()?.Contains(expected, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static async Task<string> GetBitcoinBestBlockHashAsync(RPCClient bitcoin)
    {
        var response = await bitcoin.SendCommandAsync("getbestblockhash");
        if (string.IsNullOrWhiteSpace(response.ResultString))
            throw new InvalidOperationException("bitcoind getbestblockhash returned an empty result.");
        return response.ResultString;
    }

    private static async Task SubmitBlocksToUtreexodAsync(RPCClient bitcoin, string[] blockHashes)
    {
        foreach (var blockHash in blockHashes)
        {
            var response = await bitcoin.SendCommandAsync("getblock", blockHash, 0);
            if (string.IsNullOrWhiteSpace(response.ResultString))
                throw new InvalidOperationException($"bitcoind getblock {blockHash} returned an empty raw block.");

            var submitResult = await UtreexodRpcAsync("submitblock", new object[] { response.ResultString });
            if (submitResult.ValueKind == JsonValueKind.String)
            {
                var message = submitResult.GetString() ?? string.Empty;
                if (!message.Contains("already have block", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"utreexod rejected block {blockHash}: {message}");
            }
        }
    }

    private static async Task SubmitBitcoinChainToUtreexodAsync(RPCClient bitcoin)
    {
        var bitcoinHeight = await bitcoin.GetBlockCountAsync();
        var utreexodInfo = await UtreexodRpcAsync("getblockchaininfo", Array.Empty<object>());
        var utreexodHeight = utreexodInfo.GetProperty("blocks").GetInt32();
        var startHeight = Math.Max(1, utreexodHeight + 1);

        for (var height = startHeight; height <= bitcoinHeight; height++)
        {
            var response = await bitcoin.SendCommandAsync("getblockhash", height);
            if (string.IsNullOrWhiteSpace(response.ResultString))
                throw new InvalidOperationException($"bitcoind getblockhash {height} returned an empty result.");

            await SubmitBlocksToUtreexodAsync(bitcoin, new[] { response.ResultString });
        }
    }

    private static async Task<JsonElement> BitcoinRpcJsonAsync(RPCClient bitcoin, string method, params object[] parameters)
    {
        var response = await bitcoin.SendCommandAsync(method, parameters);
        using var doc = JsonDocument.Parse(response.Result.ToString());
        return doc.RootElement.Clone();
    }

    private static async Task WaitForFlorestaChainAsync(int expectedHeight, string expectedBestBlock)
    {
        await EventuallyAsync(async () =>
        {
            var info = await FlorestaRpcAsync("getblockchaininfo", Array.Empty<object>());
            var height = info.GetProperty("height").GetInt32();
            var bestBlock = info.GetProperty("best_block").GetString();
            var rootCount = info.GetProperty("root_count").GetInt32();
            var validated = info.GetProperty("validated").GetInt32();
            var ibd = info.GetProperty("ibd").GetBoolean();
            Assert.True(height >= expectedHeight, $"Floresta height {height} is below bitcoind height {expectedHeight}");
            Assert.Equal(expectedBestBlock, bestBlock);
            Assert.False(ibd, "Expected Floresta to leave IBD before continuing");
            Assert.True(rootCount > 0 || validated > 0, $"Expected Floresta to expose usable Utreexo state, got root_count={rootCount}, validated={validated}");
        }, TimeSpan.FromSeconds(180), "floresta to sync to bitcoind");
    }

    private static async Task WaitForUtreexodChainAsync(int expectedHeight, string expectedBestBlock)
    {
        await EventuallyAsync(async () =>
        {
            var info = await UtreexodRpcAsync("getblockchaininfo", Array.Empty<object>());
            var height = info.GetProperty("blocks").GetInt32();
            var bestBlock = info.GetProperty("bestblockhash").GetString();
            Assert.True(height >= expectedHeight, $"utreexod height {height} is below bitcoind height {expectedHeight}");
            Assert.Equal(expectedBestBlock, bestBlock);
        }, TimeSpan.FromSeconds(60), "utreexod to accept bitcoind blocks");
    }

    private static async Task WaitForDescriptorsAsync()
    {
        await EventuallyAsync(async () =>
        {
            var result = await FlorestaRpcAsync("listdescriptors", Array.Empty<object>());
            Assert.True(result.GetArrayLength() >= 2, "Expected receive/change descriptors to be registered in Floresta");
        }, TimeSpan.FromSeconds(60), "Floresta descriptor registration");
    }

    private async Task WaitForBtcpayFlorestaBackendAsync(IPage page, int expectedHeight)
    {
        await EventuallyAsync(async () =>
        {
            await page.GotoAsync(new Uri(_server.ServerUri, "/server/floresta").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.Commit });
            await AssertNoUiError(page);
            await Expect(page.Locator("#FlorestaHealthElectrumStatus")).ToContainTextAsync("Ready", new LocatorAssertionsToContainTextOptions { Timeout = 2_000 });
            await Expect(page.Locator("#FlorestaHealthRpcStatus")).ToContainTextAsync("Reachable", new LocatorAssertionsToContainTextOptions { Timeout = 2_000 });
            await Expect(page.Locator("#FlorestaHealthIbd")).ToContainTextAsync("No", new LocatorAssertionsToContainTextOptions { Timeout = 2_000 });

            var heightText = (await page.Locator("#FlorestaHealthHeight").InnerTextAsync()).Trim();
            Assert.True(
                int.TryParse(heightText, out var height) && height >= expectedHeight,
                $"BTCPay Floresta status height {heightText} is below expected height {expectedHeight}");
        }, TimeSpan.FromSeconds(90), "BTCPay Floresta backend status");
    }

    private static async Task<JsonElement> FlorestaRpcAsync(string method, object[] parameters)
    {
        var rpcUrl = Environment.GetEnvironmentVariable("FLORESTA_RPC_URL") ?? "http://127.0.0.1:18442";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Post, rpcUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                method,
                @params = parameters
            }), Encoding.UTF8, "application/json")
        };
        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        if (!response.IsSuccessStatusCode ||
            doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            throw new InvalidOperationException($"Floresta RPC {method} failed: HTTP {(int)response.StatusCode} {body}");

        return doc.RootElement.GetProperty("result").Clone();
    }

    private static async Task<JsonElement> UtreexodRpcAsync(string method, object[] parameters)
    {
        var rpcUrl = Environment.GetEnvironmentVariable("E2E_UTREEXOD_RPC_URL") ?? "https://127.0.0.1:38332";
        var rpcUser = Environment.GetEnvironmentVariable("E2E_UTREEXOD_RPC_USER") ?? "test";
        var rpcPassword = Environment.GetEnvironmentVariable("E2E_UTREEXOD_RPC_PASSWORD") ?? "test";
        using var handler = new HttpClientHandler();
        if (new Uri(rpcUrl).Scheme == Uri.UriSchemeHttps)
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Post, rpcUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "1.0",
                id = 1,
                method,
                @params = parameters
            }), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{rpcUser}:{rpcPassword}")));

        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        if (!response.IsSuccessStatusCode ||
            doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            throw new InvalidOperationException($"utreexod RPC {method} failed: HTTP {(int)response.StatusCode} {body}");

        return doc.RootElement.GetProperty("result").Clone();
    }

    private async Task RegisterAdmin(IPage page)
    {
        await page.GotoAsync(new Uri(_server.ServerUri, "/register").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.Commit });
        await AssertNoUiError(page);

        var suffix = Guid.NewGuid().ToString("N")[..12];
        await page.Locator("#Email").FillAsync($"floresta-payment-{suffix}@example.com");
        await page.Locator("#Password").FillAsync("123456");
        await page.Locator("#ConfirmPassword").FillAsync("123456");
        if (await page.Locator("#IsAdmin").CountAsync() > 0)
            await page.Locator("#IsAdmin").SetCheckedAsync(true, new LocatorSetCheckedOptions { Force = true });
        await page.Locator("#RegisterButton").ClickAsync();
        await Expect(page).ToHaveURLAsync(new Regex(".*/(dashboard|stores/create)$", RegexOptions.IgnoreCase));
    }

    private async Task ConfigureFloresta(IPage page)
    {
        var electrumHost = Environment.GetEnvironmentVariable("FLORESTA_ELECTRUM_HOST") ?? "127.0.0.1";
        var electrumPort = Environment.GetEnvironmentVariable("FLORESTA_ELECTRUM_PORT") ?? "20001";
        var rpcUrl = Environment.GetEnvironmentVariable("FLORESTA_RPC_URL") ?? "http://127.0.0.1:18442";

        await page.GotoAsync(new Uri(_server.ServerUri, "/server/floresta").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.Commit });
        await AssertNoUiError(page);
        await page.Locator("#Enabled").SetCheckedAsync(true, new LocatorSetCheckedOptions { Force = true });
        await page.Locator("#CryptoCode").FillAsync("BTC");
        await page.Locator("#Network").SelectOptionAsync("regtest");
        await page.Locator("#ElectrumHost").FillAsync(electrumHost);
        await page.Locator("#ElectrumPort").FillAsync(electrumPort);
        await page.Locator("#RpcUrl").FillAsync(rpcUrl);
        await page.Locator("#GapLimit").FillAsync("100");
        await page.Locator("#DefaultRescanStartHeight").FillAsync("0");
        await page.Locator("#UseFlorestaAsBitcoinBackend").SetCheckedAsync(true, new LocatorSetCheckedOptions { Force = true });
        await page.Locator("#SaveFlorestaSettings").ClickAsync();
    }

    private async Task<string> CreateStore(IPage page)
    {
        await page.GotoAsync(new Uri(_server.ServerUri, "/stores/create").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.Commit });
        await AssertNoUiError(page);
        await page.Locator("#Name").FillAsync("Floresta payment E2E " + Guid.NewGuid().ToString("N")[..8]);
        await page.Locator("#Create").ClickAsync();
        await Expect(page).ToHaveURLAsync(new Regex(".*/stores/[^/?#]+.*", RegexOptions.IgnoreCase));
        var match = Regex.Match(page.Url, @"/stores/([^/?#]+)");
        Assert.True(match.Success, $"Could not extract store id from URL {page.Url}");
        return match.Groups[1].Value;
    }

    private async Task ConfigureStoreToSettleUnconfirmed(IPage page, string storeId)
    {
        await page.GotoAsync(new Uri(_server.ServerUri, $"/stores/{storeId}/settings").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.Commit });
        await AssertNoUiError(page);
        await page.Locator("#SpeedPolicy").SelectOptionAsync("0");
        await page.Locator("#page-primary").ClickAsync();
        await Expect(page.Locator(".alert-success")).ToContainTextAsync("Store successfully updated", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
    }

    private async Task AddWatchOnlyWallet(IPage page, string storeId)
    {
        await page.GotoAsync(new Uri(_server.ServerUri, $"/stores/{storeId}/onchain/BTC").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.Commit });
        await AssertNoUiError(page);
        await page.Locator("#ImportWalletOptionsLink").ClickAsync();
        await page.Locator("#ImportXpubLink").ClickAsync();
        await page.Locator("#DerivationScheme").FillAsync(WalletXpubBip84);
        await page.Locator("#Continue").ClickAsync();
        await page.Locator("#Confirm").ClickAsync();
        await Expect(page.Locator(".alert-success")).ToContainTextAsync("Wallet settings for BTC have been updated", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
    }

    private async Task<string> CreateInvoice(IPage page, string storeId, string amount, string currency)
    {
        await page.GotoAsync(new Uri(_server.ServerUri, $"/stores/{storeId}/invoices/create").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.Commit });
        await AssertNoUiError(page);
        await page.Locator("#Amount").FillAsync(amount);
        await page.Locator("#Currency").ClearAsync();
        await page.Locator("#Currency").FillAsync(currency);
        await page.Locator("#page-primary").ClickAsync();

        await AssertNoUiError(page);
        var status = page.Locator(".alert-success, .alert-danger").First;
        await status.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        var statusText = await status.InnerTextAsync();
        Assert.DoesNotContain("alert-danger", await status.GetAttributeAsync("class") ?? string.Empty);

        var match = Regex.Match(statusText, @"Invoice (\w+) just created!", RegexOptions.IgnoreCase);
        Assert.True(match.Success, $"Could not extract invoice id from status message at {page.Url}: {statusText}");
        return match.Groups[1].Value;
    }

    private static async Task EventuallyAsync(Func<Task> action, TimeSpan timeout, string description)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                await action();
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                await Task.Delay(1_000);
            }
        }

        throw new TimeoutException($"Timed out waiting for {description}. Last error: {lastError?.Message}", lastError);
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
