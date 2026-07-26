using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Floresta.Data;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests;

public class FlorestaWalletSyncTests
{
    [Fact]
    public async Task AddressQueueContinuesAndReportsPartialFailure()
    {
        var addresses = new[]
        {
            CreateAddress("A"),
            CreateAddress("B"),
            CreateAddress("C")
        };
        var attempted = new List<string>();
        var errors = new List<(string Scripthash, Exception Error)>();
        var progress = new List<(int Succeeded, int Total)>();

        var summary = await FlorestaWalletSync.ProcessAddressQueueAsync(
            addresses,
            (address, _) =>
            {
                attempted.Add(address.Scripthash);
                return address.Scripthash == "B"
                    ? Task.FromException<IReadOnlyCollection<TrackedAddress>>(
                        new InvalidOperationException("electrum-sensitive-detail"))
                    : Task.FromResult<IReadOnlyCollection<TrackedAddress>>(Array.Empty<TrackedAddress>());
            },
            (address, error) => errors.Add((address.Scripthash, error)),
            (succeeded, total) => progress.Add((succeeded, total)),
            CancellationToken.None);

        Assert.Equal(new[] { "A", "B", "C" }, attempted);
        Assert.Equal(3, summary.Attempted);
        Assert.Equal(2, summary.Succeeded);
        Assert.Equal(1, summary.Failed);
        Assert.False(summary.IsComplete);
        Assert.Equal(new[] { (1, 3), (1, 3), (2, 3) }, progress);
        var error = Assert.Single(errors);
        Assert.Equal("B", error.Scripthash);
        Assert.Equal("electrum-sensitive-detail", error.Error.Message);
    }

    [Fact]
    public async Task AddressQueueProcessesDiscoveredAddressesOnceWithoutEarlyCompletion()
    {
        var first = CreateAddress("A");
        var duplicateFirst = CreateAddress("A");
        var discovered = CreateAddress("B");
        var duplicateDiscovered = CreateAddress("B");
        var attempted = new List<string>();
        var progress = new List<(int Succeeded, int Total)>();

        var summary = await FlorestaWalletSync.ProcessAddressQueueAsync(
            new[] { first, duplicateFirst },
            (address, _) =>
            {
                attempted.Add(address.Scripthash);
                IReadOnlyCollection<TrackedAddress> added = address.Scripthash == "A"
                    ? new[] { discovered, duplicateDiscovered, duplicateFirst }
                    : Array.Empty<TrackedAddress>();
                return Task.FromResult(added);
            },
            (_, _) => throw new InvalidOperationException("No address should fail."),
            (succeeded, total) => progress.Add((succeeded, total)),
            CancellationToken.None);

        Assert.Equal(new[] { "A", "B" }, attempted);
        Assert.Equal(2, summary.Attempted);
        Assert.Equal(2, summary.Succeeded);
        Assert.Equal(0, summary.Failed);
        Assert.True(summary.IsComplete);
        Assert.Equal(new[] { (1, 2), (2, 2) }, progress);
    }

    [Fact]
    public async Task AddressQueuePropagatesCancellationWithoutProcessingRemainingAddresses()
    {
        using var cts = new CancellationTokenSource();
        var attempted = new List<string>();
        var errors = new List<Exception>();
        var progress = new List<(int Succeeded, int Total)>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FlorestaWalletSync.ProcessAddressQueueAsync(
                new[] { CreateAddress("A"), CreateAddress("B"), CreateAddress("C") },
                (address, _) =>
                {
                    attempted.Add(address.Scripthash);
                    if (address.Scripthash == "B")
                    {
                        cts.Cancel();
                        return Task.FromResult<IReadOnlyCollection<TrackedAddress>>(Array.Empty<TrackedAddress>());
                    }

                    return Task.FromResult<IReadOnlyCollection<TrackedAddress>>(Array.Empty<TrackedAddress>());
                },
                (_, error) => errors.Add(error),
                (succeeded, total) => progress.Add((succeeded, total)),
                cts.Token));

        Assert.Equal(new[] { "A", "B" }, attempted);
        Assert.Empty(errors);
        Assert.Equal(new[] { (1, 3) }, progress);
    }

    [Fact]
    public void WalletAddressMapIncludesRequestedTransientSources()
    {
        var source = new TrackedAddress
        {
            WalletId = "wallet",
            Scripthash = "transient"
        };

        var addresses = FlorestaWalletSync.BuildWalletAddressMap(
            new List<TrackedAddress>(),
            new[] { source },
            "wallet");

        Assert.Same(source, addresses["transient"]);
    }

    [Fact]
    public void WalletAddressMapKeepsPersistedSourceOverTransientDuplicate()
    {
        var persisted = new TrackedAddress
        {
            WalletId = "wallet",
            Scripthash = "known",
            KeyPath = "0/1"
        };
        var transient = new TrackedAddress
        {
            WalletId = "wallet",
            Scripthash = "known"
        };

        var addresses = FlorestaWalletSync.BuildWalletAddressMap(
            new[] { persisted },
            new[] { transient },
            "wallet");

        Assert.Same(persisted, addresses["known"]);
    }

    [Fact]
    public void WalletAddressMapIgnoresTransientSourcesForOtherWallets()
    {
        var addresses = FlorestaWalletSync.BuildWalletAddressMap(
            new List<TrackedAddress>(),
            new[]
            {
                new TrackedAddress
                {
                    WalletId = "other-wallet",
                    Scripthash = "foreign"
                }
            },
            "wallet");

        Assert.Empty(addresses);
    }

    private static TrackedAddress CreateAddress(string scripthash)
    {
        return new TrackedAddress
        {
            WalletId = "wallet",
            Scripthash = scripthash
        };
    }
}
