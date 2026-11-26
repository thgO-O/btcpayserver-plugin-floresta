using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests;

public class FlorestaReceiptQueueTests
{
    [Fact]
    public async Task ConcurrentDeliveryPassesHaveOnlyOneConsumer()
    {
        var queue = new FlorestaReceiptQueue();
        queue.Enqueue(new FlorestaWalletTracker.NewTransactionInfo { TxId = "first" });
        queue.Enqueue(new FlorestaWalletTracker.NewTransactionInfo { TxId = "second" });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var delivered = new ConcurrentBag<string>();
        var calls = 0;
        async Task<ReceiptDeliveryResult> Deliver(FlorestaWalletTracker.NewTransactionInfo receipt, CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            delivered.Add(receipt.TxId);
            return ReceiptDeliveryResult.Delivered;
        }
        var first = queue.ProcessPendingAsync(Deliver, NullLogger.Instance, timeout.Token);
        await entered.Task.WaitAsync(timeout.Token);
        var second = queue.ProcessPendingAsync(Deliver, NullLogger.Instance, timeout.Token);
        try
        {
            Assert.False(second.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(timeout.Token);
        }
        Assert.Equal(2, calls);
        Assert.Equal(2, delivered.Count);
        Assert.Contains("first", delivered);
        Assert.Contains("second", delivered);
        Assert.Empty(queue);
    }

    [Fact]
    public async Task CancellationAfterDeliveryKeepsUnacknowledgedReceipt()
    {
        var queue = new FlorestaReceiptQueue();
        var receipt = new FlorestaWalletTracker.NewTransactionInfo { TxId = "receipt" };
        queue.Enqueue(receipt);
        using var canceled = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.ProcessPendingAsync((_, _) =>
        {
            canceled.Cancel();
            return Task.FromResult(ReceiptDeliveryResult.Delivered);
        }, NullLogger.Instance, canceled.Token));
        Assert.Same(receipt, Assert.Single(queue));
        await queue.ProcessPendingAsync((_, _) => Task.FromResult(ReceiptDeliveryResult.Delivered),
            NullLogger.Instance, CancellationToken.None);
        Assert.Empty(queue);
    }
}
