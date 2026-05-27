using System;
using BTCPayServer.Plugins.Floresta.Data;
using BTCPayServer.Plugins.Floresta.Services;

namespace BTCPayServer.Plugins.Floresta;

internal static class FlorestaWalletDescriptorMetadata
{
    public static void Apply(
        TrackedWallet wallet,
        FlorestaDescriptorRegistrationResult descriptorRegistration,
        DateTimeOffset? descriptorRegisteredAt,
        string descriptorRegistrationError)
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
    }
}
