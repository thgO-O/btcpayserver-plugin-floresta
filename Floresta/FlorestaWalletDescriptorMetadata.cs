using System;
using BTCPayServer.Plugins.Floresta.Data;
using BTCPayServer.Plugins.Floresta.Services;

namespace BTCPayServer.Plugins.Floresta;

internal static class FlorestaWalletDescriptorStatus
{
    public const string Unknown = "Unknown";
    public const string NotRequested = "NotRequested";
    public const string Registered = "Registered";
    public const string Failed = "Failed";
}

internal static class FlorestaWalletRescanStatus
{
    public const string Unknown = "Unknown";
    public const string NotRequested = "NotRequested";
    public const string Pending = "Pending";
    public const string Requested = "Requested";
    public const string Failed = "Failed";
}

internal static class FlorestaWalletDescriptorMetadata
{
    public static void Apply(
        TrackedWallet wallet,
        FlorestaDescriptorRegistrationResult descriptorRegistration,
        DateTimeOffset? descriptorRegisteredAt,
        string descriptorRegistrationError,
        bool autoRegisterDescriptors,
        DateTimeOffset now)
    {
        if (descriptorRegistration.Descriptors is not null)
        {
            wallet.DescriptorHash = descriptorRegistration.Descriptors.DescriptorHash;
            wallet.ReceiveDescriptor = descriptorRegistration.Descriptors.ReceiveDescriptor;
            wallet.ChangeDescriptor = descriptorRegistration.Descriptors.ChangeDescriptor;
        }

        if (descriptorRegisteredAt is not null && wallet.DescriptorRegisteredAt is null)
            wallet.DescriptorRegisteredAt = descriptorRegisteredAt;

        wallet.DescriptorRegistrationError = descriptorRegistrationError;
        wallet.DescriptorStatus = GetDescriptorStatus(
            autoRegisterDescriptors,
            descriptorRegistration,
            descriptorRegistrationError);
        wallet.DescriptorStatusUpdatedAt = now;
    }

    public static void ApplyRescanNotRequested(TrackedWallet wallet, DateTimeOffset now)
    {
        wallet.RescanStatus = FlorestaWalletRescanStatus.NotRequested;
        wallet.RescanStatusUpdatedAt = now;
        wallet.RescanRequestedAt = null;
        wallet.RescanStartHeight = null;
        wallet.RescanError = null;
    }

    public static void ApplyRescanPending(TrackedWallet wallet, int? startHeight, DateTimeOffset now)
    {
        // Another descriptor must not narrow a pending interval.
        if (wallet.RescanStatus != FlorestaWalletRescanStatus.Pending)
            wallet.RescanStartHeight = startHeight;
        else
            wallet.RescanStartHeight = wallet.RescanStartHeight is null || startHeight is null
                ? null : Math.Min(wallet.RescanStartHeight.Value, startHeight.Value);
        wallet.RescanStatus = FlorestaWalletRescanStatus.Pending;
        wallet.RescanStatusUpdatedAt = now;
        wallet.RescanRequestedAt = null;
        wallet.RescanError = null;
    }

    public static void ApplyRescanRequested(TrackedWallet wallet, int? startHeight, DateTimeOffset now)
    {
        wallet.RescanStatus = FlorestaWalletRescanStatus.Requested;
        wallet.RescanStatusUpdatedAt = now;
        wallet.RescanRequestedAt = now;
        wallet.RescanStartHeight = startHeight;
        wallet.RescanError = null;
    }

    public static void ApplyRescanFailed(TrackedWallet wallet, int? startHeight, string error, DateTimeOffset now)
    {
        wallet.RescanStatus = FlorestaWalletRescanStatus.Failed;
        wallet.RescanStatusUpdatedAt = now;
        wallet.RescanRequestedAt = now;
        wallet.RescanStartHeight = startHeight;
        wallet.RescanError = error;
    }

    private static string GetDescriptorStatus(
        bool autoRegisterDescriptors,
        FlorestaDescriptorRegistrationResult descriptorRegistration,
        string descriptorRegistrationError)
    {
        if (!string.IsNullOrEmpty(descriptorRegistrationError) ||
            !string.IsNullOrEmpty(descriptorRegistration.Error))
            return FlorestaWalletDescriptorStatus.Failed;

        return autoRegisterDescriptors
            ? FlorestaWalletDescriptorStatus.Registered
            : FlorestaWalletDescriptorStatus.NotRequested;
    }
}
