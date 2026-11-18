using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using Microsoft.EntityFrameworkCore;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests;

public partial class FlorestaListenerInitializationTests
{
    [Theory]
    [InlineData("import", false)]
    [InlineData("initialization", false)]
    [InlineData("import", true)]
    [InlineData("initialization", true)]
    public async Task RecoveryReceiptsAreRetainedBeforePostCommitSubscriptions(string trigger, bool cancelAfterCommit)
    {
        await VerifyCommittedRecoveryDeliveryAsync(new InvoiceDatabase(), trigger, cancelAfterCommit);
    }

    [Theory]
    [Trait("Integration", "Integration")]
    [InlineData("import")]
    [InlineData("initialization")]
    public async Task PostCommitCancellationRetainsRecoveryReceiptsInPostgres(string trigger)
    {
        await using var invoices = await PostgresInvoiceDatabase.CreateAsync();
        await VerifyCommittedRecoveryDeliveryAsync(invoices, trigger, true);
    }

    private static async Task VerifyCommittedRecoveryDeliveryAsync(
        ApplicationDbContextFactory invoices, string trigger, bool cancelAfterCommit)
    {
        await using var harness = new Harness(invoices: invoices);
        Assert.Equal(90, await harness.InitializeAsync());
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        var addresses = await harness.Tracker.PrepareWalletTrackingAsync(wallet, harness.Token);
        // The last initial address extends the gap and requires a subscription after commit.
        var invoiceId = await harness.CreateInvoiceAsync(addresses[1]);
        var transaction = harness.SetRecoveredHistory(addresses[1], true);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(harness.Token);
        var sawCommittedSubscription = false;
        harness.BeforeSubscription = async () =>
        {
            await using var ctx = harness.Database.CreateContext();
            if (!await ctx.Transactions.AnyAsync(t => t.Txid == transaction.GetHash().ToString(), harness.Token))
                return;
            sawCommittedSubscription = true;
            if (cancelAfterCommit)
                canceled.Cancel();
        };
        async Task Synchronize()
        {
            if (trigger == "initialization")
                await harness.Tracker.InitializeAsync(canceled.Token);
            else
                await harness.Tracker.TrackWalletAsync(wallet, canceled.Token);
        }
        if (cancelAfterCommit)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(Synchronize);
        else
            await Synchronize();
        harness.BeforeSubscription = () => Task.CompletedTask;
        Assert.True(sawCommittedSubscription);
        Assert.Equal(transaction.GetHash().ToString(), Assert.Single(harness.Tracker.RecoveredTransactions).TxId);
        await using (var ctx = harness.Database.CreateContext())
            Assert.Empty(await ctx.Utxos.Where(u => !u.IsSpent).ToListAsync(harness.Token));
        await harness.RunUntilPaymentAsync();
        await using var invoiceContext = invoices.CreateContext();
        var payment = Assert.Single(await invoiceContext.Payments.AsNoTracking().ToListAsync(harness.Token));
        Assert.Equal(invoiceId, payment.InvoiceDataId);
        Assert.Equal(new OutPoint(transaction.GetHash(), 0).ToString(), payment.Id);
        Assert.Equal(0.00001234m, payment.Amount);
        Assert.Equal(PaymentStatus.Settled, payment.Status);
        Assert.Equal(1, harness.ReceivedPayments);
        Assert.Equal(1, harness.OnChainEvents);
        Assert.Empty(harness.Tracker.RecoveredTransactions);
        Assert.Equal(0, harness.RescanRequests);
    }

    [Fact]
    public async Task FailedRecoveryCacheCommitDoesNotPublishReceipts()
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
        Assert.Empty(harness.Tracker.RecoveredTransactions);
        await using (var cache = harness.Database.CreateContext())
            Assert.Empty(await cache.Transactions.ToListAsync(harness.Token));
        Assert.True(harness.Tracker.HasDeferredWalletSynchronizations);
        await harness.Tracker.RetryPendingWalletsAsync(harness.Token);
        Assert.Single(harness.Tracker.RecoveredTransactions);
        await harness.RunUntilPaymentAsync();
        await using var ctx = invoices.CreateContext();
        Assert.Equal(invoiceId, Assert.Single(await ctx.Payments.ToListAsync(harness.Token)).InvoiceDataId);
        Assert.Equal(1, harness.ReceivedPayments);
    }
}
