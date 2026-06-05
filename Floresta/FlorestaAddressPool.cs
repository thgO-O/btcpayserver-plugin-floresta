using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Floresta.Data;
using BTCPayServer.Plugins.Floresta.Services;
using Microsoft.EntityFrameworkCore;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;

namespace BTCPayServer.Plugins.Floresta;

internal sealed record FlorestaAddressReservation(
    KeyPathInformation Address,
    List<TrackedAddress> AddressesToSubscribe);

internal sealed class FlorestaAddressPool
{
    private readonly FlorestaDbContextFactory _dbFactory;
    private readonly Network _network;
    private readonly Func<string, DerivationStrategyBase> _parseStrategy;
    private readonly Func<CancellationToken, Task<FlorestaSettings>> _getSettings;
    private readonly ConcurrentDictionary<string, DerivationStrategyBase> _trackedStrategies;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public FlorestaAddressPool(
        FlorestaDbContextFactory dbFactory,
        Network network,
        Func<string, DerivationStrategyBase> parseStrategy,
        Func<CancellationToken, Task<FlorestaSettings>> getSettings,
        ConcurrentDictionary<string, DerivationStrategyBase> trackedStrategies)
    {
        _dbFactory = dbFactory;
        _network = network;
        _parseStrategy = parseStrategy;
        _getSettings = getSettings;
        _trackedStrategies = trackedStrategies;
    }

