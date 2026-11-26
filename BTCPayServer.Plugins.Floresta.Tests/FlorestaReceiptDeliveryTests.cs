using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Floresta.Data;
using BTCPayServer.Plugins.Floresta.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NBitcoin;
using NBXplorer.Models;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests;

public partial class FlorestaListenerInitializationTests
{
    [Theory]
    [InlineData("import")]
    [InlineData("block")]
    [InlineData("notification")]
    [InlineData("scan")]
    public async Task EveryWalletSyncTriggerRetainsSpentReceiptsForDeliveryRetry(string trigger)
    {
        await VerifyCommittedReceiptDeliveryAsync(new InvoiceDatabase(), trigger);
    }

    [Theory]
    [Trait("Integration", "Integration")]
    [InlineData("block")]
    [InlineData("scan")]
    public async Task CommittedReceiptDeliveryUsesPostgres(string trigger)
    {
        await using var invoices = await PostgresInvoiceDatabase.CreateAsync();
        await VerifyCommittedReceiptDeliveryAsync(invoices, trigger);
    }

    private static async Task VerifyCommittedReceiptDeliveryAsync(ApplicationDbContextFactory invoices, string trigger)
    {
        await using var harness = new Harness(invoices: invoices);
        Assert.Equal(90, await harness.InitializeAsync());
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        var addresses = await harness.Tracker.PrepareWalletTrackingAsync(wallet, harness.Token);
        var invoiceId = await harness.CreateInvoiceAsync(addresses[0]);
        var transaction = harness.SetRecoveredHistory(addresses[0], true);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(harness.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        switch (trigger)
        {
            case "import":
                await harness.Tracker.TrackWalletAsync(wallet, deadline.Token);
                break;
            case "block":
                await harness.Tracker.HandleNewBlockAsync(90, deadline.Token);
                break;
            case "notification":
                await harness.Tracker.HandleScripthashNotificationAsync(addresses[0].Scripthash, "changed", deadline.Token);
                break;
            case "scan":
                Assert.True(harness.Tracker.StartScan(wallet, 2, 0));
                while (harness.Tracker.GetScanState(wallet)?.Status != ScanUTXOStatus.Complete)
                    await Task.Delay(10, deadline.Token);
                break;
        }
        Assert.Equal(transaction.GetHash().ToString(), Assert.Single(harness.Tracker.PendingTransactions).TxId);
        await using (var cache = harness.Database.CreateContext())
            Assert.Empty(await cache.Utxos.Where(u => !u.IsSpent).ToListAsync(deadline.Token));
        await harness.Listener.ProcessPendingTransactionsAsync((_, _) =>
            Task.FromException<ReceiptDeliveryResult>(new IOException("Invoice service temporarily unavailable")), deadline.Token);
        Assert.Single(harness.Tracker.PendingTransactions);
        Assert.Equal(0, harness.ReceivedPayments);
        await harness.RunUntilPaymentAsync();
        await using var ctx = invoices.CreateContext();
        var payment = Assert.Single(await ctx.Payments.ToListAsync(harness.Token));
        Assert.Equal(invoiceId, payment.InvoiceDataId);
        Assert.Equal(new OutPoint(transaction.GetHash(), 0).ToString(), payment.Id);
        Assert.Equal(1, harness.ReceivedPayments);
        Assert.Equal(1, harness.OnChainEvents);
        Assert.Empty(harness.Tracker.PendingTransactions);
        Assert.Equal(0, harness.RescanRequests);
    }


    [Theory]
    [InlineData("block")]
    [InlineData("notification")]
    [InlineData("reconnect")]
    public async Task ListenerEventsRetrySpentReceiptsAfterInvoiceFailure(string trigger)
    {
        var invoices = new InvoiceDatabase();
        await using var harness = new Harness(invoices: invoices);
        Assert.Equal(90, await harness.InitializeAsync());
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        var address = (await harness.Tracker.PrepareWalletTrackingAsync(wallet, harness.Token))[0];
        var invoiceId = await harness.CreateInvoiceAsync(address);
        var transaction = harness.SetRecoveredHistory(address, true);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        invoices.BeforeCreateContext = () =>
        {
            invoices.BeforeCreateContext = null;
            failed.TrySetResult();
            throw new IOException("Invoice database temporarily unavailable");
        };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(harness.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await EnvironmentVariableTestHelper.WithBackendReplacementEnvironmentAsync("true", async () =>
        {
            // Enter the actual listener handlers; protocol decoding is tested separately.
            var name = trigger switch
            {
                "block" => "OnNewBlock",
                "notification" => "OnScripthashNotification",
                _ => "ProcessReconnectAsync"
            };
            object[] args = trigger switch
            {
                "block" => [new FlorestaElectrumHeaderNotification { Height = 90 }],
                "notification" => [address.Scripthash, "changed"],
                _ => [deadline.Token]
            };
            var result = typeof(FlorestaListener).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(harness.Listener, args);
            if (result is Task task)
                await task.WaitAsync(deadline.Token);
            await failed.Task.WaitAsync(deadline.Token);
            return true;
        });
        // Wait behind the event's delivery pass before inspecting its retry.
        await harness.Listener.ProcessPendingTransactionsAsync((_, _) =>
            Task.FromResult(ReceiptDeliveryResult.Retry), deadline.Token);
        Assert.Equal(transaction.GetHash().ToString(), Assert.Single(harness.Tracker.PendingTransactions).TxId);
        // Polling can also hit the injected context failure, but only the
        // event's receipt delivery publishes the transaction before the lookup.
        Assert.Equal(1, harness.OnChainEvents);
        Assert.Equal(transaction.GetHash(), harness.LastOnChainTransaction!.TransactionHash);
        Assert.Equal(0, harness.ReceivedPayments);
        await harness.RunUntilPaymentAsync();
        await using var ctx = invoices.CreateContext();
        Assert.Equal(invoiceId, Assert.Single(await ctx.Payments.ToListAsync(harness.Token)).InvoiceDataId);
        Assert.Equal(1, harness.ReceivedPayments);
        Assert.Empty(harness.Tracker.PendingTransactions);
    }

    [Fact]
    public async Task WipedReceiptIsDiscardedAndRediscoveryCanDeliverAgain()
    {
        var invoices = new InvoiceDatabase();
        await using var harness = new Harness(invoices: invoices);
        Assert.Equal(90, await harness.InitializeAsync());
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        var address = (await harness.Tracker.PrepareWalletTrackingAsync(wallet, harness.Token))[0];
        var invoiceId = await harness.CreateInvoiceAsync(address);
        harness.SetRecoveredHistory(address, true);
        await harness.Tracker.TrackWalletAsync(wallet, harness.Token);
        Assert.Single(harness.Tracker.PendingTransactions);
        await harness.Tracker.WipeAsync(wallet, harness.Token);
        await harness.Listener.ProcessPendingTransactionsAsync(harness.Listener.ProcessPendingTransactionAsync, harness.Token);
        Assert.Empty(harness.Tracker.PendingTransactions);
        Assert.Equal(0, harness.ReceivedPayments);
        await harness.Tracker.TrackWalletAsync(wallet, harness.Token);
        Assert.Single(harness.Tracker.PendingTransactions);
        await harness.RunUntilPaymentAsync();
        await using var ctx = invoices.CreateContext();
        Assert.Equal(invoiceId, Assert.Single(await ctx.Payments.ToListAsync(harness.Token)).InvoiceDataId);
        Assert.Equal(1, harness.ReceivedPayments);
    }

    [Fact]
    public async Task AddressSourceReceiptKeepsItsOnChainEventSemantics()
    {
        await using var harness = new Harness(invoices: new InvoiceDatabase());
        Assert.Equal(90, await harness.InitializeAsync());
        var address = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        var source = new TrackedAddress
        {
            WalletId = address.ToString(), Address = address.ToString(), ScriptPubKey = address.ScriptPubKey.ToBytes(),
            Scripthash = ScriptHashUtility.ComputeScriptHash(address.ScriptPubKey)
        };
        var transaction = harness.SetRecoveredHistory(source, true);
        await harness.Tracker.TrackAddressAsync(address.ToString(), harness.Token);
        var receipt = Assert.Single(harness.Tracker.PendingTransactions);
        Assert.Equal(transaction.GetHash().ToString(), receipt.TxId);
        Assert.Null(receipt.DerivationStrategy);
        await harness.Listener.ProcessPendingTransactionsAsync(harness.Listener.ProcessPendingTransactionAsync, harness.Token);
        Assert.Empty(harness.Tracker.PendingTransactions);
        Assert.Equal(1, harness.OnChainEvents);
        Assert.Equal(0, harness.ReceivedPayments);
    }

    [Fact]
    public async Task PaymentSaveFailureReturningNullDoesNotAcknowledgeTheReceipt()
    {
        var invoices = new InvoiceDatabase();
        await using var harness = new Harness(invoices: invoices);
        Assert.Equal(90, await harness.InitializeAsync());
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        var address = (await harness.Tracker.PrepareWalletTrackingAsync(wallet, harness.Token))[0];
        var invoiceId = await harness.CreateInvoiceAsync(address);
        harness.SetRecoveredHistory(address, true);
        await harness.Tracker.TrackWalletAsync(wallet, harness.Token);
        invoices.FailNextSave = true;
        await harness.Listener.ProcessPendingTransactionsAsync(harness.Listener.ProcessPendingTransactionAsync, harness.Token);
        Assert.False(invoices.FailNextSave);
        Assert.Single(harness.Tracker.PendingTransactions);
        await using (var ctx = invoices.CreateContext())
            Assert.Empty(await ctx.Payments.ToListAsync(harness.Token));
        await harness.RunUntilPaymentAsync();
        await using var context = invoices.CreateContext();
        Assert.Equal(invoiceId, Assert.Single(await context.Payments.ToListAsync(harness.Token)).InvoiceDataId);
        Assert.Equal(1, harness.ReceivedPayments);
    }

    [Theory]
    [Trait("Integration", "Integration")]
    [InlineData("write-rejected")]
    [InlineData("write-exception")]
    [InlineData("read-exception")]
    public async Task FirstOutputFailureStillDeliversLaterOutputs(string fault)
    {
        await using var invoices = await PostgresInvoiceDatabase.CreateAsync();
        await using var harness = new Harness(invoices: invoices);
        Assert.Equal(90, await harness.InitializeAsync());
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        var addresses = await harness.Tracker.PrepareWalletTrackingAsync(wallet, harness.Token);
        var firstInvoice = await harness.CreateInvoiceAsync(addresses[0]);
        var secondInvoice = await harness.CreateInvoiceAsync(addresses[1]);
        var transaction = Network.RegTest.CreateTransaction();
        transaction.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        transaction.Outputs.Add(Money.Satoshis(1234), new Script(addresses[0].ScriptPubKey));
        transaction.Outputs.Add(Money.Satoshis(2345), new Script(addresses[1].ScriptPubKey));
        var txid = transaction.GetHash();
        harness.RawTransactions.Add(txid.ToString(), transaction.ToHex());
        harness.HistoryResponse = script => script == addresses[0].Scripthash || script == addresses[1].Scripthash
            ? new object[] { new { tx_hash = txid.ToString(), height = 80 } }
            : Array.Empty<object>();
        await harness.Tracker.TrackWalletAsync(wallet, harness.Token);
        await using (var cache = harness.Database.CreateContext())
            Assert.False(await cache.Utxos.AnyAsync(u => !u.IsSpent, harness.Token));

        await using var faultContext = invoices.CreateContext();
        var firstInvoiceRow = faultContext.Invoices.Where(i => i.Id == firstInvoice);
        string? originalBlob = null;
        if (fault == "read-exception")
        {
            originalBlob = await firstInvoiceRow.Select(i => i.Blob2).SingleAsync(harness.Token);
            Assert.NotNull(originalBlob);
            // PostgreSQL accepts the JSON array, but the invoice lookup expects an object.
            Assert.Equal(1, await firstInvoiceRow.ExecuteUpdateAsync(
                b => b.SetProperty(i => i.Blob2, "[]"), harness.Token));
        }
        else if (fault == "write-exception")
            invoices.SaveInterceptor = new ThrowOnPaymentSave(new OutPoint(txid, 0).ToString());
        else
            await faultContext.Database.ExecuteSqlRawAsync(
                """ALTER TABLE "Payments" ADD CONSTRAINT reject_first_output CHECK ("Amount" <> 0.00001234)""", harness.Token);
        await harness.Listener.ProcessPendingTransactionsAsync(harness.Listener.ProcessPendingTransactionAsync, harness.Token);
        Assert.Single(harness.Tracker.PendingTransactions);
        await using (var context = invoices.CreateContext())
        {
            var payment = Assert.Single(await context.Payments.AsNoTracking().ToListAsync(harness.Token));
            Assert.Equal(secondInvoice, payment.InvoiceDataId);
            Assert.Equal(new OutPoint(txid, 1).ToString(), payment.Id);
        }
        Assert.Equal(1, harness.ReceivedPayments);

        if (originalBlob is not null)
            Assert.Equal(1, await firstInvoiceRow.ExecuteUpdateAsync(
                b => b.SetProperty(i => i.Blob2, originalBlob), harness.Token));
        else if (fault == "write-exception")
            invoices.SaveInterceptor = null;
        else
            await faultContext.Database.ExecuteSqlRawAsync(
                """ALTER TABLE "Payments" DROP CONSTRAINT reject_first_output""", harness.Token);
        await harness.Listener.ProcessPendingTransactionsAsync(harness.Listener.ProcessPendingTransactionAsync, harness.Token);
        await using var retriedContext = invoices.CreateContext();
        var payments = await retriedContext.Payments.AsNoTracking().OrderBy(p => p.Id).ToListAsync(harness.Token);
        Assert.Equal(new[] { firstInvoice, secondInvoice }, payments.Select(p => p.InvoiceDataId));
        Assert.Equal(new[] { new OutPoint(txid, 0).ToString(), new OutPoint(txid, 1).ToString() }, payments.Select(p => p.Id));
        Assert.Equal(2, harness.ReceivedPayments);
        Assert.Empty(harness.Tracker.PendingTransactions);
    }

    private sealed class ThrowOnPaymentSave(string outpoint) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<PaymentData>().Any(entry =>
                    entry.State == EntityState.Added && entry.Entity.Id == outpoint))
                throw new IOException("Payment write temporarily unavailable");
            return base.SavingChangesAsync(eventData, result, ct);
        }
    }
}
