using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Floresta;

internal enum ReceiptDeliveryResult
{
    Retry,
    Delivered,
    Discard
}

// Owns committed receipts until delivery succeeds or the consumer explicitly
// discards them. This is process-local delivery, not a durable outbox.
internal sealed class FlorestaReceiptQueue : IReadOnlyCollection<FlorestaWalletTracker.NewTransactionInfo>
{
    private readonly ConcurrentQueue<FlorestaWalletTracker.NewTransactionInfo> _pending = new();
    private readonly SemaphoreSlim _consumerLock = new(1, 1);

    public int Count => _pending.Count;
    public bool IsEmpty => _pending.IsEmpty;

    public void Enqueue(FlorestaWalletTracker.NewTransactionInfo transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        _pending.Enqueue(transaction);
    }

    public void Enqueue(IEnumerable<FlorestaWalletTracker.NewTransactionInfo> transactions)
    {
        foreach (var transaction in transactions)
            Enqueue(transaction);
    }

    public async Task ProcessPendingAsync(
        Func<FlorestaWalletTracker.NewTransactionInfo, CancellationToken, Task<ReceiptDeliveryResult>> process,
        ILogger logger,
        CancellationToken ct)
    {
        await _consumerLock.WaitAsync(ct);
        try
        {
            // Bound each pass; failed receipts rotate so other wallets can progress.
            var count = _pending.Count;
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (!_pending.TryPeek(out var transaction))
                    break;
                var result = ReceiptDeliveryResult.Retry;
                try
                {
                    result = await process(transaction, ct);
                    ct.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to deliver transaction {TxId}; retrying", transaction.TxId);
                }

                // Keep the head throughout the await. Only this serialized
                // consumer may acknowledge it; cancellation leaves it pending.
                if (_pending.TryDequeue(out var receipt) &&
                    result is not ReceiptDeliveryResult.Delivered and not ReceiptDeliveryResult.Discard)
                    _pending.Enqueue(receipt);
            }
        }
        finally
        {
            _consumerLock.Release();
        }
    }

    public IEnumerator<FlorestaWalletTracker.NewTransactionInfo> GetEnumerator() => _pending.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
