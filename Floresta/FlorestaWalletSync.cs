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

namespace BTCPayServer.Plugins.Floresta;

internal sealed class FlorestaWalletSync
{
    private readonly FlorestaDbContextFactory _dbFactory;
    private readonly FlorestaElectrumClient _client;
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
        FlorestaAddressPool addressPool,
        FlorestaUtxoCache utxoCache,
        ConcurrentDictionary<string, DerivationStrategyBase> trackedStrategies,
        Func<int> getTipHeight,
        Network network,
        ILogger logger)
    {
        _dbFactory = dbFactory;
        _client = client;
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
        string walletId;
        await using (var lookupCtx = _dbFactory.CreateContext())
        {
            var trackedAddress = await lookupCtx.TrackedAddresses.FindAsync(new object[] { scripthash }, ct);
            if (trackedAddress == null)
                return new List<FlorestaWalletTracker.NewTransactionInfo>();
            walletId = trackedAddress.WalletId;
        }

        var walletLock = GetWalletLock(walletId);
        await walletLock.WaitAsync(ct);
        try
        {
            await using var ctx = _dbFactory.CreateContext();
            var reloadedAddress = await ctx.TrackedAddresses.FindAsync(new object[] { scripthash }, ct);
            if (reloadedAddress == null)
                return new List<FlorestaWalletTracker.NewTransactionInfo>();

            var existingTxids = await ctx.Transactions
                .Where(t => t.WalletId == reloadedAddress.WalletId)
                .Select(t => t.Txid)
                .ToHashSetAsync(ct);

            if (!_trackedStrategies.TryGetValue(reloadedAddress.WalletId, out var strategy))
                return new List<FlorestaWalletTracker.NewTransactionInfo>();

            var walletAddresses = await ctx.TrackedAddresses
                .Where(a => a.WalletId == reloadedAddress.WalletId)
                .ToDictionaryAsync(a => a.Scripthash, ct);
            var addressesToSubscribe = new List<TrackedAddress>();
            var newTxs = await SyncAddressAsync(
                ctx,
                reloadedAddress,
                strategy,
                walletAddresses,
                existingTxids,
                null,
                ct,
                addressesToSubscribe);

            await ctx.SaveChangesAsync(ct);
            await SubscribeTrackedAddressesAsync(addressesToSubscribe, ct);
            return newTxs;
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
            var addresses = await ctx.TrackedAddresses
                .AsNoTracking()
                .ToListAsync(ct);
            wallets = addresses
                .GroupBy(a => a.WalletId)
                .ToList();
        }

        var newTxs = new List<FlorestaWalletTracker.NewTransactionInfo>();
        foreach (var wallet in wallets)
        {
            if (!_trackedStrategies.ContainsKey(wallet.Key))
                continue;

            newTxs.AddRange(await SyncWalletStateAsync(wallet.Key, wallet.ToList(), ct));
        }

        return newTxs;
    }

    private async Task<List<FlorestaWalletTracker.NewTransactionInfo>> SyncWalletStateCoreAsync(
        string walletId,
        List<TrackedAddress> addresses,
        CancellationToken ct,
        Action<int, int> reportProgress)
    {
        await using var ctx = _dbFactory.CreateContext();
        var addressesToSubscribe = new List<TrackedAddress>();
        var newTxs = new List<FlorestaWalletTracker.NewTransactionInfo>();
        var observedHistoryTxids = new HashSet<string>();
        var syncHadErrors = false;
        var existingTxids = await ctx.Transactions
            .Where(t => t.WalletId == walletId)
            .Select(t => t.Txid)
            .ToHashSetAsync(ct);

        if (!_trackedStrategies.TryGetValue(walletId, out var strategy))
            return newTxs;

        var walletAddresses = await ctx.TrackedAddresses
            .Where(a => a.WalletId == walletId)
            .ToDictionaryAsync(a => a.Scripthash, ct);

        var processed = 0;
        foreach (var addr in addresses)
        {
            try
            {
                if (walletAddresses.TryGetValue(addr.Scripthash, out var trackedAddress))
                {
                    newTxs.AddRange(await SyncAddressAsync(
                        ctx,
                        trackedAddress,
                        strategy,
                        walletAddresses,
                        existingTxids,
                        observedHistoryTxids,
                        ct,
                        addressesToSubscribe));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                syncHadErrors = true;
                _logger.LogWarning(ex, "Error syncing address {Address} ({Scripthash})",
                    addr.Address, addr.Scripthash);
            }
            finally
            {
                processed++;
                reportProgress?.Invoke(processed, addresses.Count);
            }
        }

        if (!syncHadErrors && addresses.Count > 0)
        {
            var demotedCount = await DemoteTransactionsMissingFromHistoryAsync(ctx, walletId, observedHistoryTxids, ct);
            if (demotedCount > 0)
            {
                _logger.LogInformation(
                    "Demoted {TransactionCount} cached Floresta transactions missing from wallet history for wallet {WalletId}",
                    demotedCount,
                    LogSafeId.Hash(walletId));
            }
        }

        await ctx.SaveChangesAsync(ct);
        await SubscribeTrackedAddressesAsync(addressesToSubscribe, ct);
        return newTxs;
    }

    private async Task<List<FlorestaWalletTracker.NewTransactionInfo>> SyncAddressAsync(
        FlorestaDbContext ctx,
        TrackedAddress addr,
        DerivationStrategyBase strategy,
        IReadOnlyDictionary<string, TrackedAddress> walletAddresses,
        HashSet<string> existingTxids,
        HashSet<string> observedHistoryTxids,
        CancellationToken ct,
        List<TrackedAddress> addressesToSubscribe)
    {
        var newTxs = new List<FlorestaWalletTracker.NewTransactionInfo>();
        var history = await _client.ScripthashGetHistoryAsync(addr.Scripthash, ct);
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
            var balanceChange = ComputeBalanceChange(ctx, tx, addr.WalletId);

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

            var txInfo = BuildNewTransactionInfo(tx, strategy, item, walletAddresses);
            if (txInfo != null)
                newTxs.Add(txInfo);
        }

        var utxos = await _utxoCache.GetReconciledAsync(addr, ct);
        await _utxoCache.UpdateForAddressAsync(ctx, addr, utxos, ct);

        if (history.Length > 0 && !addr.IsUsed)
        {
            addr.IsUsed = true;
            addressesToSubscribe.AddRange(await _addressPool.ExtendGapIfNeededAsync(ctx, addr, ct));
        }

        return newTxs;
    }

    private SemaphoreSlim GetWalletLock(string walletId)
    {
        return _walletLocks.GetOrAdd(walletId, _ => new SemaphoreSlim(1, 1));
    }

    private long ComputeBalanceChange(FlorestaDbContext ctx, Transaction tx, string walletId)
    {
        var ourScripts = ctx.TrackedAddresses
            .Where(a => a.WalletId == walletId)
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
        FlorestaElectrumHistoryItem historyItem,
        IReadOnlyDictionary<string, TrackedAddress> walletAddresses)
    {
        var tipHeight = _getTipHeight();
        var info = new FlorestaWalletTracker.NewTransactionInfo
        {
            TxId = tx.GetHash().ToString(),
            DerivationStrategy = strategy,
            Confirmations = FlorestaWalletTracker.GetConfirmations(historyItem.Height, tipHeight),
            IsRbf = tx.RBF,
            SeenAt = DateTimeOffset.UtcNow
        };

        for (var i = 0; i < tx.Outputs.Count; i++)
        {
            var output = tx.Outputs[i];
            var scriptHash = ScriptHashUtility.ComputeScriptHash(output.ScriptPubKey);

            if (!walletAddresses.TryGetValue(scriptHash, out var outputAddress))
                continue;

            var parts = outputAddress.KeyPath.Split('/');
            var keyIndex = parts.Length == 2 && int.TryParse(parts[1], out var idx) ? idx : 0;

            info.Outputs.Add(new FlorestaWalletTracker.OutputInfo
            {
                Address = outputAddress.Address,
                TrackedDestination = output.ScriptPubKey.Hash.ToString(),
                Index = i,
                Value = output.Value,
                KeyPath = outputAddress.KeyPath,
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
    }

    internal static int DemoteTransactionsMissingFromHistory(
        IEnumerable<TrackedTransaction> transactions,
        IReadOnlySet<string> observedHistoryTxids)
    {
        var demoted = 0;
        foreach (var transaction in transactions)
        {
            if (transaction.BlockHeight is null || observedHistoryTxids.Contains(transaction.Txid))
                continue;

            transaction.BlockHeight = null;
            demoted++;
        }

        return demoted;
    }

    private static async Task<int> DemoteTransactionsMissingFromHistoryAsync(
        FlorestaDbContext ctx,
        string walletId,
        IReadOnlySet<string> observedHistoryTxids,
        CancellationToken ct)
    {
        var confirmedTransactions = await ctx.Transactions
            .Where(t => t.WalletId == walletId && t.BlockHeight != null)
            .ToListAsync(ct);
        return DemoteTransactionsMissingFromHistory(confirmedTransactions, observedHistoryTxids);
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[] x, byte[] y) => x.SequenceEqual(y);
        public int GetHashCode(byte[] obj) => BitConverter.ToInt32(obj, 0);
    }
}
