using System.Collections.Generic;
using BTCPayServer.Plugins.Floresta.Data;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests;

public class FlorestaWalletSyncTests
{
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
}
