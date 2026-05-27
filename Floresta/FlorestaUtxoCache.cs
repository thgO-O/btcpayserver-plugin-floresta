using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Floresta.Data;
using BTCPayServer.Plugins.Floresta.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace BTCPayServer.Plugins.Floresta;

internal sealed class FlorestaUtxoCache
{
    private readonly FlorestaElectrumClient _client;
    private readonly Network _network;
    private readonly ILogger _logger;

    public FlorestaUtxoCache(
        FlorestaElectrumClient client,
        Network network,
        ILogger logger)
    {
        _client = client;
        _network = network;
        _logger = logger;
    }

    public async Task<FlorestaElectrumUnspentItem[]> GetReconciledAsync(
        TrackedAddress addr,
        CancellationToken ct)
    {
        var utxos = await _client.ScripthashListUnspentAsync(addr.Scripthash, ct);
        utxos = await NormalizeAsync(addr, utxos, ct);

        try
        {
            var balance = await _client.ScripthashGetBalanceAsync(addr.Scripthash, ct);
            if (!BalanceMatchesListUnspent(balance, utxos))
            {
                _logger.LogWarning(
                    "Electrum balance mismatch for wallet {WalletId} scripthash {Scripthash}; retrying once",
                    LogSafeId.Hash(addr.WalletId),
                    addr.Scripthash);

                utxos = await _client.ScripthashListUnspentAsync(addr.Scripthash, ct);
                utxos = await NormalizeAsync(addr, utxos, ct);
                balance = await _client.ScripthashGetBalanceAsync(addr.Scripthash, ct);

                if (!BalanceMatchesListUnspent(balance, utxos))
                {
                    _logger.LogWarning(
                        "Electrum balance still mismatched for wallet {WalletId} scripthash {Scripthash}; keeping listunspent as UTXO source",
                        LogSafeId.Hash(addr.WalletId),
                        addr.Scripthash);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not reconcile Electrum balance for wallet {WalletId} scripthash {Scripthash}; keeping listunspent as UTXO source",
                LogSafeId.Hash(addr.WalletId),
                addr.Scripthash);
        }

        return utxos;
    }

    public async Task UpdateForAddressAsync(
        FlorestaDbContext ctx,
        TrackedAddress addr,
        FlorestaElectrumUnspentItem[] utxos,
        CancellationToken ct)
    {
        var currentUtxos = utxos
            .GroupBy(u => $"{u.TxHash}:{u.TxPos}")
            .Select(g => g.First())
            .ToList();
        var currentOutpoints = currentUtxos.Select(u => $"{u.TxHash}:{u.TxPos}").ToHashSet();

        var existingUtxos = await ctx.Utxos
            .Where(u => u.Scripthash == addr.Scripthash && !u.IsSpent)
            .ToListAsync(ct);

        foreach (var existing in existingUtxos)
        {
            if (!currentOutpoints.Contains(existing.Outpoint))
                existing.IsSpent = true;
        }

        var existingByOutpoint = await ctx.Utxos
            .Where(u => currentOutpoints.Contains(u.Outpoint))
            .ToDictionaryAsync(u => u.Outpoint, ct);

        foreach (var utxo in currentUtxos)
        {
            var outpoint = $"{utxo.TxHash}:{utxo.TxPos}";
            if (existingByOutpoint.TryGetValue(outpoint, out var existing))
            {
                existing.WalletId = addr.WalletId;
                existing.Scripthash = addr.Scripthash;
                existing.Txid = utxo.TxHash;
                existing.Vout = utxo.TxPos;
                existing.Value = utxo.Value;
                existing.ScriptPubKey = addr.ScriptPubKey;
                existing.KeyPath = addr.KeyPath;
                existing.BlockHeight = utxo.Height > 0 ? utxo.Height : null;
                existing.IsSpent = false;
                existing.SpendingTxid = null;
                continue;
            }

            ctx.Utxos.Add(new TrackedUtxo
            {
                Outpoint = outpoint,
                WalletId = addr.WalletId,
                Scripthash = addr.Scripthash,
                Txid = utxo.TxHash,
                Vout = utxo.TxPos,
                Value = utxo.Value,
                ScriptPubKey = addr.ScriptPubKey,
                KeyPath = addr.KeyPath,
                BlockHeight = utxo.Height > 0 ? utxo.Height : null,
                SeenAt = DateTimeOffset.UtcNow
            });
        }
    }

    private async Task<FlorestaElectrumUnspentItem[]> NormalizeAsync(
        TrackedAddress addr,
        FlorestaElectrumUnspentItem[] utxos,
        CancellationToken ct)
    {
        if (utxos.Length == 0)
            return utxos;

        var scriptPubKey = addr.ScriptPubKey;
        var normalized = new List<FlorestaElectrumUnspentItem>(utxos.Length);

        foreach (var utxo in utxos)
        {
            var normalizedUtxo = utxo;
            try
            {
                var rawHex = await _client.TransactionGetAsync(utxo.TxHash, ct);
                var tx = Transaction.Parse(rawHex, _network);
                normalizedUtxo = NormalizeUtxoPosition(utxo, tx, scriptPubKey);
                if (normalizedUtxo.TxPos != utxo.TxPos)
                {
                    _logger.LogDebug(
                        "Corrected Electrum UTXO position for wallet {WalletId}: {TxId}:{OldVout} -> {TxId}:{NewVout}",
                        LogSafeId.Hash(addr.WalletId),
                        utxo.TxHash,
                        utxo.TxPos,
                        utxo.TxHash,
                        normalizedUtxo.TxPos);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not verify Electrum UTXO position for wallet {WalletId} outpoint {TxId}:{Vout}",
                    LogSafeId.Hash(addr.WalletId),
                    utxo.TxHash,
                    utxo.TxPos);
            }

            normalized.Add(normalizedUtxo);
        }

        return normalized
            .GroupBy(u => $"{u.TxHash}:{u.TxPos}")
            .Select(g => g.First())
            .ToArray();
    }

    public static FlorestaElectrumUnspentItem NormalizeUtxoPosition(
        FlorestaElectrumUnspentItem utxo,
        Transaction tx,
        byte[] scriptPubKey)
    {
        var txPosMatches = utxo.TxPos >= 0 &&
                           utxo.TxPos < tx.Outputs.Count &&
                           tx.Outputs[utxo.TxPos].ScriptPubKey.ToBytes().SequenceEqual(scriptPubKey);

        if (txPosMatches)
        {
            var output = tx.Outputs[utxo.TxPos];
            if (output.Value.Satoshi == utxo.Value)
                return utxo;

            var sameScriptSameValueIndex = Enumerable.Range(0, tx.Outputs.Count)
                .Cast<int?>()
                .FirstOrDefault(i =>
                    i.Value != utxo.TxPos &&
                    tx.Outputs[i.Value].Value.Satoshi == utxo.Value &&
                    tx.Outputs[i.Value].ScriptPubKey.ToBytes().SequenceEqual(scriptPubKey));
            if (sameScriptSameValueIndex is int matchingIndex)
            {
                return new FlorestaElectrumUnspentItem
                {
                    TxHash = utxo.TxHash,
                    TxPos = matchingIndex,
                    Value = utxo.Value,
                    Height = utxo.Height
                };
            }

            return new FlorestaElectrumUnspentItem
            {
                TxHash = utxo.TxHash,
                TxPos = utxo.TxPos,
                Value = output.Value.Satoshi,
                Height = utxo.Height
            };
        }

        var matchingIndexes = Enumerable.Range(0, tx.Outputs.Count)
            .Where(i => tx.Outputs[i].ScriptPubKey.ToBytes().SequenceEqual(scriptPubKey))
            .ToList();
        var correctedIndex = matchingIndexes
            .Cast<int?>()
            .FirstOrDefault(i => tx.Outputs[i.Value].Value.Satoshi == utxo.Value);
        correctedIndex ??= matchingIndexes.Count == 1 ? matchingIndexes[0] : null;

        if (correctedIndex is not int index)
            return utxo;

        var correctedOutput = tx.Outputs[index];
        return new FlorestaElectrumUnspentItem
        {
            TxHash = utxo.TxHash,
            TxPos = index,
            Value = correctedOutput.Value.Satoshi,
            Height = utxo.Height
        };
    }

    private static bool BalanceMatchesListUnspent(
        FlorestaElectrumBalance balance,
        IReadOnlyCollection<FlorestaElectrumUnspentItem> utxos)
    {
        var confirmed = utxos.Where(u => u.Height > 0).Sum(u => u.Value);
        var unconfirmed = utxos.Where(u => u.Height <= 0).Sum(u => u.Value);
        return balance.Confirmed == confirmed && balance.Unconfirmed == unconfirmed;
    }
}
