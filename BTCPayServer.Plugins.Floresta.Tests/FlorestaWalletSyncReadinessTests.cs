using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.Floresta.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBXplorer.DerivationStrategy;
using NBitcoin;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests;

public class FlorestaWalletSyncReadinessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialBlockDownloadDoesNotAccessOrChangeTheWalletCache(bool blockNotification)
    {
        using var http = new HttpClient(new RpcHandler(_ => Task.FromResult(InfoResponse(true))));
        var (sync, database) = CreateSync(http);

        if (blockNotification)
            Assert.Empty(await sync.HandleNewBlockAsync(0, CancellationToken.None));
        else
        {
            var result = await sync.SyncWalletStateForScanAsync("wallet", Addresses(), CancellationToken.None, (_, _) => { });
            Assert.Empty(result.NewTransactions);
            Assert.False(result.Summary.IsComplete);
            Assert.Equal(0, result.Summary.Attempted);
        }

        Assert.Equal(0, database.Accesses);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadyNodeResumesTheNormalDatabasePath(bool blockNotification)
    {
        using var http = new HttpClient(new RpcHandler(_ => Task.FromResult(InfoResponse(false))));
        var (sync, database) = CreateSync(http);

        await Assert.ThrowsAsync<DatabaseReachedException>(() => InvokeAsync(sync, blockNotification, CancellationToken.None));

        Assert.Equal(1, database.Accesses);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RpcFailureDoesNotChangeTheWalletCache(bool blockNotification)
    {
        using var http = new HttpClient(new RpcHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("node unavailable")
        })));
        var (sync, database) = CreateSync(http);

        await Assert.ThrowsAsync<FlorestaRpcException>(() => InvokeAsync(sync, blockNotification, CancellationToken.None));

        Assert.Equal(0, database.Accesses);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringTheReadinessCheckDoesNotChangeTheWalletCache(bool blockNotification)
    {
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new RpcHandler(ct =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(ct);
        }));
        var (sync, database) = CreateSync(http);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvokeAsync(sync, blockNotification, cancellation.Token));

        Assert.Equal(0, database.Accesses);
    }

    [Fact]
    public async Task ConnectionChangeDuringReadinessCheckRejectsTheOldSnapshot()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var electrum = new FlorestaElectrumClient(new FlorestaSettings
            {
                ElectrumHost = "127.0.0.1",
                ElectrumPort = ((IPEndPoint)listener.LocalEndpoint).Port,
                ElectrumUseTls = false
            }, NullLogger<FlorestaElectrumClient>.Instance);
            using var http = new HttpClient(new RpcHandler(async ct =>
            {
                await electrum.ConnectAsync(ct);
                return InfoResponse(false);
            }));
            var (sync, database) = CreateSync(http, electrum);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                InvokeAsync(sync, false, timeout.Token));

            Assert.Contains("connection changed", error.Message);
            Assert.Equal(0, database.Accesses);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static HttpResponseMessage InfoResponse(bool ibd) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            result = new { chain = "regtest", blocks = 0, initialblockdownload = ibd }
        }))
    };

    private static List<TrackedAddress> Addresses() => new()
    {
        new TrackedAddress { WalletId = "wallet", Scripthash = "known" }
    };

    private static async Task InvokeAsync(FlorestaWalletSync sync, bool blockNotification, CancellationToken ct)
    {
        if (blockNotification)
            await sync.HandleNewBlockAsync(0, ct);
        else
            await sync.SyncWalletStateForScanAsync("wallet", Addresses(), ct, (_, _) => { });
    }

    private static (FlorestaWalletSync Sync, ObservedDatabaseFactory Database) CreateSync(
        HttpClient http, FlorestaElectrumClient? electrum = null)
    {
        var database = new ObservedDatabaseFactory();
        var rpc = new FlorestaRpcClient(new FlorestaSettings { RpcUrl = "http://floresta.test" },
            NullLogger<FlorestaRpcClient>.Instance, http);
        electrum ??= new FlorestaElectrumClient(new FlorestaSettings(), NullLogger<FlorestaElectrumClient>.Instance);
        return (new FlorestaWalletSync(database, electrum, rpc, null!, null!,
            new ConcurrentDictionary<string, DerivationStrategyBase>(), () => 100,
            Network.RegTest, NullLogger.Instance), database);
    }

    private sealed class DatabaseReachedException : Exception;

    private sealed class ObservedDatabaseFactory() : FlorestaDbContextFactory(Options.Create(new DatabaseOptions()))
    {
        public int Accesses { get; private set; }

        public override FlorestaDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptionsAction = null)
        {
            Accesses++;
            throw new DatabaseReachedException();
        }
    }

    private sealed class RpcHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("getblockchaininfo", json.RootElement.GetProperty("method").GetString());
            return await respond(ct);
        }
    }
}
