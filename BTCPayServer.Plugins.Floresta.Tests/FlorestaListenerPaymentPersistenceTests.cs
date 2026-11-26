using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Floresta.Data;
using BTCPayServer.Services.Invoices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests;

public partial class FlorestaListenerInitializationTests
{
    [Theory]
    [Trait("Integration", "Integration")]
    [InlineData("repeat", "confirmed")]
    [InlineData("repeat", "removed")]
    [InlineData("block", "confirmed")]
    [InlineData("block", "removed")]
    [InlineData("block", "mempool")]
    [InlineData("polling", "confirmed")]
    [InlineData("polling", "mempool")]
    public async Task PaymentRefreshUsesInvoiceWalletWithConflictingTransactionCaches(
        string flow, string ownerState)
    {
        await using var invoices = await PostgresInvoiceDatabase.CreateAsync();
        await using var harness = new Harness(invoices: invoices);
        Assert.Equal(90, await harness.InitializeAsync());
        harness.UpdateSettings(s => { s.AutoRegisterDescriptors = false; s.AutoRescanOnNewDescriptor = true; });
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        var address = (await harness.Tracker.PrepareWalletTrackingAsync(wallet, harness.Token))[0];
        var invoiceId = await harness.CreateInvoiceAsync(address);
        var transaction = harness.SetRecoveredHistory(address, alreadySpent: flow != "polling");
        var txid = transaction.GetHash().ToString();

        // Insert the conflicting row first. Prove below that the global getter
        // selects it, so this case cannot pass by accidentally choosing the owner.
        await using (var cache = harness.Database.CreateContext())
        {
            cache.TrackedWallets.Add(new TrackedWallet { Id = "other-wallet", CryptoCode = "BTC" });
            cache.Transactions.Add(new TrackedTransaction
            {
                WalletId = "other-wallet", Txid = txid, RawTx = transaction.ToBytes(),
                BlockHeight = 80, SeenAt = DateTimeOffset.UtcNow,
                RemovedAt = ownerState == "confirmed" ? DateTimeOffset.UtcNow : null
            });
            await cache.SaveChangesAsync(harness.Token);
        }
        harness.UpdateSettings(s => s.AutoRegisterDescriptors = true);
        await harness.Tracker.TrackWalletAsync(wallet, harness.Token);
        Assert.Single(harness.Tracker.PendingTransactions);
        if (flow != "polling")
        {
            await harness.Listener.ProcessPendingTransactionsAsync(async (receipt, ct) =>
            {
                Assert.Equal(ReceiptDeliveryResult.Delivered, await harness.Listener.ProcessPendingTransactionAsync(receipt, ct));
                // A write can succeed before delivery is acknowledged. Retain the
                // same receipt so the next attempt reaches AddPayment's duplicate path.
                return flow != "repeat" ? ReceiptDeliveryResult.Delivered : ReceiptDeliveryResult.Retry;
            }, harness.Token);
            await AssertPaymentAsync(PaymentStatus.Settled);
        }
        else
        {
            await using var context = invoices.CreateContext();
            Assert.Empty(await context.Payments.ToListAsync(harness.Token));
        }

        if (ownerState == "removed")
            harness.HistoryResponse = _ => Array.Empty<object>();
        else if (ownerState == "mempool")
        {
            harness.HistoryResponse = script => script == address.Scripthash
                ? new object[] { new { tx_hash = txid, height = 0 } } : Array.Empty<object>();
            if (flow == "polling")
                harness.UnspentResponse = script => script == address.Scripthash
                    ? new object[] { new { tx_hash = txid, tx_pos = 0, value = 1234, height = 0 } }
                    : Array.Empty<object>();
        }
        Assert.Empty(await harness.Tracker.HandleNewBlockAsync(91, harness.Token));
        var current = await harness.Tracker.GetTransactionInfoAsync(wallet, txid, harness.Token);
        Assert.NotNull(current);
        var global = await harness.Tracker.GetTransactionResultAsync(txid, harness.Token);
        Assert.NotEqual(current.Confirmations, global.Confirmations);

        var expected = ownerState switch
        {
            "removed" => PaymentStatus.Unaccounted,
            "mempool" => PaymentStatus.Processing,
            _ => PaymentStatus.Settled
        };
        if (flow == "repeat")
        {
            await EnvironmentVariableTestHelper.WithBackendReplacementEnvironmentAsync("true", async () =>
            {
                await harness.Listener.ProcessPendingTransactionsAsync(
                    harness.Listener.ProcessPendingTransactionAsync, harness.Token);
                return true;
            });
            Assert.Equal(current.Confirmations, (long)harness.LastOnChainTransaction!.Confirmations);
        }
        else
        {
            // Exercise the ordinary block refresh, including the pinned host's
            // real PostgreSQL monitored-invoice query, without a retained receipt.
            var repository = new InvoiceRepository(invoices, new EventAggregator(new Logs()));
            Assert.Contains(await repository.GetMonitoredInvoices(PaymentTypes.CHAIN.GetPaymentMethodId("BTC")),
                invoice => invoice.Id == invoiceId);
            await EnvironmentVariableTestHelper.WithBackendReplacementEnvironmentAsync("true", async () =>
            {
                if (flow == "block")
                    await harness.Listener.PublishChainUpdate(harness.Token);
                else
                    await harness.Listener.RequestPaymentPollingAsync(harness.Token);
                return true;
            });
            if (flow == "polling")
            {
                await AssertPaymentAsync(expected);
                // Polling can win the race with retained delivery. The latter
                // must remain idempotent and must not publish ReceivedPayment again.
                await harness.Listener.ProcessPendingTransactionsAsync(
                    harness.Listener.ProcessPendingTransactionAsync, harness.Token);
            }
        }
        await AssertPaymentAsync(expected);
        Assert.Equal(1, harness.ReceivedPayments);
        Assert.Equal(flow == "repeat" ? 2 : 1, harness.OnChainEvents);
        Assert.Empty(harness.Tracker.PendingTransactions);
        Assert.Equal(1, harness.RescanRequests);

        async Task AssertPaymentAsync(PaymentStatus expected)
        {
            await using var context = invoices.CreateContext();
            var payment = Assert.Single(await context.Payments.AsNoTracking().ToListAsync(harness.Token));
            Assert.Equal(invoiceId, payment.InvoiceDataId);
            Assert.Equal(new OutPoint(transaction.GetHash(), 0).ToString(), payment.Id);
            Assert.Equal(0.00001234m, payment.Amount);
            Assert.Equal(expected, payment.Status);
        }
    }

