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

    [Fact]
    public async Task PostCommitSubscriptionCancellationDoesNotLoseTheReceipt()
    {
        var invoices = new InvoiceDatabase();
        await using var harness = new Harness(invoices: invoices);
        Assert.Equal(90, await harness.InitializeAsync());
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        var addresses = await harness.Tracker.PrepareWalletTrackingAsync(wallet, harness.Token);
        var invoiceId = await harness.CreateInvoiceAsync(addresses[1]);
        var transaction = harness.SetRecoveredHistory(addresses[1], true);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(harness.Token);
        harness.BeforeSubscription = async () =>
        {
            await using var ctx = harness.Database.CreateContext();
            if (await ctx.Transactions.AnyAsync(t => t.Txid == transaction.GetHash().ToString(), harness.Token))
                canceled.Cancel();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Tracker.TrackWalletAsync(wallet, canceled.Token));
        harness.BeforeSubscription = () => Task.CompletedTask;
        Assert.Single(harness.Tracker.PendingTransactions);
        await harness.RunUntilPaymentAsync();
        await using var invoicesContext = invoices.CreateContext();
        Assert.Equal(invoiceId, Assert.Single(await invoicesContext.Payments.ToListAsync(harness.Token)).InvoiceDataId);
        Assert.Equal(1, harness.ReceivedPayments);
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

    [Fact]
    public async Task ReceiptIsNotPublishedUntilTheWalletCacheCommitSucceeds()
    {
        var invoices = new InvoiceDatabase();
        await using var harness = new Harness(invoices: invoices);
        Assert.Equal(90, await harness.InitializeAsync());
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        var address = (await harness.Tracker.PrepareWalletTrackingAsync(wallet, harness.Token))[0];
        var invoiceId = await harness.CreateInvoiceAsync(address);
        harness.SetRecoveredHistory(address, true);
        harness.BeforeSubscription = () =>
        {
            harness.Database.FailNextSave = true;
            return Task.CompletedTask;
        };
        await Assert.ThrowsAsync<DbUpdateException>(() => harness.Tracker.TrackWalletAsync(wallet, harness.Token));
        harness.BeforeSubscription = () => Task.CompletedTask;
        Assert.False(harness.Database.FailNextSave);
        Assert.Empty(harness.Tracker.PendingTransactions);
        await using (var cache = harness.Database.CreateContext())
            Assert.Empty(await cache.Transactions.ToListAsync(harness.Token));
        Assert.True(harness.Tracker.HasDeferredWalletSynchronizations);
        await harness.Tracker.RetryPendingWalletsAsync(harness.Token);
        Assert.Single(harness.Tracker.PendingTransactions);
        await harness.RunUntilPaymentAsync();
        await using var ctx = invoices.CreateContext();
        Assert.Equal(invoiceId, Assert.Single(await ctx.Payments.ToListAsync(harness.Token)).InvoiceDataId);
        Assert.Equal(1, harness.ReceivedPayments);
    }
}
