using System;
using BTCPayServer.Plugins.Floresta.Data;
using BTCPayServer.Plugins.Floresta.Services;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests;

public class FlorestaWalletDescriptorMetadataTests
{
    [Fact]
    public void AutoRegisterDisabledMarksDescriptorNotRequested()
    {
        var wallet = new TrackedWallet();
        var now = DateTimeOffset.Parse("2026-07-02T12:00:00Z");

        FlorestaWalletDescriptorMetadata.Apply(
            wallet,
            CreateRegistrationResult(0, 0, null),
            null,
            null,
            false,
            now);

        Assert.Equal(FlorestaWalletDescriptorStatus.NotRequested, wallet.DescriptorStatus);
        Assert.Equal(now, wallet.DescriptorStatusUpdatedAt);
        Assert.Null(wallet.DescriptorRegistrationError);
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(0, 2)]
    public void SuccessfulAutoRegisterMarksDescriptorRegistered(int alreadyRegistered, int registered)
    {
        var wallet = new TrackedWallet();
        var now = DateTimeOffset.Parse("2026-07-02T12:00:00Z");

        FlorestaWalletDescriptorMetadata.Apply(
            wallet,
            CreateRegistrationResult(alreadyRegistered, registered, null),
            registered > 0 ? now : null,
            null,
            true,
            now);

        Assert.Equal(FlorestaWalletDescriptorStatus.Registered, wallet.DescriptorStatus);
        Assert.Equal(now, wallet.DescriptorStatusUpdatedAt);
        Assert.Null(wallet.DescriptorRegistrationError);
    }

    [Fact]
    public void FailedRegistrationMarksDescriptorFailed()
    {
        var wallet = new TrackedWallet();
        var now = DateTimeOffset.Parse("2026-07-02T12:00:00Z");

        FlorestaWalletDescriptorMetadata.Apply(
            wallet,
            CreateRegistrationResult(0, 0, "descriptor rejected"),
            null,
            "descriptor rejected",
            true,
            now);

        Assert.Equal(FlorestaWalletDescriptorStatus.Failed, wallet.DescriptorStatus);
        Assert.Equal(now, wallet.DescriptorStatusUpdatedAt);
        Assert.Equal("descriptor rejected", wallet.DescriptorRegistrationError);
    }

    [Fact]
    public void RescanNotRequestedClearsRescanAttemptDetails()
    {
        var wallet = new TrackedWallet
        {
            RescanRequestedAt = DateTimeOffset.Parse("2026-07-01T12:00:00Z"),
            RescanStartHeight = 100,
            RescanError = "old error"
        };
        var now = DateTimeOffset.Parse("2026-07-02T12:00:00Z");

        FlorestaWalletDescriptorMetadata.ApplyRescanNotRequested(wallet, now);

        Assert.Equal(FlorestaWalletRescanStatus.NotRequested, wallet.RescanStatus);
        Assert.Equal(now, wallet.RescanStatusUpdatedAt);
        Assert.Null(wallet.RescanRequestedAt);
        Assert.Null(wallet.RescanStartHeight);
        Assert.Null(wallet.RescanError);
    }

    [Fact]
    public void RescanRequestedStoresAttemptDetails()
    {
        var wallet = new TrackedWallet { RescanError = "old error" };
        var now = DateTimeOffset.Parse("2026-07-02T12:00:00Z");

        FlorestaWalletDescriptorMetadata.ApplyRescanRequested(wallet, 123, now);

        Assert.Equal(FlorestaWalletRescanStatus.Requested, wallet.RescanStatus);
        Assert.Equal(now, wallet.RescanStatusUpdatedAt);
        Assert.Equal(now, wallet.RescanRequestedAt);
        Assert.Equal(123, wallet.RescanStartHeight);
        Assert.Null(wallet.RescanError);
    }

    [Fact]
    public void RescanFailedStoresErrorAndAttemptDetails()
    {
        var wallet = new TrackedWallet();
        var now = DateTimeOffset.Parse("2026-07-02T12:00:00Z");

        FlorestaWalletDescriptorMetadata.ApplyRescanFailed(wallet, 123, "node not ready", now);

        Assert.Equal(FlorestaWalletRescanStatus.Failed, wallet.RescanStatus);
        Assert.Equal(now, wallet.RescanStatusUpdatedAt);
        Assert.Equal(now, wallet.RescanRequestedAt);
        Assert.Equal(123, wallet.RescanStartHeight);
        Assert.Equal("node not ready", wallet.RescanError);
    }

    private static FlorestaDescriptorRegistrationResult CreateRegistrationResult(
        int alreadyRegistered,
        int registered,
        string? error)
    {
        return new FlorestaDescriptorRegistrationResult(
            new FlorestaDescriptorSet(
                "strategy",
                "hash",
                "wpkh(xpub/0/*)",
                "wpkh(xpub/1/*)"),
            alreadyRegistered,
            registered,
            error);
    }
}
