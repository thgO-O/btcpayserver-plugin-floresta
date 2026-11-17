using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Floresta.Data;
using Microsoft.EntityFrameworkCore;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests;

public partial class FlorestaListenerInitializationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportSyncExceptionRetainsRecoveryUntilSpentReceiptIsDelivered(bool unrelatedCancellation)
    {
        var invoices = new InvoiceDatabase();
        await using var harness = new Harness(invoices: invoices);
        Assert.Equal(90, await harness.InitializeAsync());
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        var address = (await harness.Tracker.PrepareWalletTrackingAsync(wallet, harness.Token))[0];
        var invoiceId = await harness.CreateInvoiceAsync(address);
        var transaction = harness.SetRecoveredHistory(address, true);
        var failed = false;
        harness.BeforeSubscription = () =>
        {
            if (!failed)
            {
                failed = true;
                if (unrelatedCancellation)
                    harness.Database.BeforeCreateContext = () =>
                    {
                        harness.Database.BeforeCreateContext = null;
                        throw new OperationCanceledException();
                    };
                else
                    harness.Database.FailNextContext = true;
            }
            return Task.CompletedTask;
        };
        await harness.ImportWalletAsync(wallet);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(harness.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await WaitForTrackingCleanupAsync(harness.Tracker, deadline.Token);
        Assert.True(failed);
        Assert.True(harness.Tracker.HasDeferredWalletSynchronizations);
        while (harness.Tracker.RecoveredTransactions.IsEmpty)
        {
            Assert.Null(await harness.InitializeAsync());
            await Task.Delay(10, deadline.Token);
        }
        await harness.RunUntilPaymentAsync();
        await using var ctx = invoices.CreateContext();
        var payment = Assert.Single(await ctx.Payments.ToListAsync(harness.Token));
        Assert.Equal(invoiceId, payment.InvoiceDataId);
        Assert.Equal(new OutPoint(transaction.GetHash(), 0).ToString(), payment.Id);
        Assert.Equal(1, harness.ReceivedPayments);
        Assert.Equal(0, harness.RescanRequests);
    }

    [Fact]
    public async Task RetryKeepsGenerationAuthorizedBeforeAddressLookup()
    {
        await using var harness = new Harness();
        Assert.Equal(90, await harness.InitializeAsync());
        harness.UpdateSettings(s => s.AutoRegisterDescriptors = false);
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        var address = (await harness.Tracker.PrepareWalletTrackingAsync(wallet, harness.Token))[0];
        await harness.AddCachedOutputAsync(address);
        harness.BlockchainInfo = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        await harness.Tracker.TrackWalletAsync(wallet, harness.Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(harness.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var contexts = 0;
        // Discovery and validation each create a context; the third loads addresses.
        harness.Database.BeforeCreateContext = () =>
        {
            if (Interlocked.Increment(ref contexts) != 3) return;
            entered.TrySetResult();
            release.Task.WaitAsync(deadline.Token).GetAwaiter().GetResult();
        };
        var retry = Task.Run(() => harness.Tracker.RetryPendingWalletsAsync(deadline.Token));
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            harness.UpdateSettings(s => { s.AutoRegisterDescriptors = true; s.AutoRescanOnNewDescriptor = true; });
            harness.BlockchainInfo = _ => Harness.Info(false);
            harness.Rescan = _ => Task.FromResult(Harness.Json(false));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                harness.Tracker.PrepareWalletTrackingAsync(wallet, deadline.Token));
            Assert.Equal(FlorestaWalletDescriptorStatus.Registered, (await harness.GetWalletAsync(wallet)).DescriptorStatus);
        }
        finally
        {
            harness.Database.BeforeCreateContext = null;
            harness.BlockchainInfo = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            release.TrySetResult();
            await retry.WaitAsync(deadline.Token);
        }
        Assert.True(harness.Tracker.HasDeferredWalletSynchronizations);
        harness.BlockchainInfo = _ => Harness.Info(false);
        await harness.Tracker.RetryPendingWalletsAsync(deadline.Token);
        Assert.False(harness.Tracker.HasDeferredWalletSynchronizations);
        Assert.False(await harness.CachedOutputIsSpentAsync());
        Assert.Equal(0, harness.HistoryRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OlderSyncResultDoesNotDiscardNewerRecovery(bool olderComplete)
    {
        await using var harness = new Harness();
        Assert.Equal(90, await harness.InitializeAsync());
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        var address = (await harness.Tracker.PrepareWalletTrackingAsync(wallet, harness.Token))[0];
        await harness.AddCachedOutputAsync(address);
        harness.Database.FailNextContext = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Tracker.PrepareWalletTrackingAsync(wallet, harness.Token));
        var deferred = new FlorestaWalletSync.WalletSyncResult(new(), new(0, 0, 1), Deferred: true);
        // The sync lock is released before recording; model that result order here.
        harness.Tracker.RecordRecoverySynchronizationResult(wallet, deferred, 1);
        harness.Tracker.RecordRecoverySynchronizationResult(wallet,
            olderComplete ? new(new(), new(1, 1, 0)) : deferred, 0);
        await harness.RetryUntilWalletReconciledAsync();
        Assert.True(await harness.CachedOutputIsSpentAsync());
        Assert.Equal(0, harness.RescanRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectImportSyncPreservesExceptionAndCancellation(bool canceled)
    {
        await using var harness = new Harness();
        Assert.Equal(90, await harness.InitializeAsync());
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(harness.Token);
        var failure = new InvalidOperationException("Wallet database unavailable");
        harness.BeforeSubscription = () =>
        {
            harness.Database.BeforeCreateContext = () =>
            {
                if (canceled)
                {
                    cancellation.Cancel();
                    cancellation.Token.ThrowIfCancellationRequested();
                }
                throw failure;
            };
            return Task.CompletedTask;
        };
        if (canceled)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                harness.Tracker.TrackWalletAsync(wallet, cancellation.Token));
        else
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
                harness.Tracker.TrackWalletAsync(wallet, cancellation.Token)));
        harness.Database.BeforeCreateContext = null;
        Assert.Equal(!canceled, harness.Tracker.HasDeferredWalletSynchronizations);
    }

    private static async Task WaitForTrackingCleanupAsync(FlorestaWalletTracker tracker, CancellationToken ct)
    {
        var flights = (ConcurrentDictionary<string, TaskCompletionSource<Exception>>)typeof(FlorestaWalletTracker)
            .GetField("_trackingPreparations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tracker)!;
        while (!flights.IsEmpty)
            await Task.Delay(10, ct);
    }
}
