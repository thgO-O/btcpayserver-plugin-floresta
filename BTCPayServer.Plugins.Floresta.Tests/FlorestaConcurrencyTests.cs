using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Floresta.Data;
using BTCPayServer.Plugins.Floresta.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests;

public class FlorestaConcurrencyTests
{
    [Fact]
    public async Task WalletLockSerializesActionsForSameWallet()
    {
        var sync = new FlorestaWalletSync(
            null!,
            null!,
            null!,
            null!,
            new ConcurrentDictionary<string, DerivationStrategyBase>(),
            () => 0,
            Network.RegTest,
            NullLogger.Instance);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var first = sync.RunWithWalletLockAsync("wallet", async () =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task.WaitAsync(cts.Token);
        }, CancellationToken.None);
        await firstEntered.Task.WaitAsync(cts.Token);

        var second = sync.RunWithWalletLockAsync("wallet", () =>
        {
            secondEntered.SetResult();
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.False(secondEntered.Task.IsCompleted);
        Assert.False(second.IsCompleted);

        releaseFirst.SetResult();
        await Task.WhenAll(first, second).WaitAsync(cts.Token);
        Assert.True(secondEntered.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public void StatusMonitorKeepsTipHeightMonotonic()
    {
        var monitor = CreateStatusMonitor();

        monitor.UpdateTipHeight(12);
        monitor.UpdateTipHeight(10);

        Assert.Equal(12, monitor.TipHeight);
    }

    [Fact]
    public void StatusMonitorAcceptsAuthoritativeTipHeightDecrease()
    {
        var monitor = CreateStatusMonitor();

        monitor.SetTipHeight(12);
        monitor.SetTipHeight(10);

        Assert.Equal(10, monitor.TipHeight);
    }

    [Fact]
    public void ConfirmationsNeverGoNegativeWhenTipDropsBelowCachedBlock()
    {
        Assert.Equal(0, FlorestaWalletTracker.GetConfirmations(12, 10));
        Assert.Equal(1, FlorestaWalletTracker.GetConfirmations(10, 10));
        Assert.Equal(3, FlorestaWalletTracker.GetConfirmations(8, 10));
        Assert.Equal(0, FlorestaWalletTracker.GetConfirmations(0, 10));
        Assert.Equal(0, FlorestaWalletTracker.GetConfirmations(null, 10));
    }

    [Fact]
    public void ConfirmedHeightIsZeroForUnconfirmedResponses()
    {
        Assert.Equal(0, FlorestaWalletTracker.GetConfirmedHeight(12, 0));
        Assert.Equal(12, FlorestaWalletTracker.GetConfirmedHeight(12, 1));
        Assert.Equal(0, FlorestaWalletTracker.GetConfirmedHeight(0, 1));
        Assert.Equal(0, FlorestaWalletTracker.GetConfirmedHeight(null, 1));
    }

    [Fact]
    public void HistoryBlockHeightDemotesCachedTransactionWhenUnconfirmed()
    {
        var tx = new TrackedTransaction { BlockHeight = 12 };

        FlorestaWalletSync.ApplyHistoryBlockHeight(tx, 0);

        Assert.Null(tx.BlockHeight);
    }

    [Fact]
    public void HistoryBlockHeightUpdatesCachedTransactionWhenConfirmed()
    {
        var tx = new TrackedTransaction { BlockHeight = null };

        FlorestaWalletSync.ApplyHistoryBlockHeight(tx, 12);

        Assert.Equal(12, tx.BlockHeight);
    }

    [Fact]
    public void MissingHistoryDemotesOnlyPreviouslyConfirmedTransactions()
    {
        var present = new TrackedTransaction { Txid = "present", BlockHeight = 12 };
        var missing = new TrackedTransaction { Txid = "missing", BlockHeight = 12 };
        var unconfirmed = new TrackedTransaction { Txid = "unconfirmed", BlockHeight = null };

        var demoted = FlorestaWalletSync.DemoteTransactionsMissingFromHistory(
            new[] { present, missing, unconfirmed },
            new HashSet<string> { "present" });

        Assert.Equal(1, demoted);
        Assert.Equal(12, present.BlockHeight);
        Assert.Null(missing.BlockHeight);
        Assert.Null(unconfirmed.BlockHeight);
    }

    private static FlorestaStatusMonitor CreateStatusMonitor()
    {
        return new FlorestaStatusMonitor(
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            NullLogger<FlorestaStatusMonitor>.Instance);
    }
}
