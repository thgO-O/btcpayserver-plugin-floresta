using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Floresta.Data;
using BTCPayServer.Plugins.Floresta.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;

namespace BTCPayServer.Plugins.Floresta;

internal sealed class FlorestaWalletSync
{
    internal readonly record struct WalletAddressSyncSummary(
        int Attempted,
        int Succeeded,
        int Failed)
    {
        public bool IsComplete => Failed == 0;
    }

    internal sealed record WalletSyncResult(
        List<FlorestaWalletTracker.NewTransactionInfo> NewTransactions,
        WalletAddressSyncSummary Summary);

    private readonly FlorestaDbContextFactory _dbFactory;
    private readonly FlorestaElectrumClient _client;
    private readonly FlorestaRpcClient _rpcClient;
    private readonly FlorestaAddressPool _addressPool;
    private readonly FlorestaUtxoCache _utxoCache;
    private readonly ConcurrentDictionary<string, DerivationStrategyBase> _trackedStrategies;
    private readonly Func<int> _getTipHeight;
    private readonly Network _network;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _walletLocks = new();

    public FlorestaWalletSync(
        FlorestaDbContextFactory dbFactory,
        FlorestaElectrumClient client,
        FlorestaRpcClient rpcClient,
        FlorestaAddressPool addressPool,
        FlorestaUtxoCache utxoCache,
        ConcurrentDictionary<string, DerivationStrategyBase> trackedStrategies,
        Func<int> getTipHeight,
        Network network,
        ILogger logger)
    {
        _dbFactory = dbFactory;
        _client = client;
        _rpcClient = rpcClient;
        _addressPool = addressPool;
        _utxoCache = utxoCache;
        _trackedStrategies = trackedStrategies;
        _getTipHeight = getTipHeight;
        _network = network;
        _logger = logger;
    }