    private sealed class PostgresInvoiceDatabase(string connection, string adminConnection, string database)
        : ApplicationDbContextFactory(Options.Create(new DatabaseOptions { ConnectionString = connection }),
            NullLoggerFactory.Instance), IAsyncDisposable
    {
        public SaveChangesInterceptor? SaveInterceptor { get; set; }

        public override ApplicationDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder>? configure = null)
        {
            return base.CreateContext(options =>
            {
                configure?.Invoke(options);
                if (SaveInterceptor is { } interceptor)
                    ((IRelationalDbContextOptionsBuilderInfrastructure)options).OptionsBuilder.AddInterceptors(interceptor);
            });
        }

        public static async Task<PostgresInvoiceDatabase> CreateAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("E2E_POSTGRES") ??
                "Host=127.0.0.1;Port=39372;Username=postgres;Password=postgres")
            {
                Database = "postgres", Pooling = false, Timeout = 5
            };
            var admin = options.ConnectionString;
            // Only the newly generated database is ever migrated or dropped.
            var name = "floresta_payments_" + Guid.NewGuid().ToString("N");
            await using (var client = new NpgsqlConnection(admin))
            {
                await client.OpenAsync(timeout.Token);
                await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", client);
                await command.ExecuteNonQueryAsync(timeout.Token);
            }
            options.Database = name;
            var factory = new PostgresInvoiceDatabase(options.ConnectionString, admin, name);
            try
            {
                await using var context = factory.CreateContext();
                // EnsureCreated alone omits the host's get_monitored_invoices
                // function. Use its real migrations for block-refresh fidelity.
                await context.Database.MigrateAsync(timeout.Token);
                return factory;
            }
            catch
            {
                await factory.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await using var client = new NpgsqlConnection(adminConnection);
            await client.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", client);
            await command.ExecuteNonQueryAsync();
        }
    }
}
