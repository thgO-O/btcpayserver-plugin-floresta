using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Floresta.Data;
using BTCPayServer.Plugins.Floresta.Services;
using BTCPayServer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;

namespace BTCPayServer.Plugins.Floresta;

public class FlorestaWalletTracker
{
    private readonly FlorestaElectrumClient _client;
    private readonly FlorestaFeeProvider _feeProvider;
    private readonly FlorestaRpcClient _rpcClient;
    private readonly FlorestaDescriptorService _descriptorService;
    private readonly FlorestaDescriptorRegistry _descriptorRegistry;
    private readonly FlorestaDbContextFactory _dbFactory;
    private readonly SettingsRepository _settingsRepository;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly ILogger<FlorestaWalletTracker> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly SemaphoreSlim _migrationLock = new(1, 1);
    private bool _migrated;

    private readonly ConcurrentDictionary<string, DerivationStrategyBase> _trackedStrategies = new();
    private readonly ConcurrentDictionary<string, ScanUTXOInformation> _scanStates = new();
    private Network _network;
    private DerivationStrategyFactory _derivationFactory;
    private int _tipHeight;

    public FlorestaWalletTracker(
        FlorestaElectrumClient client,
        FlorestaFeeProvider feeProvider,
        FlorestaRpcClient rpcClient,
        FlorestaDescriptorService descriptorService,
        FlorestaDescriptorRegistry descriptorRegistry,
        FlorestaDbContextFactory dbFactory,
        SettingsRepository settingsRepository,
        BTCPayNetworkProvider networkProvider,
        ILogger<FlorestaWalletTracker> logger)
    {
        _client = client;
        _feeProvider = feeProvider;
        _rpcClient = rpcClient;
        _descriptorService = descriptorService;
        _descriptorRegistry = descriptorRegistry;
        _dbFactory = dbFactory;
        _settingsRepository = settingsRepository;
        _networkProvider = networkProvider;
        _logger = logger;
        ConfigureNetwork();
    }

    private void ConfigureNetwork()
    {
        var settings = new FlorestaSettings();
        var btcNetwork = _networkProvider.GetNetwork<BTCPayNetwork>(settings.CryptoCode ?? "BTC");
        if (btcNetwork == null)
            return;

        _network = btcNetwork.NBitcoinNetwork;
        _derivationFactory = new DerivationStrategyFactory(_network);
    }

    /// <summary>
    /// Ensures DB migrations have run. Safe to call multiple times — only migrates once.
    /// Does NOT require Electrum connection.
    /// </summary>
    private async Task EnsureMigratedAsync(CancellationToken ct)
    {
        if (_migrated) return;
        await _migrationLock.WaitAsync(ct);
        try
        {
            if (_migrated) return;
            await using var ctx = _dbFactory.CreateContext();
            await ctx.Database.MigrateAsync(ct);
            _migrated = true;
            _logger.LogInformation("Floresta plugin DB schema migrated");
        }
        finally
        {
            _migrationLock.Release();
        }
    }