    public async Task SubscribeTrackedAddressesAsync(IEnumerable<TrackedAddress> addresses, CancellationToken ct)
    {
        if (!_client.IsConnected)
            return;

        var subscribed = new HashSet<string>();
        foreach (var addr in addresses)
        {
            if (!subscribed.Add(addr.Scripthash))
                continue;

            try
            {
                await _client.ScripthashSubscribeAsync(addr.Scripthash, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to subscribe scripthash {Scripthash}", addr.Scripthash);
            }
        }
    }

    public async Task<List<FlorestaWalletTracker.NewTransactionInfo>> SyncWalletStateAsync(
        string walletId,
        List<TrackedAddress> addresses,
        CancellationToken ct,
        Action<int, int> reportProgress = null)
    {
        var result = await SyncWalletStateWithResultAsync(walletId, addresses, ct, reportProgress);
        return result.NewTransactions;
    }

    internal Task<WalletSyncResult> SyncWalletStateForScanAsync(
        string walletId,
        List<TrackedAddress> addresses,
        CancellationToken ct,
        Action<int, int> reportProgress)
    {
        return SyncWalletStateWithResultAsync(walletId, addresses, ct, reportProgress);
    }

    private async Task<WalletSyncResult> SyncWalletStateWithResultAsync(
        string walletId,
        List<TrackedAddress> addresses,
        CancellationToken ct,
        Action<int, int> reportProgress)
    {
        var walletLock = GetWalletLock(walletId);
        await walletLock.WaitAsync(ct);
        try
        {
            return await SyncWalletStateCoreAsync(walletId, addresses, ct, reportProgress);
        }
        finally
        {
            walletLock.Release();
        }
    }

    public async Task<List<FlorestaWalletTracker.NewTransactionInfo>> HandleScripthashNotificationAsync(
        string scripthash,
        CancellationToken ct)
    {
        List<TrackedAddress> sources;
        await using (var lookupCtx = _dbFactory.CreateContext())
        {
            sources = await FlorestaWalletTracker.GetTrackedAddressSourcesByScripthashAsync(
                lookupCtx,
                scripthash,
                ct);
            if (sources.Count == 0)
                return new List<FlorestaWalletTracker.NewTransactionInfo>();
        }

        var newTxs = new List<FlorestaWalletTracker.NewTransactionInfo>();
        foreach (var source in sources)
            newTxs.AddRange(await SyncAddressNotificationAsync(source, ct));
        return newTxs;
    }

    private async Task<List<FlorestaWalletTracker.NewTransactionInfo>> SyncAddressNotificationAsync(
        TrackedAddress source,
        CancellationToken ct)
    {
        var walletId = source.WalletId;
        var walletLock = GetWalletLock(walletId);
        await walletLock.WaitAsync(ct);
        try
        {
            List<TrackedAddress> walletAddresses;
            await using (var ctx = _dbFactory.CreateContext())
            {
                walletAddresses = await FlorestaWalletTracker.GetTrackedAddressSourcesAsync(ctx, walletId, ct);
                if (walletAddresses.All(a => a.Scripthash != source.Scripthash))
                    walletAddresses.Add(source);
            }

            var result = await SyncWalletStateCoreAsync(walletId, walletAddresses, ct, null);
            return result.NewTransactions;
        }
        finally
        {
            walletLock.Release();
        }
    }

    public async Task RunWithWalletLockAsync(string walletId, Func<Task> action, CancellationToken ct)
    {
        var walletLock = GetWalletLock(walletId);
        await walletLock.WaitAsync(ct);
        try
        {
            await action();
        }
        finally
        {
            walletLock.Release();
        }
    }

    public async Task<List<FlorestaWalletTracker.NewTransactionInfo>> HandleNewBlockAsync(
        int height,
        CancellationToken ct)
    {
        if (await IsNodeInInitialBlockDownloadAsync(ct))
            return new();

        await using (var ctx = _dbFactory.CreateContext())
        {
            var state = await ctx.SyncStates.FindAsync(new object[] { "tip_height" }, ct);
            if (state != null)
            {
                state.Value = height.ToString();
                state.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                ctx.SyncStates.Add(new SyncState
                {
                    Key = "tip_height",
                    Value = height.ToString(),
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }

            await ctx.SaveChangesAsync(ct);
        }

        List<IGrouping<string, TrackedAddress>> wallets;
        await using (var ctx = _dbFactory.CreateContext())
        {
            var addresses = await FlorestaWalletTracker.GetAllTrackedAddressSourcesAsync(ctx, ct);
            wallets = addresses
                .GroupBy(a => a.WalletId)
                .ToList();
        }

        var newTxs = new List<FlorestaWalletTracker.NewTransactionInfo>();
        foreach (var wallet in wallets)
        {
            newTxs.AddRange(await SyncWalletStateAsync(wallet.Key, wallet.ToList(), ct));
        }

        return newTxs;
    }

    private async Task<WalletSyncResult> SyncWalletStateCoreAsync(
        string walletId,
        List<TrackedAddress> addresses,
        CancellationToken ct,
        Action<int, int> reportProgress)
    {
        var connectionGeneration = _client.ConnectionGeneration;
        if (await IsNodeInInitialBlockDownloadAsync(ct))
        {
            // A successful response from an incomplete node is not evidence
            // that a cached transaction or output has disappeared.
            return new(new(), new WalletAddressSyncSummary(0, 0, addresses.Count));
        }
        EnsureConnectionGeneration(connectionGeneration);

        await using var ctx = _dbFactory.CreateContext();
        var addressesToSubscribe = new List<TrackedAddress>();
        var newTxs = new List<FlorestaWalletTracker.NewTransactionInfo>();
        var observedHistoryTxids = new HashSet<string>();
        var existingTxids = await ctx.Transactions
            .Where(t => t.WalletId == walletId)
            .Select(t => t.Txid)
            .ToHashSetAsync(ct);

        _trackedStrategies.TryGetValue(walletId, out var strategy);

        var persistedWalletAddresses = await FlorestaWalletTracker.GetTrackedAddressSourcesAsync(ctx, walletId, ct);
        var walletAddresses = BuildWalletAddressMap(persistedWalletAddresses, addresses, walletId);

        var summary = await ProcessAddressQueueAsync(
            addresses,
            async (addr, token) =>
            {
                if (walletAddresses.TryGetValue(addr.Scripthash, out var trackedAddress))
                {
                    var beforeSubscribeCount = addressesToSubscribe.Count;
                    newTxs.AddRange(await SyncAddressAsync(
                        ctx,
                        trackedAddress,
                        strategy,
                        walletAddresses,
                        existingTxids,
                        observedHistoryTxids,
                        token,
                        addressesToSubscribe,
                        connectionGeneration));

                    var addedAddresses = addressesToSubscribe.Skip(beforeSubscribeCount).ToList();
                    foreach (var added in addedAddresses)
                        walletAddresses[added.Scripthash] = added;
                    return addedAddresses;
                }

                return Array.Empty<TrackedAddress>();
            },
            (addr, ex) =>
            {
                _logger.LogWarning(ex, "Error syncing address {Address} ({Scripthash})",
                    addr.Address, addr.Scripthash);
            },
            reportProgress,
            ct);

        var removedCount = await MarkTransactionsMissingFromHistoryAsync(
            ctx,
            walletId,
            observedHistoryTxids,
            summary.IsComplete && addresses.Count > 0,
            ct);
        if (removedCount > 0)
        {
            _logger.LogInformation(
                "Marked {TransactionCount} cached Floresta transactions missing from wallet history as removed for wallet {WalletId}",
                removedCount,
                LogSafeId.Hash(walletId));
        }

        await ctx.SaveChangesAsync(ct);
        await SubscribeTrackedAddressesAsync(addressesToSubscribe, ct);
        return new WalletSyncResult(newTxs, summary);
    }

    private async Task<bool> IsNodeInInitialBlockDownloadAsync(CancellationToken ct)
    {
        var info = FlorestaChainInfoParser.Parse(await _rpcClient.GetBlockchainInfoAsync(ct));
        return info.IsInitialBlockDownload == true;
    }

    private void EnsureConnectionGeneration(long expectedGeneration)
    {
        if (_client.ConnectionGeneration != expectedGeneration)
            throw new InvalidOperationException("Floresta Electrum connection changed during wallet synchronization.");
    }

    internal static async Task<WalletAddressSyncSummary> ProcessAddressQueueAsync(
        IReadOnlyCollection<TrackedAddress> initialAddresses,
        Func<TrackedAddress, CancellationToken, Task<IReadOnlyCollection<TrackedAddress>>> processAddress,
        Action<TrackedAddress, Exception> onError,
        Action<int, int> reportProgress,
        CancellationToken ct)
    {
        var pending = new Queue<TrackedAddress>();
        var queuedOrProcessed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var initialAddress in initialAddresses)
        {
            if (queuedOrProcessed.Add(initialAddress.Scripthash))
                pending.Enqueue(initialAddress);
        }
        var attempted = 0;
        var succeeded = 0;
        var failed = 0;
        var total = pending.Count;

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var address = pending.Dequeue();
            attempted++;
            try
            {
                var addedAddresses = await processAddress(address, ct) ?? Array.Empty<TrackedAddress>();
                ct.ThrowIfCancellationRequested();
                succeeded++;
                foreach (var addedAddress in addedAddresses)
                {
                    if (!queuedOrProcessed.Add(addedAddress.Scripthash))
                        continue;

                    pending.Enqueue(addedAddress);
                    total++;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                onError?.Invoke(address, ex);
            }

            reportProgress?.Invoke(succeeded, total);
        }

        return new WalletAddressSyncSummary(attempted, succeeded, failed);
    }

    private async Task<List<FlorestaWalletTracker.NewTransactionInfo>> SyncAddressAsync(
        FlorestaDbContext ctx,
        TrackedAddress addr,
        DerivationStrategyBase strategy,
        IDictionary<string, TrackedAddress> walletAddresses,
        HashSet<string> existingTxids,
        HashSet<string> observedHistoryTxids,
        CancellationToken ct,
        List<TrackedAddress> addressesToSubscribe,
        long connectionGeneration)
    {
        EnsureConnectionGeneration(connectionGeneration);
        var newTxs = new List<FlorestaWalletTracker.NewTransactionInfo>();
        var history = await _client.ScripthashGetHistoryAsync(addr.Scripthash, ct);
        var canUpdateAddressState = ctx.Entry(addr).State != EntityState.Detached;
        var utxos = canUpdateAddressState ? await _utxoCache.GetReconciledAsync(addr, ct) : null;
        EnsureConnectionGeneration(connectionGeneration);
        foreach (var item in history)
            observedHistoryTxids?.Add(item.TxHash);

        foreach (var item in history)
        {
            if (existingTxids.Contains(item.TxHash))
            {
                var existing = await ctx.Transactions
                    .FirstOrDefaultAsync(t => t.Txid == item.TxHash && t.WalletId == addr.WalletId, ct);
                if (existing != null)
                    ApplyHistoryBlockHeight(existing, item.Height);
                continue;
            }

            var rawHex = await _client.TransactionGetAsync(item.TxHash, ct);
            var tx = Transaction.Parse(rawHex, _network);
            var balanceChange = ComputeBalanceChange(ctx, tx, addr.WalletId, walletAddresses.Values);

            ctx.Transactions.Add(new TrackedTransaction
            {
                Txid = item.TxHash,
                WalletId = addr.WalletId,
                RawTx = tx.ToBytes(),
                BlockHeight = item.Height > 0 ? item.Height : null,
                Fee = item.Fee > 0 ? item.Fee : null,
                BalanceChange = balanceChange,
                SeenAt = DateTimeOffset.UtcNow
            });
            existingTxids.Add(item.TxHash);

            var txInfo = BuildNewTransactionInfo(tx, strategy, addr, item, walletAddresses);
            if (txInfo != null)
                newTxs.Add(txInfo);
        }

        if (canUpdateAddressState)
        {
            await _utxoCache.UpdateForAddressAsync(ctx, addr, utxos, ct);
        }

        if (canUpdateAddressState && history.Length > 0 && !addr.IsUsed)
        {
            addr.IsUsed = true;
            var addedAddresses = await _addressPool.ExtendGapIfNeededAsync(ctx, addr, ct);
            foreach (var added in addedAddresses)
            {
                walletAddresses[added.Scripthash] = added;
                addressesToSubscribe.Add(added);
            }
        }

        return newTxs;
    }

    private SemaphoreSlim GetWalletLock(string walletId)
    {
        return _walletLocks.GetOrAdd(walletId, _ => new SemaphoreSlim(1, 1));
    }

    private long ComputeBalanceChange(
        FlorestaDbContext ctx,
        Transaction tx,
        string walletId,
        IEnumerable<TrackedAddress> walletAddresses)
    {
        var ourScripts = walletAddresses
            .Select(a => a.ScriptPubKey)
            .ToHashSet(new ByteArrayComparer());

        long change = 0;

        foreach (var output in tx.Outputs)
        {
            if (ourScripts.Contains(output.ScriptPubKey.ToBytes()))
                change += output.Value.Satoshi;
        }

        foreach (var input in tx.Inputs)
        {
            var prevOutpoint = $"{input.PrevOut.Hash}:{input.PrevOut.N}";
            var ourUtxo = ctx.Utxos.Local.FirstOrDefault(u => u.Outpoint == prevOutpoint && u.WalletId == walletId)
                          ?? ctx.Utxos.FirstOrDefault(u => u.Outpoint == prevOutpoint && u.WalletId == walletId);
            if (ourUtxo != null)
                change -= ourUtxo.Value;
        }

        return change;
    }

    private FlorestaWalletTracker.NewTransactionInfo BuildNewTransactionInfo(
        Transaction tx,
        DerivationStrategyBase strategy,
        TrackedAddress triggeringAddress,
        FlorestaElectrumHistoryItem historyItem,
        IDictionary<string, TrackedAddress> walletAddresses)
    {
        var tipHeight = _getTipHeight();
        var confirmations = FlorestaWalletTracker.GetConfirmations(historyItem.Height, tipHeight);
        var info = new FlorestaWalletTracker.NewTransactionInfo
        {
            TxId = tx.GetHash().ToString(),
            Transaction = tx,
            DerivationStrategy = strategy,
            TrackedSource = strategy != null
                ? TrackedSource.Create(strategy)
                : TrackedSource.Create(BitcoinAddress.Create(triggeringAddress.Address, _network)),
            Confirmations = confirmations,
            Height = FlorestaWalletTracker.GetConfirmedHeight(historyItem.Height, confirmations),
            IsRbf = tx.RBF,
            SeenAt = DateTimeOffset.UtcNow
        };

        for (var i = 0; i < tx.Outputs.Count; i++)
        {
            var output = tx.Outputs[i];
            var scriptHash = ScriptHashUtility.ComputeScriptHash(output.ScriptPubKey);

            if (!walletAddresses.TryGetValue(scriptHash, out var outputAddress))
                continue;

            var keyPath = outputAddress.KeyPath;
            var parts = string.IsNullOrWhiteSpace(keyPath) ? Array.Empty<string>() : keyPath.Split('/');
            var keyIndex = parts.Length == 2 && int.TryParse(parts[1], out var idx) ? idx : 0;

            info.Outputs.Add(new FlorestaWalletTracker.OutputInfo
            {
                Address = outputAddress.Address,
                TrackedDestination = output.ScriptPubKey.Hash.ToString(),
                Index = i,
                Value = output.Value,
                ScriptPubKey = output.ScriptPubKey,
                KeyPath = keyPath,
                KeyIndex = keyIndex
            });
        }

        return info.Outputs.Count > 0 ? info : null;
    }

    internal static void ApplyHistoryBlockHeight(TrackedTransaction transaction, long historyHeight)
    {
        var blockHeight = historyHeight > 0 ? historyHeight : (long?)null;
        if (transaction.BlockHeight != blockHeight)
            transaction.BlockHeight = blockHeight;
        transaction.RemovedAt = null;
    }

    internal static Dictionary<string, TrackedAddress> BuildWalletAddressMap(
        IEnumerable<TrackedAddress> persistedSources,
        IEnumerable<TrackedAddress> requestedSources,
        string walletId)
    {
        var walletAddresses = (persistedSources ?? Enumerable.Empty<TrackedAddress>())
            .Where(a => a is not null && !string.IsNullOrWhiteSpace(a.Scripthash))
            .GroupBy(a => a.Scripthash)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var source in requestedSources ?? Enumerable.Empty<TrackedAddress>())
        {
            if (source is null ||
                source.WalletId != walletId ||
                string.IsNullOrWhiteSpace(source.Scripthash))
                continue;

            walletAddresses.TryAdd(source.Scripthash, source);
        }

        return walletAddresses;
    }

    internal static int MarkTransactionsMissingFromHistory(
        IEnumerable<TrackedTransaction> transactions,
        IReadOnlySet<string> observedHistoryTxids,
        bool syncComplete)
    {
        if (!syncComplete)
            return 0;

        var removed = 0;
        foreach (var transaction in transactions)
        {
            if (observedHistoryTxids.Contains(transaction.Txid) || transaction.RemovedAt is not null)
                continue;

            transaction.RemovedAt = DateTimeOffset.UtcNow;
            removed++;
        }

        return removed;
    }

    private static async Task<int> MarkTransactionsMissingFromHistoryAsync(
        FlorestaDbContext ctx,
        string walletId,
        IReadOnlySet<string> observedHistoryTxids,
        bool syncComplete,
        CancellationToken ct)
    {
        if (!syncComplete)
            return 0;

        var activeTransactions = await ctx.Transactions
            .Where(t => t.WalletId == walletId && t.RemovedAt == null)
            .ToListAsync(ct);
        return MarkTransactionsMissingFromHistory(activeTransactions, observedHistoryTxids, true);
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[] x, byte[] y) => x.SequenceEqual(y);
        public int GetHashCode(byte[] obj) => BitConverter.ToInt32(obj, 0);
    }
}
