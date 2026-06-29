using System;
using BTCPayServer.Plugins.Floresta.Data;

namespace BTCPayServer.Plugins.Floresta;

internal static class TrackedAddressReservationPolicy
{
    public static bool IsAvailableForReservation(TrackedAddress address)
    {
        return IsAvailableForReservationCore(address, null);
    }

    public static bool IsAvailableForReservationWithinRange(TrackedAddress address, int maxKeyIndex)
    {
        return IsAvailableForReservationCore(address, maxKeyIndex);
    }

    private static bool IsAvailableForReservationCore(TrackedAddress address, int? maxKeyIndex)
    {
        return address is not null &&
               !address.IsReserved &&
               !address.IsUsed &&
               TryGetKeyIndex(address.KeyPath, out var index) &&
               (maxKeyIndex is null || index <= maxKeyIndex.Value);
    }

    public static int GetKeyIndexOrMax(TrackedAddress address)
    {
        return TryGetKeyIndex(address?.KeyPath, out var index)
            ? index
            : int.MaxValue;
    }

    public static bool TryGetKeyIndex(string keyPath, out int index)
    {
        index = 0;
        if (string.IsNullOrWhiteSpace(keyPath))
            return false;

        var parts = keyPath.Split('/', 2);
        return parts.Length == 2 && int.TryParse(parts[1], out index) && index >= 0;
    }

    public static bool ShouldExtendGap(int keyIndex, int currentGapIndex, int gapLimit)
    {
        if (keyIndex < 0 || gapLimit <= 0)
            return false;

        return keyIndex >= currentGapIndex - gapLimit + 1;
    }

    public static int GetExtendedGapIndex(int keyIndex, int gapLimit)
    {
        return keyIndex + Math.Max(gapLimit, 1);
    }
}
