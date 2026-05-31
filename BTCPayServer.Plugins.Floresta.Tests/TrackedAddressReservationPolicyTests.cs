using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Plugins.Floresta.Data;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests;

public class TrackedAddressReservationPolicyTests
{
    [Fact]
    public void ReservedAndUsedAddressesAreNotAvailableForReservation()
    {
        Assert.True(TrackedAddressReservationPolicy.IsAvailableForReservation(new TrackedAddress()));
        Assert.False(TrackedAddressReservationPolicy.IsAvailableForReservation(new TrackedAddress { IsReserved = true }));
        Assert.False(TrackedAddressReservationPolicy.IsAvailableForReservation(new TrackedAddress { IsUsed = true }));
    }

    [Fact]
    public void SortsKeyPathsByNumericIndex()
    {
        var addresses = new[]
        {
            new TrackedAddress { KeyPath = "0/10" },
            new TrackedAddress { KeyPath = "0/2" },
            new TrackedAddress { KeyPath = "0/1" }
        };

        var sorted = addresses
            .OrderBy(TrackedAddressReservationPolicy.GetKeyIndexOrMax)
            .Select(a => a.KeyPath)
            .ToArray();

        Assert.Equal(new[] { "0/1", "0/2", "0/10" }, sorted);
    }

    [Fact]
    public void ReservingNearBoundaryKeepsFutureGapAvailable()
    {
        const int gapLimit = 2;
        var currentGapIndex = gapLimit - 1;
        var addresses = new List<TrackedAddress>
        {
            CreateAddress(0),
            CreateAddress(1)
        };
        var reservedIndexes = new List<int>();

        for (var reservation = 0; reservation < 5; reservation++)
        {
            var next = addresses
                .Where(TrackedAddressReservationPolicy.IsAvailableForReservation)
                .OrderBy(TrackedAddressReservationPolicy.GetKeyIndexOrMax)
                .FirstOrDefault();

            Assert.NotNull(next);
            next.IsReserved = true;
            Assert.True(TrackedAddressReservationPolicy.TryGetKeyIndex(next.KeyPath, out var keyIndex));
            reservedIndexes.Add(keyIndex);

            if (!TrackedAddressReservationPolicy.ShouldExtendGap(keyIndex, currentGapIndex, gapLimit))
                continue;

            var newGapIndex = TrackedAddressReservationPolicy.GetExtendedGapIndex(keyIndex, gapLimit);
            for (var i = currentGapIndex + 1; i <= newGapIndex; i++)
            {
                addresses.Add(CreateAddress(i));
            }
            currentGapIndex = newGapIndex;
        }

        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, reservedIndexes);
        Assert.True(addresses.Count(a => TrackedAddressReservationPolicy.IsAvailableForReservation(a)) >= gapLimit);
    }

    private static TrackedAddress CreateAddress(int index)
    {
        return new TrackedAddress
        {
            WalletId = "wallet",
            KeyPath = $"0/{index}",
            IsChange = false
        };
    }
}