    public async Task<List<TrackedAddress>> EnsureAddressesDerivedAsync(
        string strategyStr,
        FlorestaSettings settings,
        FlorestaDescriptorRegistrationResult descriptorRegistration,
        DateTimeOffset? descriptorRegisteredAt,
        string descriptorRegistrationError,
        CancellationToken ct)
    {
        var strategy = _parseStrategy(strategyStr);
        if (strategy == null)
            throw new InvalidOperationException("Cannot parse derivation strategy.");

        await _lock.WaitAsync(ct);
        try
        {
            await using var ctx = _dbFactory.CreateContext();

            var existing = await ctx.TrackedWallets.FindAsync(new object[] { strategyStr }, ct);
            if (existing != null)
            {
                FlorestaWalletDescriptorMetadata.Apply(
                    existing,
                    descriptorRegistration,
                    descriptorRegisteredAt,
                    descriptorRegistrationError);

                existing.ReceiveGapIndex = Math.Max(existing.ReceiveGapIndex, settings.GapLimit - 1);
                existing.ChangeGapIndex = Math.Max(existing.ChangeGapIndex, settings.GapLimit - 1);

                var existingHashes = await GetWalletScripthashesAsync(ctx, strategyStr, ct);

                var addressesToAdd = DeriveAddresses(strategy, false, 0, existing.ReceiveGapIndex + 1)
                    .Concat(DeriveAddresses(strategy, true, 0, existing.ChangeGapIndex + 1));
                foreach (var addr in addressesToAdd)
                {
                    if (existingHashes.Contains(addr.Scripthash))
                        continue;

                    await AttachOrLinkAddressAsync(ctx, addr, strategyStr, ct);
                    existingHashes.Add(addr.Scripthash);
                }

                await ctx.SaveChangesAsync(ct);
                _trackedStrategies[strategyStr] = strategy;
                return await FlorestaWalletTracker.GetTrackedAddressSourcesAsync(ctx, strategyStr, ct);
            }

            var wallet = new TrackedWallet
            {
                Id = strategyStr,
                CryptoCode = settings.CryptoCode ?? "BTC",
                DerivationStrategy = strategyStr,
                ReceiveGapIndex = settings.GapLimit - 1,
                ChangeGapIndex = settings.GapLimit - 1
            };
            FlorestaWalletDescriptorMetadata.Apply(
                wallet,
                descriptorRegistration,
                descriptorRegisteredAt,
                descriptorRegistrationError);

            ctx.TrackedWallets.Add(wallet);

            var addresses = DeriveAddresses(strategy, false, 0, settings.GapLimit);
            addresses.AddRange(DeriveAddresses(strategy, true, 0, settings.GapLimit));
            var addedHashes = new HashSet<string>(StringComparer.Ordinal);

            foreach (var addr in addresses)
            {
                if (!addedHashes.Add(addr.Scripthash))
                    continue;

                await AttachOrLinkAddressAsync(ctx, addr, strategyStr, ct);
            }

            await ctx.SaveChangesAsync(ct);
            _trackedStrategies[strategyStr] = strategy;
            return await FlorestaWalletTracker.GetTrackedAddressSourcesAsync(ctx, strategyStr, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<FlorestaAddressReservation> GetNextUnusedAddressAsync(
        string strategyStr,
        bool isChange,
        bool reserve,
        FlorestaSettings settings,
        FlorestaDescriptorRegistrationResult descriptorRegistration,
        CancellationToken ct)
    {
        var addressesToSubscribe = new List<TrackedAddress>();

        await using (var readCtx = _dbFactory.CreateContext())
        {
            var existingAddress = await GetFirstAvailableAddressAsync(readCtx, strategyStr, isChange, ct);
            if (existingAddress == null)
            {
                addressesToSubscribe.AddRange(await EnsureAddressesDerivedAsync(
                    strategyStr,
                    settings,
                    descriptorRegistration,
                    null,
                    null,
                    ct));
            }
        }

        KeyPathInformation result = null;
        await _lock.WaitAsync(ct);
        try
        {
            await using var ctx = _dbFactory.CreateContext();
            var addr = await GetFirstAvailableAddressAsync(ctx, strategyStr, isChange, ct);
            if (addr == null)
            {
                addressesToSubscribe.AddRange(await ExtendAddressPoolCoreAsync(ctx, strategyStr, isChange, settings, ct));
                addr = await GetFirstAvailableAddressAsync(ctx, strategyStr, isChange, ct);
            }

            if (addr != null)
            {
                if (reserve)
                {
                    addr.IsReserved = true;
                    addressesToSubscribe.AddRange(await ExtendGapIfNeededCoreAsync(ctx, addr, ct));
                    await ctx.SaveChangesAsync(ct);
                }

                var script = Script.FromBytesUnsafe(addr.ScriptPubKey);
                result = new KeyPathInformation
                {
                    Address = script.GetDestinationAddress(_network),
                    ScriptPubKey = script,
                    KeyPath = KeyPath.Parse(addr.KeyPath),
                    Feature = isChange ? DerivationFeature.Change : DerivationFeature.Deposit,
                    TrackedSource = TrackedSource.Create(_parseStrategy(strategyStr))
                };
            }
        }
        finally
        {
            _lock.Release();
        }

        return new FlorestaAddressReservation(result, addressesToSubscribe);
    }

    public async Task CancelReservationsAsync(string strategyStr, IEnumerable<KeyPath> keyPaths, CancellationToken ct)
    {
        var keyPathStrings = keyPaths?
            .Select(k => k.ToString())
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];

        if (keyPathStrings.Length == 0)
            return;

        await _lock.WaitAsync(ct);
        try
        {
            await using var ctx = _dbFactory.CreateContext();
            var addresses = await ctx.TrackedAddresses
                .Where(a => a.WalletId == strategyStr &&
                            a.IsReserved &&
                            !a.IsUsed &&
                            keyPathStrings.Contains(a.KeyPath))
                .ToListAsync(ct);

            foreach (var address in addresses)
                address.IsReserved = false;

            if (addresses.Count > 0)
                await ctx.SaveChangesAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<TrackedAddress>> EnsureScanAddressesAsync(
        string strategyStr,
        FlorestaSettings settings,
        FlorestaDescriptorRegistrationResult descriptorRegistration,
        DateTimeOffset? descriptorRegisteredAt,
        int startingIndex,
        int gapLimit,
        CancellationToken ct)
    {
        var strategy = _parseStrategy(strategyStr);
        if (strategy == null)
            throw new InvalidOperationException("Cannot parse derivation strategy.");

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
                    ReceiveGapIndex = startingIndex + gapLimit - 1,
                    ChangeGapIndex = startingIndex + gapLimit - 1
                };
                FlorestaWalletDescriptorMetadata.Apply(wallet, descriptorRegistration, descriptorRegisteredAt, null);
                ctx.TrackedWallets.Add(wallet);
            }
            else
            {
                wallet.ReceiveGapIndex = Math.Max(wallet.ReceiveGapIndex, startingIndex + gapLimit - 1);
                wallet.ChangeGapIndex = Math.Max(wallet.ChangeGapIndex, startingIndex + gapLimit - 1);
                FlorestaWalletDescriptorMetadata.Apply(wallet, descriptorRegistration, descriptorRegisteredAt, null);
            }

            var existingHashes = await GetWalletScripthashesAsync(ctx, strategyStr, ct);

            var newAddresses = new List<TrackedAddress>();
            foreach (var addr in DeriveAddresses(strategy, false, startingIndex, gapLimit)
                         .Concat(DeriveAddresses(strategy, true, startingIndex, gapLimit)))
            {
                if (existingHashes.Contains(addr.Scripthash))
                    continue;

                newAddresses.Add(await AttachOrLinkAddressAsync(ctx, addr, strategyStr, ct));
                existingHashes.Add(addr.Scripthash);
            }

            await ctx.SaveChangesAsync(ct);
            _trackedStrategies[strategyStr] = strategy;
            return newAddresses;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<TrackedAddress>> ExtendGapIfNeededAsync(
        FlorestaDbContext ctx,
        TrackedAddress usedAddr,
        CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return await ExtendGapIfNeededCoreAsync(ctx, usedAddr, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<TrackedAddress>> ExtendGapIfNeededCoreAsync(
        FlorestaDbContext ctx,
        TrackedAddress usedAddr,
        CancellationToken ct)
    {
        var addedAddresses = new List<TrackedAddress>();
        if (!TrackedAddressReservationPolicy.TryGetKeyIndex(usedAddr.KeyPath, out var index))
            return addedAddresses;

        var wallet = await ctx.TrackedWallets.FindAsync(new object[] { usedAddr.WalletId }, ct);
        if (wallet == null)
            return addedAddresses;

        if (!_trackedStrategies.TryGetValue(wallet.Id, out var strategy))
        {
            strategy = _parseStrategy(wallet.DerivationStrategy ?? wallet.Id);
            if (strategy == null)
                return addedAddresses;
            _trackedStrategies[wallet.Id] = strategy;
        }

        var currentGapIndex = usedAddr.IsChange ? wallet.ChangeGapIndex : wallet.ReceiveGapIndex;
        var settings = await _getSettings(ct);
        var gapLimit = settings.GapLimit;

        if (!TrackedAddressReservationPolicy.ShouldExtendGap(index, currentGapIndex, gapLimit))
            return addedAddresses;

        var newGapIndex = TrackedAddressReservationPolicy.GetExtendedGapIndex(index, gapLimit);
        var deriveFrom = currentGapIndex + 1;
        var deriveCount = newGapIndex - currentGapIndex;
        if (deriveCount <= 0)
            return addedAddresses;

        var newAddresses = DeriveAddresses(strategy, usedAddr.IsChange, deriveFrom, deriveCount);
        foreach (var addr in newAddresses)
        {
            addedAddresses.Add(await AttachOrLinkAddressAsync(ctx, addr, wallet.Id, ct));
        }

        if (usedAddr.IsChange)
            wallet.ChangeGapIndex = newGapIndex;
        else
            wallet.ReceiveGapIndex = newGapIndex;

        return addedAddresses;
    }

    private async Task<List<TrackedAddress>> ExtendAddressPoolCoreAsync(
        FlorestaDbContext ctx,
        string strategyStr,
        bool isChange,
        FlorestaSettings settings,
        CancellationToken ct)
    {
        var addedAddresses = new List<TrackedAddress>();
        var wallet = await ctx.TrackedWallets.FindAsync(new object[] { strategyStr }, ct);
        if (wallet == null)
            return addedAddresses;

        var strategy = _parseStrategy(strategyStr);
        if (strategy == null)
            return addedAddresses;
        _trackedStrategies[strategyStr] = strategy;

        var currentGapIndex = isChange ? wallet.ChangeGapIndex : wallet.ReceiveGapIndex;
        var deriveFrom = currentGapIndex + 1;
        var deriveCount = Math.Max(settings.GapLimit, 1);
        var newGapIndex = currentGapIndex + deriveCount;
        var existingHashes = await GetWalletScripthashesAsync(ctx, strategyStr, ct);

        foreach (var addr in DeriveAddresses(strategy, isChange, deriveFrom, deriveCount))
        {
            if (existingHashes.Contains(addr.Scripthash))
                continue;

            addedAddresses.Add(await AttachOrLinkAddressAsync(ctx, addr, strategyStr, ct));
            existingHashes.Add(addr.Scripthash);
        }

        if (isChange)
            wallet.ChangeGapIndex = Math.Max(wallet.ChangeGapIndex, newGapIndex);
        else
            wallet.ReceiveGapIndex = Math.Max(wallet.ReceiveGapIndex, newGapIndex);

        await ctx.SaveChangesAsync(ct);
        return addedAddresses;
    }

    private static async Task<HashSet<string>> GetWalletScripthashesAsync(
        FlorestaDbContext ctx,
        string walletId,
        CancellationToken ct)
    {
        var primary = await ctx.TrackedAddresses
            .Where(a => a.WalletId == walletId)
            .Select(a => a.Scripthash)
            .ToListAsync(ct);

        var owned = await ctx.TrackedAddressOwners
            .Where(o => o.WalletId == walletId)
            .Select(o => o.Scripthash)
            .ToListAsync(ct);

        return primary.Concat(owned).ToHashSet(StringComparer.Ordinal);
    }

    private static async Task<TrackedAddress> AttachOrLinkAddressAsync(
        FlorestaDbContext ctx,
        TrackedAddress address,
        string walletId,
        CancellationToken ct)
    {
        address.WalletId = walletId;
        var existing = await ctx.TrackedAddresses
            .Include(a => a.Wallet)
            .SingleOrDefaultAsync(a => a.Scripthash == address.Scripthash, ct);

        if (existing == null)
        {
            ctx.TrackedAddresses.Add(address);
            return address;
        }

        if (existing.WalletId == walletId)
            return existing;

        if (existing.Wallet?.DerivationStrategy == null && address.KeyPath != null)
        {
            var previousWalletId = existing.WalletId;
            existing.WalletId = walletId;
            existing.KeyPath = address.KeyPath;
            existing.ScriptPubKey = address.ScriptPubKey;
            existing.Address = address.Address;
            existing.IsChange = address.IsChange;
            existing.IsUsed |= address.IsUsed;
            existing.IsReserved |= address.IsReserved;

            await EnsureAddressOwnerAsync(ctx, previousWalletId, existing.Scripthash, ct);
            return existing;
        }

        await EnsureAddressOwnerAsync(ctx, walletId, existing.Scripthash, ct);
        return FlorestaWalletTracker.CreateAddressSource(existing, walletId);
    }

    private static async Task EnsureAddressOwnerAsync(
        FlorestaDbContext ctx,
        string walletId,
        string scripthash,
        CancellationToken ct)
    {
        if (await ctx.TrackedAddressOwners.FindAsync(new object[] { walletId, scripthash }, ct) != null)
            return;

        ctx.TrackedAddressOwners.Add(new TrackedAddressOwner
        {
            WalletId = walletId,
            Scripthash = scripthash
        });
    }

    private List<TrackedAddress> DeriveAddresses(
        DerivationStrategyBase strategy,
        bool isChange,
        int fromIndex,
        int count)
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
                IsReserved = false,
                IsUsed = false
            });
        }

        return result;
    }

    private static async Task<TrackedAddress> GetFirstAvailableAddressAsync(
        FlorestaDbContext ctx,
        string strategyStr,
        bool isChange,
        CancellationToken ct)
    {
        var candidates = await ctx.TrackedAddresses
            .Where(a => a.WalletId == strategyStr &&
                        a.IsChange == isChange &&
                        !a.IsReserved &&
                        !a.IsUsed)
            .ToListAsync(ct);

        return candidates
            .OrderBy(TrackedAddressReservationPolicy.GetKeyIndexOrMax)
            .FirstOrDefault();
    }
}