    public void SetTipHeight(int height)
    {
        _tipHeight = height;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await EnsureMigratedAsync(ct);

        // Load wallets and their addresses from DB (fast, no Electrum calls)
        List<(string walletId, List<TrackedAddress> addresses)> walletsToSubscribe;
        await _lock.WaitAsync(ct);
        try
        {
            await using var ctx = _dbFactory.CreateContext();

            var tipState = await ctx.SyncStates.FindAsync(new object[] { "tip_height" }, ct);
            if (tipState != null && int.TryParse(tipState.Value, out var savedTip))
                _tipHeight = savedTip;

            var wallets = await ctx.TrackedWallets.ToListAsync(ct);
            walletsToSubscribe = new();

            foreach (var wallet in wallets)
            {
                var strategy = ParseStrategy(wallet.DerivationStrategy);
                if (strategy == null) continue;
                _trackedStrategies[wallet.Id] = strategy;

                var addresses = await ctx.TrackedAddresses
                    .Where(a => a.WalletId == wallet.Id)
                    .ToListAsync(ct);
                walletsToSubscribe.Add((wallet.Id, addresses));
            }

            _logger.LogInformation("Loaded {Count} tracked wallets from DB", wallets.Count);
        }
        finally
        {
            _lock.Release();
        }

        // Subscribe to Electrum + sync state OUTSIDE the lock — these are slow network calls
        foreach (var (walletId, addresses) in walletsToSubscribe)
        {
            foreach (var addr in addresses)
            {
                try
                {
                    await _client.ScripthashSubscribeAsync(addr.Scripthash, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to subscribe scripthash {Scripthash}", addr.Scripthash);
                }
            }

            try
            {
                await SyncWalletStateAsync(walletId, addresses, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to sync wallet state for wallet {WalletId}", LogSafeId.Hash(walletId));
            }
        }

        _logger.LogInformation("Initialized {Count} tracked wallets", walletsToSubscribe.Count);
    }

    public async Task TrackWalletAsync(string strategyStr, CancellationToken ct)
    {
        var settings = await GetSettingsAsync();
        var descriptorRegistration = await PrepareDescriptorRegistrationAsync(strategyStr, settings, ct);
        if (!descriptorRegistration.Succeeded)
        {
            await UpsertWalletDescriptorMetadataAsync(strategyStr, settings, descriptorRegistration, null,
                descriptorRegistration.Error, ct);
            throw new InvalidOperationException($"Floresta descriptor registration failed: {descriptorRegistration.Error}");
        }

        var descriptorRegisteredAt = settings.AutoRegisterDescriptors ? DateTimeOffset.UtcNow : (DateTimeOffset?)null;
        await StartDescriptorRescanIfNeededAsync(settings, descriptorRegistration, ct);

        // Phase 1: derive addresses locally (fast, no network)
        var addresses = await EnsureAddressesDerivedAsync(
            strategyStr,
            settings,
            descriptorRegistration,
            descriptorRegisteredAt,
            null,
            ct);
        if (addresses == null)
            addresses = await GetTrackedAddressesAsync(strategyStr, ct);

        // Phase 2: subscribe on Electrum + sync state (slow, needs network)
        await _lock.WaitAsync(ct);
        try
        {
            foreach (var addr in addresses)
            {
                await _client.ScripthashSubscribeAsync(addr.Scripthash, ct);
            }

            await SyncWalletStateAsync(strategyStr, addresses, ct);
            _logger.LogInformation("Now tracking wallet {WalletId}", LogSafeId.Hash(strategyStr));
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<TrackedAddress>> GetTrackedAddressesAsync(string strategyStr, CancellationToken ct)
    {
        await EnsureMigratedAsync(ct);
        await using var ctx = _dbFactory.CreateContext();
        return await ctx.TrackedAddresses
            .Where(a => a.WalletId == strategyStr)
            .ToListAsync(ct);
    }

    private async Task<FlorestaDescriptorRegistrationResult> PrepareDescriptorRegistrationAsync(
        string strategyStr,
        FlorestaSettings settings,
        CancellationToken ct)
    {
        if (settings.AutoRegisterDescriptors)
            return await _descriptorRegistry.RegisterAsync(settings.CryptoCode ?? "BTC", strategyStr, ct);

        return new FlorestaDescriptorRegistrationResult(
            _descriptorService.CreateDescriptors(settings.CryptoCode ?? "BTC", strategyStr),
            0,
            0,
            null);
    }

    private async Task StartDescriptorRescanIfNeededAsync(
        FlorestaSettings settings,
        FlorestaDescriptorRegistrationResult descriptorRegistration,
        CancellationToken ct)
    {
        if (!settings.AutoRegisterDescriptors ||
            descriptorRegistration.Registered == 0 ||
            !settings.AutoRescanOnNewDescriptor)
            return;

        await _rpcClient.RescanBlockchainAsync(
            settings.DefaultRescanStartHeight,
            null,
            false,
            "medium",
            ct);
        _logger.LogInformation(
            "Started Floresta rescan for descriptor {DescriptorHash} from height {Height}",
            descriptorRegistration.Descriptors.DescriptorHash,
            settings.DefaultRescanStartHeight);
    }

    /// <summary>
    /// Fast, local-only address derivation. Creates the wallet and derives
    /// gap-limit addresses in the DB without any Electrum network calls.
    /// Returns all currently tracked wallet addresses after filling missing ranges.
    /// </summary>
    private async Task<List<TrackedAddress>> EnsureAddressesDerivedAsync(
        string strategyStr,
        FlorestaSettings settings,
        FlorestaDescriptorRegistrationResult descriptorRegistration,
        DateTimeOffset? descriptorRegisteredAt,
        string descriptorRegistrationError,
        CancellationToken ct)
    {
        var strategy = ParseStrategy(strategyStr);
        if (strategy == null)
            throw new InvalidOperationException("Cannot parse derivation strategy.");

        await EnsureMigratedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            await using var ctx = _dbFactory.CreateContext();

            var existing = await ctx.TrackedWallets.FindAsync(new object[] { strategyStr }, ct);
            if (existing != null)
            {
                ApplyDescriptorMetadata(existing, descriptorRegistration, descriptorRegisteredAt, descriptorRegistrationError);

                existing.ReceiveGapIndex = Math.Max(existing.ReceiveGapIndex, settings.GapLimit - 1);
                existing.ChangeGapIndex = Math.Max(existing.ChangeGapIndex, settings.GapLimit - 1);

                var existingHashes = await ctx.TrackedAddresses
                    .Where(a => a.WalletId == strategyStr)
                    .Select(a => a.Scripthash)
                    .ToHashSetAsync(ct);

                var addressesToAdd = DeriveAddresses(strategy, false, 0, existing.ReceiveGapIndex + 1)
                    .Concat(DeriveAddresses(strategy, true, 0, existing.ChangeGapIndex + 1));
                foreach (var addr in addressesToAdd)
                {
                    if (existingHashes.Contains(addr.Scripthash))
                        continue;

                    addr.WalletId = strategyStr;
                    ctx.TrackedAddresses.Add(addr);
                    existingHashes.Add(addr.Scripthash);
                }

                await ctx.SaveChangesAsync(ct);
                _trackedStrategies[strategyStr] = strategy;
                return await ctx.TrackedAddresses
                    .Where(a => a.WalletId == strategyStr)
                    .ToListAsync(ct);
            }

            var wallet = new TrackedWallet
            {
                Id = strategyStr,
                CryptoCode = settings.CryptoCode ?? "BTC",
                DerivationStrategy = strategyStr,
                ReceiveGapIndex = settings.GapLimit - 1,
                ChangeGapIndex = settings.GapLimit - 1
            };
            ApplyDescriptorMetadata(wallet, descriptorRegistration, descriptorRegisteredAt, descriptorRegistrationError);

            ctx.TrackedWallets.Add(wallet);

            var addresses = DeriveAddresses(strategy, false, 0, settings.GapLimit);
            addresses.AddRange(DeriveAddresses(strategy, true, 0, settings.GapLimit));

            foreach (var addr in addresses)
            {
                addr.WalletId = strategyStr;
                ctx.TrackedAddresses.Add(addr);
            }

            await ctx.SaveChangesAsync(ct);
            _trackedStrategies[strategyStr] = strategy;

            return addresses;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task UpsertWalletDescriptorMetadataAsync(
        string strategyStr,
        FlorestaSettings settings,
        FlorestaDescriptorRegistrationResult descriptorRegistration,
        DateTimeOffset? descriptorRegisteredAt,
        string descriptorRegistrationError,
        CancellationToken ct)
    {
        await EnsureMigratedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            await using var ctx = _dbFactory.CreateContext();
            var wallet = await ctx.TrackedWallets.FindAsync(new object[] { strategyStr }, ct);
            if (wallet == null)
            {
                wallet = new TrackedWallet
                {
                    Id = strategyStr,
                    CryptoCode = settings.CryptoCode ?? "BTC",
                    DerivationStrategy = strategyStr,
                    ReceiveGapIndex = settings.GapLimit - 1,
                    ChangeGapIndex = settings.GapLimit - 1
                };
                ctx.TrackedWallets.Add(wallet);
            }

            ApplyDescriptorMetadata(wallet, descriptorRegistration, descriptorRegisteredAt, descriptorRegistrationError);
            await ctx.SaveChangesAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static void ApplyDescriptorMetadata(
        TrackedWallet wallet,
        FlorestaDescriptorRegistrationResult descriptorRegistration,
        DateTimeOffset? descriptorRegisteredAt,
        string descriptorRegistrationError)
    {
        wallet.DescriptorHash = descriptorRegistration.Descriptors.DescriptorHash;
        wallet.ReceiveDescriptor = descriptorRegistration.Descriptors.ReceiveDescriptor;
        wallet.ChangeDescriptor = descriptorRegistration.Descriptors.ChangeDescriptor;
        if (descriptorRegisteredAt is not null)
            wallet.DescriptorRegisteredAt = descriptorRegisteredAt;
        wallet.DescriptorRegistrationError = descriptorRegistrationError;
    }

    public async Task<List<NewTransactionInfo>> HandleScripthashNotificationAsync(
        string scripthash, string status, CancellationToken ct)
    {
        await EnsureMigratedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            await using var ctx = _dbFactory.CreateContext();

            var addr = await ctx.TrackedAddresses.FindAsync(new object[] { scripthash }, ct);
            if (addr == null) return new List<NewTransactionInfo>();

            var existingTxids = await ctx.Transactions
                .Where(t => t.WalletId == addr.WalletId)
                .Select(t => t.Txid)
                .ToHashSetAsync(ct);

            if (!_trackedStrategies.TryGetValue(addr.WalletId, out var strategy))
                return new List<NewTransactionInfo>();

            var walletAddresses = await ctx.TrackedAddresses
                .Where(a => a.WalletId == addr.WalletId)
                .ToDictionaryAsync(a => a.Scripthash, ct);
            var newTxs = await SyncAddressAsync(ctx, addr, strategy, walletAddresses, existingTxids, ct);

            await ctx.SaveChangesAsync(ct);
            return newTxs;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<NewTransactionInfo>> HandleNewBlockAsync(int height, CancellationToken ct)
    {
        var newTxs = new List<NewTransactionInfo>();
        _tipHeight = height;

        await EnsureMigratedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            await using var ctx = _dbFactory.CreateContext();

            // Update sync state
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

            // Re-check unconfirmed transactions
            var unconfirmed = await ctx.Transactions
                .Where(t => t.BlockHeight == null || t.BlockHeight == 0)
                .ToListAsync(ct);

            var addressesByWallet = await ctx.TrackedAddresses
                .ToListAsync(ct);

            foreach (var tx in unconfirmed)
            {
                var addresses = addressesByWallet
                    .Where(a => a.WalletId == tx.WalletId);

                foreach (var addr in addresses)
                {
                    var history = await _client.ScripthashGetHistoryAsync(addr.Scripthash, ct);
                    var match = history.FirstOrDefault(h => h.TxHash == tx.Txid);
                    if (match != null && match.Height > 0)
                    {
                        tx.BlockHeight = match.Height;
                        break;
                    }
                }
            }

            // Update UTXO confirmations
            var unconfirmedUtxos = await ctx.Utxos
                .Where(u => u.BlockHeight == null || u.BlockHeight == 0)
                .ToListAsync(ct);

            foreach (var utxo in unconfirmedUtxos)
            {
                var addr = await ctx.TrackedAddresses.FindAsync(new object[] { utxo.Scripthash }, ct);
                if (addr == null) continue;

                var utxoList = await _client.ScripthashListUnspentAsync(addr.Scripthash, ct);
                var match = utxoList.FirstOrDefault(u =>
                    u.TxHash == utxo.Txid && u.TxPos == utxo.Vout);

                if (match != null && match.Height > 0)
                {
                    utxo.BlockHeight = match.Height;
                }
            }

            foreach (var walletGroup in addressesByWallet.GroupBy(a => a.WalletId))
            {
                if (!_trackedStrategies.TryGetValue(walletGroup.Key, out var strategy))
                    continue;

                var walletAddresses = walletGroup.ToDictionary(a => a.Scripthash);
                var existingTxids = await ctx.Transactions
                    .Where(t => t.WalletId == walletGroup.Key)
                    .Select(t => t.Txid)
                    .ToHashSetAsync(ct);

                foreach (var addr in walletGroup)
                {
                    newTxs.AddRange(await SyncAddressAsync(ctx, addr, strategy, walletAddresses, existingTxids, ct));
                }
            }

            await ctx.SaveChangesAsync(ct);
        }
        finally
        {
            _lock.Release();
        }

        return newTxs;
    }

    // ─────────────────────────────────────────────
    // Methods called by FlorestaHttpHandler
    // ─────────────────────────────────────────────

    public async Task<KeyPathInformation> GetNextUnusedAddressAsync(
        string strategyStr, bool isChange, bool reserve, CancellationToken ct)
    {
        await EnsureMigratedAsync(ct);
        await using var ctx = _dbFactory.CreateContext();

        var addr = await ctx.TrackedAddresses
            .Where(a => a.WalletId == strategyStr && a.IsChange == isChange && !a.IsUsed)
            .OrderBy(a => a.KeyPath)
            .FirstOrDefaultAsync(ct);

        if (addr == null)
        {
            // Derive addresses locally (fast, no Electrum calls).
            // Background TrackWalletAsync will subscribe them later.
            var settings = await GetSettingsAsync();
            var descriptorRegistration = new FlorestaDescriptorRegistrationResult(
                _descriptorService.CreateDescriptors(settings.CryptoCode ?? "BTC", strategyStr),
                0,
                0,
                null);
            await EnsureAddressesDerivedAsync(strategyStr, settings, descriptorRegistration, null, null, ct);
            addr = await ctx.TrackedAddresses
                .Where(a => a.WalletId == strategyStr && a.IsChange == isChange && !a.IsUsed)
                .OrderBy(a => a.KeyPath)
                .FirstOrDefaultAsync(ct);
        }

        if (addr == null) return null;

        if (reserve)
        {
            addr.IsUsed = true;
            await ctx.SaveChangesAsync(ct);
        }

        var script = Script.FromBytesUnsafe(addr.ScriptPubKey);
        var address = script.GetDestinationAddress(_network);

        return new KeyPathInformation
        {
            Address = address,
            ScriptPubKey = script,
            KeyPath = KeyPath.Parse(addr.KeyPath),
            Feature = isChange ? DerivationFeature.Change : DerivationFeature.Deposit,
            TrackedSource = TrackedSource.Create(ParseStrategy(strategyStr))
        };
    }

    public async Task<UTXOChanges> GetUTXOChangesAsync(string strategyStr, CancellationToken ct)
    {
        await EnsureMigratedAsync(ct);
        await using var ctx = _dbFactory.CreateContext();

        var utxos = await ctx.Utxos
            .Where(u => u.WalletId == strategyStr && !u.IsSpent)
            .ToListAsync(ct);

        var confirmed = new List<UTXO>();
        var unconfirmed = new List<UTXO>();

        foreach (var u in utxos)
        {
            var utxo = new UTXO
            {
                Outpoint = OutPoint.Parse(u.Outpoint),
                Value = Money.Satoshis(u.Value),
                ScriptPubKey = Script.FromBytesUnsafe(u.ScriptPubKey),
                KeyPath = KeyPath.Parse(u.KeyPath),
                Timestamp = u.SeenAt,
                Confirmations = u.BlockHeight.HasValue && _tipHeight > 0
                    ? _tipHeight - (int)u.BlockHeight.Value + 1
                    : 0
            };

            if (u.BlockHeight.HasValue && u.BlockHeight > 0)
                confirmed.Add(utxo);
            else
                unconfirmed.Add(utxo);
        }

        var result = new UTXOChanges
        {
            CurrentHeight = _tipHeight,
            Confirmed = new UTXOChange { UTXOs = confirmed },
            Unconfirmed = new UTXOChange { UTXOs = unconfirmed }
        };

        return result;
    }

    public async Task<GetBalanceResponse> GetBalanceAsync(string strategyStr, CancellationToken ct)
    {
        await EnsureMigratedAsync(ct);
        await using var ctx = _dbFactory.CreateContext();

        var utxos = await ctx.Utxos
            .Where(u => u.WalletId == strategyStr && !u.IsSpent)
            .ToListAsync(ct);

        var confirmed = utxos.Where(u => u.BlockHeight.HasValue && u.BlockHeight > 0).Sum(u => u.Value);
        var unconfirmed = utxos.Where(u => !u.BlockHeight.HasValue || u.BlockHeight <= 0).Sum(u => u.Value);

        return new GetBalanceResponse
        {
            Confirmed = Money.Satoshis(confirmed),
            Unconfirmed = Money.Satoshis(unconfirmed),
            Available = Money.Satoshis(confirmed + unconfirmed),
            Total = Money.Satoshis(confirmed + unconfirmed)
        };
    }

    public async Task WipeAsync(string strategyStr, CancellationToken ct)
    {
        await EnsureMigratedAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            await using var ctx = _dbFactory.CreateContext();
            var utxos = await ctx.Utxos
                .Where(u => u.WalletId == strategyStr)
                .ToListAsync(ct);
            var transactions = await ctx.Transactions
                .Where(t => t.WalletId == strategyStr)
                .ToListAsync(ct);

            ctx.Utxos.RemoveRange(utxos);
            ctx.Transactions.RemoveRange(transactions);
            await ctx.SaveChangesAsync(ct);
            _logger.LogInformation(
                "Wiped Floresta wallet cache for wallet {WalletId}: {UtxoCount} UTXOs, {TxCount} transactions",
                LogSafeId.Hash(strategyStr),
                utxos.Count,
                transactions.Count);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<TransactionResult> GetTransactionResultAsync(string txId, CancellationToken ct)
    {
        await EnsureMigratedAsync(ct);
        await using var ctx = _dbFactory.CreateContext();

        var tx = await ctx.Transactions
            .FirstOrDefaultAsync(t => t.Txid == txId, ct);

        if (tx == null)
        {
            // Try fetching from Electrum directly
            try
            {
                var rawHex = await _client.TransactionGetAsync(txId, ct);
                var parsed = Transaction.Parse(rawHex, _network);
                return new TransactionResult
                {
                    TransactionHash = parsed.GetHash(),
                    Transaction = parsed,
                    Confirmations = 0,
                    Timestamp = DateTimeOffset.UtcNow
                };
            }
            catch
            {
                return null;
            }
        }

        var transaction = tx.RawTx != null ? Transaction.Load(tx.RawTx, _network) : null;
        var confirmations = tx.BlockHeight.HasValue && tx.BlockHeight > 0 && _tipHeight > 0
            ? _tipHeight - (int)tx.BlockHeight.Value + 1
            : 0;

        return new TransactionResult
        {
            TransactionHash = uint256.Parse(tx.Txid),
            Transaction = transaction,
            Confirmations = confirmations,
            Height = tx.BlockHeight.HasValue ? (int)tx.BlockHeight.Value : 0,
            Timestamp = tx.SeenAt
        };
    }

    public async Task<TransactionInformation> GetTransactionInfoAsync(
        string strategyStr, string txId, CancellationToken ct)
    {
        await EnsureMigratedAsync(ct);
        await using var ctx = _dbFactory.CreateContext();

        var tx = await ctx.Transactions
            .FirstOrDefaultAsync(t => t.Txid == txId && t.WalletId == strategyStr, ct);

        if (tx == null) return null;

        var transaction = tx.RawTx != null ? Transaction.Load(tx.RawTx, _network) : null;
        var confirmations = tx.BlockHeight.HasValue && tx.BlockHeight > 0 && _tipHeight > 0
            ? _tipHeight - (int)tx.BlockHeight.Value + 1
            : 0;

        return new TransactionInformation
        {
            TransactionId = uint256.Parse(tx.Txid),
            Transaction = transaction,
            Confirmations = confirmations,
            Height = tx.BlockHeight.HasValue ? (int)tx.BlockHeight.Value : 0,
            Timestamp = tx.SeenAt,
            BalanceChange = Money.Satoshis(tx.BalanceChange)
        };
    }

    public async Task<GetTransactionsResponse> GetTransactionsResponseAsync(
        string strategyStr, CancellationToken ct)
    {
        await EnsureMigratedAsync(ct);
        await using var ctx = _dbFactory.CreateContext();

        var txs = await ctx.Transactions
            .Where(t => t.WalletId == strategyStr)
            .OrderByDescending(t => t.SeenAt)
            .ToListAsync(ct);

        var confirmed = new List<TransactionInformation>();
        var unconfirmed = new List<TransactionInformation>();

        foreach (var tx in txs)
        {
            var transaction = tx.RawTx != null ? Transaction.Load(tx.RawTx, _network) : null;
            var confirmations = tx.BlockHeight.HasValue && tx.BlockHeight > 0 && _tipHeight > 0
                ? _tipHeight - (int)tx.BlockHeight.Value + 1
                : 0;

            var info = new TransactionInformation
            {
                TransactionId = uint256.Parse(tx.Txid),
                Transaction = transaction,
                Confirmations = confirmations,
                Height = tx.BlockHeight.HasValue ? (int)tx.BlockHeight.Value : 0,
                Timestamp = tx.SeenAt,
                BalanceChange = Money.Satoshis(tx.BalanceChange)
            };

            if (tx.BlockHeight.HasValue && tx.BlockHeight > 0)
                confirmed.Add(info);
            else
                unconfirmed.Add(info);
        }

        return new GetTransactionsResponse
        {
            Height = _tipHeight,
            ConfirmedTransactions = new TransactionInformationSet { Transactions = confirmed },
            UnconfirmedTransactions = new TransactionInformationSet { Transactions = unconfirmed },
            ReplacedTransactions = new TransactionInformationSet { Transactions = new List<TransactionInformation>() }
        };
    }

    public async Task<BroadcastResult> BroadcastAsync(string body, CancellationToken ct)
    {
        return await BroadcastAsync(Encoding.UTF8.GetBytes(body), ct);
    }

    public async Task<BroadcastResult> BroadcastAsync(byte[] body, CancellationToken ct)
    {
        if (!TryExtractRawTransactionHex(body, out var rawTx, out var parseError))
        {
            return new BroadcastResult(false)
            {
                RPCMessage = parseError
            };
        }

        try
        {
            await _client.TransactionBroadcastAsync(rawTx, ct);
            return new BroadcastResult(true);
        }
        catch (FlorestaElectrumException ex)
        {
            try
            {
                await _rpcClient.SendRawTransactionAsync(rawTx, ct);
                return new BroadcastResult(true);
            }
            catch (Exception rpcEx)
            {
                return new BroadcastResult(false)
                {
                    RPCMessage = $"Electrum broadcast failed: {ex.Message}; RPC broadcast failed: {rpcEx.Message}"
                };
            }
        }
        catch (FlorestaRpcException ex)
        {
            return new BroadcastResult(false)
            {
                RPCMessage = ex.Message
            };
        }
    }

    internal static bool TryExtractRawTransactionHex(string body, out string rawTx, out string error)
    {
        return TryExtractRawTransactionHex(Encoding.UTF8.GetBytes(body), out rawTx, out error);
    }

    internal static bool TryExtractRawTransactionHex(byte[] body, out string rawTx, out string error)
    {
        rawTx = string.Empty;
        error = string.Empty;

        if (body.Length == 0)
        {
            error = "Broadcast request body is empty.";
            return false;
        }

        var bodyText = Encoding.UTF8.GetString(body);
        var trimmed = bodyText.Trim();
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (TryExtractRawTransactionHex(doc.RootElement, out rawTx))
                return true;
        }
        catch (JsonException)
        {
            if (TryNormalizeRawTransactionHex(trimmed.Trim('"'), out rawTx))
                return true;
        }

        if (!LooksLikeText(body))
        {
            rawTx = Convert.ToHexString(body).ToLowerInvariant();
            return true;
        }

        error = "Broadcast request did not contain raw transaction hex.";
        return false;
    }

    private static bool TryExtractRawTransactionHex(JsonElement element, out string rawTx)
    {
        rawTx = string.Empty;

        if (element.ValueKind == JsonValueKind.String)
            return TryNormalizeRawTransactionHex(element.GetString(), out rawTx);

        if (element.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var propertyName in new[] { "transaction", "hex", "rawTransaction", "rawTx" })
        {
            if (TryGetPropertyIgnoreCase(element, propertyName, out var property) &&
                TryExtractRawTransactionHex(property, out rawTx))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement property)
    {
        foreach (var candidate in element.EnumerateObject())
        {
            if (string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                property = candidate.Value;
                return true;
            }
        }

        property = default;
        return false;
    }

    private static bool LooksLikeText(byte[] bytes)
    {
        foreach (var b in bytes)
        {
            if (b is 9 or 10 or 13)
                continue;
            if (b < 32)
                return false;
        }

        return true;
    }

    private static bool TryNormalizeRawTransactionHex(string value, out string rawTx)
    {
        rawTx = (value ?? string.Empty).Trim();
        if (rawTx.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            rawTx = rawTx[2..];

        return rawTx.Length > 0 &&
               rawTx.Length % 2 == 0 &&
               rawTx.All(Uri.IsHexDigit);
    }

    public async Task<GetFeeRateResult> GetFeeRateAsync(int blockTarget, CancellationToken ct)
    {
        return new GetFeeRateResult
        {
            FeeRate = await _feeProvider.GetFeeRateAsync(blockTarget),
            BlockCount = blockTarget
        };
    }

    public StatusResult GetStatus()
    {
        return new StatusResult
        {
            IsFullySynched = _client.IsConnected,
            ChainHeight = _tipHeight,
            SyncHeight = _tipHeight,
            Version = $"floresta-plugin-{typeof(FlorestaPlugin).Assembly.GetName().Version?.ToString() ?? "unknown"}",
            SupportedCryptoCodes = new[] { "BTC" },
            NetworkType = _network?.ChainName ?? ChainName.Mainnet,
            BitcoinStatus = new BitcoinStatus
            {
                Blocks = _tipHeight,
                Headers = _tipHeight,
                VerificationProgress = 1.0,
                IsSynched = _client.IsConnected,
                MinRelayTxFee = new FeeRate(1.0m),
                IncrementalRelayFee = new FeeRate(1.0m),
                Capabilities = new NodeCapabilities
                {
                    CanScanTxoutSet = true,
                    CanSupportSegwit = true,
                    CanSupportTaproot = true,
                    CanSupportTransactionCheck = true
                }
            }
        };
    }

    // ─────────────────────────────────────────────
    // UTXO Set Scan (replaces Bitcoin Core's scantxoutset)
    // ─────────────────────────────────────────────

    public ScanUTXOInformation GetScanState(string strategyStr)
    {
        _scanStates.TryGetValue(strategyStr, out var state);
        return state;
    }

    public void StartScan(string strategyStr, int gapLimit, int startingIndex, int batchSize)
    {
        var info = new ScanUTXOInformation
        {
            Status = ScanUTXOStatus.Queued,
            QueuedAt = DateTimeOffset.UtcNow,
            Progress = new ScanUTXOProgress
            {
                StartedAt = DateTimeOffset.UtcNow,
                From = startingIndex,
                Count = gapLimit * 2,
                OverallProgress = 0
            }
        };
        _scanStates[strategyStr] = info;

        _ = Task.Run(async () =>
        {
            try
            {
                info.Status = ScanUTXOStatus.Pending;
                info.Progress.StartedAt = DateTimeOffset.UtcNow;
                await RescanWalletAsync(strategyStr, gapLimit, startingIndex, info, CancellationToken.None);
                info.Status = ScanUTXOStatus.Complete;
                info.Progress.CompletedAt = DateTimeOffset.UtcNow;
                info.Progress.OverallProgress = 100;
            }
            catch (Exception ex)
            {
                info.Status = ScanUTXOStatus.Error;
                info.Error = ex.Message;
                _logger.LogError(ex, "Scan failed for wallet {WalletId}", LogSafeId.Hash(strategyStr));
            }
        });
    }

    private async Task RescanWalletAsync(
        string strategyStr, int gapLimit, int startingIndex,
        ScanUTXOInformation scanInfo, CancellationToken ct)
    {
        await EnsureMigratedAsync(ct);

        var strategy = ParseStrategy(strategyStr);
        if (strategy == null)
            throw new InvalidOperationException("Cannot parse derivation strategy.");
        var settings = await GetSettingsAsync();
        var descriptorRegistration = await PrepareDescriptorRegistrationAsync(strategyStr, settings, ct);
        if (!descriptorRegistration.Succeeded)
        {
            await UpsertWalletDescriptorMetadataAsync(strategyStr, settings, descriptorRegistration, null,
                descriptorRegistration.Error, ct);
            throw new InvalidOperationException($"Floresta descriptor registration failed: {descriptorRegistration.Error}");
        }

        var descriptorRegisteredAt = settings.AutoRegisterDescriptors ? DateTimeOffset.UtcNow : (DateTimeOffset?)null;
        await StartDescriptorRescanIfNeededAsync(settings, descriptorRegistration, ct);

        await _lock.WaitAsync(ct);
        List<TrackedAddress> newAddresses;
        try
        {
            await using var ctx = _dbFactory.CreateContext();

            var wallet = await ctx.TrackedWallets.FindAsync(new object[] { strategyStr }, ct);
            if (wallet == null)
            {
                wallet = new TrackedWallet
                {
                    Id = strategyStr,
                    CryptoCode = settings.CryptoCode ?? "BTC",
                    DerivationStrategy = strategyStr,
                    ReceiveGapIndex = startingIndex + gapLimit - 1,
                    ChangeGapIndex = startingIndex + gapLimit - 1
                };
                ApplyDescriptorMetadata(wallet, descriptorRegistration, descriptorRegisteredAt, null);
                ctx.TrackedWallets.Add(wallet);
            }
            else
            {
                wallet.ReceiveGapIndex = Math.Max(wallet.ReceiveGapIndex, startingIndex + gapLimit - 1);
                wallet.ChangeGapIndex = Math.Max(wallet.ChangeGapIndex, startingIndex + gapLimit - 1);
                ApplyDescriptorMetadata(wallet, descriptorRegistration, descriptorRegisteredAt, null);
            }

            var existingHashes = await ctx.TrackedAddresses
                .Where(a => a.WalletId == strategyStr)
                .Select(a => a.Scripthash)
                .ToHashSetAsync(ct);

            var receiveAddrs = DeriveAddresses(strategy, false, startingIndex, gapLimit);
            var changeAddrs = DeriveAddresses(strategy, true, startingIndex, gapLimit);
            newAddresses = new List<TrackedAddress>();

            foreach (var addr in receiveAddrs.Concat(changeAddrs))
            {
                if (!existingHashes.Contains(addr.Scripthash))
                {
                    addr.WalletId = strategyStr;
                    ctx.TrackedAddresses.Add(addr);
                    newAddresses.Add(addr);
                }
            }

            await ctx.SaveChangesAsync(ct);
            _trackedStrategies[strategyStr] = strategy;
        }
        finally
        {
            _lock.Release();
        }

        foreach (var addr in newAddresses)
        {
            try
            {
                if (_client.IsConnected)
                    await _client.ScripthashSubscribeAsync(addr.Scripthash, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to subscribe {Scripthash} during scan", addr.Scripthash);
            }
        }

        var allAddresses = new List<TrackedAddress>();
        await using (var ctx = _dbFactory.CreateContext())
        {
            allAddresses = await ctx.TrackedAddresses
                .Where(a => a.WalletId == strategyStr)
                .ToListAsync(ct);
        }

        scanInfo.Progress.Count = allAddresses.Count;

        if (_client.IsConnected)
        {
            await SyncWalletStateAsync(
                strategyStr,
                allAddresses,
                ct,
                (processed, total) =>
                {
                    scanInfo.Progress.Count = total;
                    scanInfo.Progress.TotalSearched = processed;
                    scanInfo.Progress.OverallProgress = total == 0 ? 100 : processed * 100 / total;
                });
        }

        await using (var ctx = _dbFactory.CreateContext())
        {
            scanInfo.Progress.Found = await ctx.Utxos
                .CountAsync(u => u.WalletId == strategyStr && !u.IsSpent, ct);
            if (scanInfo.Progress.TotalSearched == 0 && allAddresses.Count == 0)
            {
                scanInfo.Progress.OverallProgress = 100;
            }
        }
    }

    // ─────────────────────────────────────────────
    // Metadata storage (replaces NBXplorer metadata)
    // ─────────────────────────────────────────────

    public async Task SetMetadataAsync(string derivationScheme, string key, string value, CancellationToken ct)
    {
        await EnsureMigratedAsync(ct);
        await using var ctx = _dbFactory.CreateContext();
        var metaKey = $"metadata:{derivationScheme}:{key}";
        var existing = await ctx.SyncStates.FindAsync(new object[] { metaKey }, ct);
        if (existing != null)
        {
            existing.Value = value;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            ctx.SyncStates.Add(new Data.SyncState
            {
                Key = metaKey,
                Value = value,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<string> GetMetadataAsync(string derivationScheme, string key, CancellationToken ct)
    {
        await EnsureMigratedAsync(ct);
        await using var ctx = _dbFactory.CreateContext();
        var metaKey = $"metadata:{derivationScheme}:{key}";
        var state = await ctx.SyncStates.FindAsync(new object[] { metaKey }, ct);
        return state?.Value;
    }

    // ─────────────────────────────────────────────
    // Private helpers
    // ─────────────────────────────────────────────

    private DerivationStrategyBase ParseStrategy(string strategyStr)
    {
        if (_derivationFactory == null) return null;
        try
        {
            return _derivationFactory.Parse(strategyStr);
        }
        catch
        {
            _logger.LogWarning("Failed to parse derivation strategy for wallet {WalletId}", LogSafeId.Hash(strategyStr));
            return null;
        }
    }

    private List<TrackedAddress> DeriveAddresses(
        DerivationStrategyBase strategy, bool isChange, int fromIndex, int count)
    {
        var result = new List<TrackedAddress>();
        var feature = isChange ? DerivationFeature.Change : DerivationFeature.Deposit;
        var line = strategy.GetLineFor(feature);

        for (var i = fromIndex; i < fromIndex + count; i++)
        {
            var derivation = line.Derive((uint)i);
            var script = derivation.ScriptPubKey;
            var scripthash = ScriptHashUtility.ComputeScriptHash(script);
            var address = script.GetDestinationAddress(_network);

            var chainIndex = isChange ? 1 : 0;
            result.Add(new TrackedAddress
            {
                Scripthash = scripthash,
                KeyPath = $"{chainIndex}/{i}",
                ScriptPubKey = script.ToBytes(),
                Address = address?.ToString() ?? "",
                IsChange = isChange,
                IsUsed = false
            });
        }

        return result;
    }

    private async Task SyncWalletStateAsync(
        string walletId,
        List<TrackedAddress> addresses,
        CancellationToken ct,
        Action<int, int> reportProgress = null)
    {
        await using var ctx = _dbFactory.CreateContext();
        var existingTxids = await ctx.Transactions
            .Where(t => t.WalletId == walletId)
            .Select(t => t.Txid)
            .ToHashSetAsync(ct);

        if (!_trackedStrategies.TryGetValue(walletId, out var strategy))
            return;

        var walletAddresses = await ctx.TrackedAddresses
            .Where(a => a.WalletId == walletId)
            .ToDictionaryAsync(a => a.Scripthash, ct);

        var processed = 0;
        foreach (var addr in addresses)
        {
            try
            {
                if (walletAddresses.TryGetValue(addr.Scripthash, out var trackedAddress))
                    await SyncAddressAsync(ctx, trackedAddress, strategy, walletAddresses, existingTxids, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error syncing address {Address} ({Scripthash})",
                    addr.Address, addr.Scripthash);
            }
            finally
            {
                processed++;
                reportProgress?.Invoke(processed, addresses.Count);
            }
        }

        await ctx.SaveChangesAsync(ct);
    }

    private async Task<List<NewTransactionInfo>> SyncAddressAsync(
        FlorestaDbContext ctx,
        TrackedAddress addr,
        DerivationStrategyBase strategy,
        IReadOnlyDictionary<string, TrackedAddress> walletAddresses,
        HashSet<string> existingTxids,
        CancellationToken ct)
    {
        var newTxs = new List<NewTransactionInfo>();
        var history = await _client.ScripthashGetHistoryAsync(addr.Scripthash, ct);

        foreach (var item in history)
        {
            if (existingTxids.Contains(item.TxHash))
            {
                var existing = await ctx.Transactions
                    .FirstOrDefaultAsync(t => t.Txid == item.TxHash && t.WalletId == addr.WalletId, ct);
                if (existing != null && item.Height > 0 && existing.BlockHeight != item.Height)
                    existing.BlockHeight = item.Height;
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

        var utxos = await GetReconciledUtxosAsync(addr, ct);
        await UpdateUtxosForAddress(ctx, addr, utxos, ct);

        if (history.Length > 0 && !addr.IsUsed)
        {
            addr.IsUsed = true;
            await ExtendGapIfNeeded(ctx, addr, ct);
        }

        return newTxs;
    }

    private async Task<FlorestaElectrumUnspentItem[]> GetReconciledUtxosAsync(
        TrackedAddress addr,
        CancellationToken ct)
    {
        var utxos = await _client.ScripthashListUnspentAsync(addr.Scripthash, ct);

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

    private static bool BalanceMatchesListUnspent(
        FlorestaElectrumBalance balance,
        IReadOnlyCollection<FlorestaElectrumUnspentItem> utxos)
    {
        var confirmed = utxos.Where(u => u.Height > 0).Sum(u => u.Value);
        var unconfirmed = utxos.Where(u => u.Height <= 0).Sum(u => u.Value);
        return balance.Confirmed == confirmed && balance.Unconfirmed == unconfirmed;
    }

    private async Task UpdateUtxosForAddress(
        FlorestaDbContext ctx, TrackedAddress addr,
        FlorestaElectrumUnspentItem[] utxos, CancellationToken ct)
    {
        var currentOutpoints = utxos.Select(u => $"{u.TxHash}:{u.TxPos}").ToHashSet();

        // Mark spent UTXOs
        var existingUtxos = await ctx.Utxos
            .Where(u => u.Scripthash == addr.Scripthash && !u.IsSpent)
            .ToListAsync(ct);

        foreach (var existing in existingUtxos)
        {
            if (!currentOutpoints.Contains(existing.Outpoint))
            {
                existing.IsSpent = true;
            }
        }

        // Add new UTXOs
        var existingOutpoints = existingUtxos.Select(u => u.Outpoint).ToHashSet();
        foreach (var utxo in utxos)
        {
            var outpoint = $"{utxo.TxHash}:{utxo.TxPos}";
            if (existingOutpoints.Contains(outpoint)) continue;

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

    private long ComputeBalanceChange(FlorestaDbContext ctx, Transaction tx, string walletId)
    {
        // Look up which of our addresses are involved
        var ourScripts = ctx.TrackedAddresses
            .Where(a => a.WalletId == walletId)
            .Select(a => a.ScriptPubKey)
            .ToHashSet(new ByteArrayComparer());

        long change = 0;

        // Outputs to us are positive
        foreach (var output in tx.Outputs)
        {
            if (ourScripts.Contains(output.ScriptPubKey.ToBytes()))
            {
                change += output.Value.Satoshi;
            }
        }

        // Inputs from us are negative (we'd need to look up the previous output)
        // For simplicity, we check if the spent outpoint matches our UTXOs
        foreach (var input in tx.Inputs)
        {
            var prevOutpoint = $"{input.PrevOut.Hash}:{input.PrevOut.N}";
            var ourUtxo = ctx.Utxos.Local.FirstOrDefault(u => u.Outpoint == prevOutpoint && u.WalletId == walletId)
                          ?? ctx.Utxos.FirstOrDefault(u => u.Outpoint == prevOutpoint && u.WalletId == walletId);
            if (ourUtxo != null)
            {
                change -= ourUtxo.Value;
            }
        }

        return change;
    }

    private async Task ExtendGapIfNeeded(FlorestaDbContext ctx, TrackedAddress usedAddr, CancellationToken ct)
    {
        var parts = usedAddr.KeyPath.Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var index))
            return;

        var wallet = await ctx.TrackedWallets.FindAsync(new object[] { usedAddr.WalletId }, ct);
        if (wallet == null) return;

        if (!_trackedStrategies.TryGetValue(wallet.Id, out var strategy))
            return;

        var currentGapIndex = usedAddr.IsChange ? wallet.ChangeGapIndex : wallet.ReceiveGapIndex;
        var settings = await GetSettingsAsync();
        var gapLimit = settings.GapLimit;

        // If the used address is within gapLimit of the current boundary, extend
        if (index >= currentGapIndex - gapLimit + 1)
        {
            var newGapIndex = index + gapLimit;
            var deriveFrom = currentGapIndex + 1;
            var deriveCount = newGapIndex - currentGapIndex;

            if (deriveCount > 0)
            {
                var newAddresses = DeriveAddresses(strategy, usedAddr.IsChange, deriveFrom, deriveCount);
                foreach (var addr in newAddresses)
                {
                    addr.WalletId = wallet.Id;
                    ctx.TrackedAddresses.Add(addr);
                    await _client.ScripthashSubscribeAsync(addr.Scripthash, ct);
                }

                if (usedAddr.IsChange)
                    wallet.ChangeGapIndex = newGapIndex;
                else
                    wallet.ReceiveGapIndex = newGapIndex;
            }
        }
    }

    private class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[] x, byte[] y) => x.SequenceEqual(y);
        public int GetHashCode(byte[] obj) => BitConverter.ToInt32(obj, 0);
    }

    // ─────────────────────────────────────────────
    // NewTransactionInfo for the listener
    // ─────────────────────────────────────────────

    public class NewTransactionInfo
    {
        public string TxId { get; set; }
        public DerivationStrategyBase DerivationStrategy { get; set; }
        public int Confirmations { get; set; }
        public bool IsRbf { get; set; }
        public DateTimeOffset SeenAt { get; set; }
        public List<OutputInfo> Outputs { get; set; } = new();
    }

    public class OutputInfo
    {
        public string Address { get; set; }
        public string TrackedDestination { get; set; }
        public int Index { get; set; }
        public Money Value { get; set; }
        public string KeyPath { get; set; }
        public int KeyIndex { get; set; }
    }

    private NewTransactionInfo BuildNewTransactionInfo(
        Transaction tx,
        DerivationStrategyBase strategy,
        FlorestaElectrumHistoryItem historyItem,
        IReadOnlyDictionary<string, TrackedAddress> walletAddresses)
    {
        var info = new NewTransactionInfo
        {
            TxId = tx.GetHash().ToString(),
            DerivationStrategy = strategy,
            Confirmations = historyItem.Height > 0 && _tipHeight > 0
                ? _tipHeight - historyItem.Height + 1 : 0,
            IsRbf = tx.RBF,
            SeenAt = DateTimeOffset.UtcNow
        };

        // Find outputs that match our tracked addresses
        for (var i = 0; i < tx.Outputs.Count; i++)
        {
            var output = tx.Outputs[i];
            var scriptHash = ScriptHashUtility.ComputeScriptHash(output.ScriptPubKey);

            if (!walletAddresses.TryGetValue(scriptHash, out var outputAddress))
                continue;

            var parts = outputAddress.KeyPath.Split('/');
            var keyIndex = parts.Length == 2 && int.TryParse(parts[1], out var idx) ? idx : 0;

            info.Outputs.Add(new OutputInfo
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

    private async Task<FlorestaSettings> GetSettingsAsync()
    {
        return await _settingsRepository.GetSettingAsync<FlorestaSettings>() ?? new FlorestaSettings();
    }
}
