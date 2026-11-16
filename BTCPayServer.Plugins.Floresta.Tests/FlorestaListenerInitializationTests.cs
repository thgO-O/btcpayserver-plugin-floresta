using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Models;
using SpeedPolicy = BTCPayServer.Client.Models.SpeedPolicy;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Plugins.Floresta.Data;
using BTCPayServer.Plugins.Floresta.Services;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Wallets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBXplorer;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests;

public partial class FlorestaListenerInitializationTests
{
    [Theory]
    [InlineData("ibd")]
    [InlineData("http-error")]
    [InlineData("invalid-json")]
    public async Task PendingWalletIsReconciledByTheNextInitializationWithoutAnotherEvent(string deferredReason)
    {
        await using var harness = new Harness();
        harness.BlockchainInfo = call => call == 2
            ? deferredReason switch
            {
                "http-error" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                "invalid-json" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not JSON") },
                _ => Harness.Info(true)
            }
            : Harness.Info(call == 1 && deferredReason == "ibd");
        await harness.AddCachedWalletAsync();

        // The final status refresh is ready, but the wallet itself was deferred.
        Assert.Null(await harness.InitializeAsync());
        Assert.True(harness.Monitor.IsNetworkCompatible, harness.Monitor.LastError);
        Assert.Equal(0, harness.HistoryRequests);
        Assert.False(await harness.CachedOutputIsSpentAsync());

        // No block, subscription notification, invoice or reconnect is sent.
        Assert.Equal(90, await harness.InitializeAsync());
        Assert.Equal(1, harness.HistoryRequests);
        Assert.True(await harness.CachedOutputIsSpentAsync());

        // Once reconciled, the same initialized generation stops doing work.
        Assert.Null(await harness.InitializeAsync());
        Assert.Equal(1, harness.HistoryRequests);
    }

    [Fact]
    public async Task NewAndFailedWalletsRetryDuringIbdWithoutPreparingSuccessfulWalletsAgain()
    {
        await using var harness = new Harness();
        harness.BlockchainInfo = _ => Harness.Info(true);
        Assert.Null(await harness.InitializeAsync());
        Assert.True(harness.Monitor.IsNetworkCompatible, harness.Monitor.LastError);

        var first = new ExtKey().Neuter().ToString(Network.RegTest);
        harness.FailNextDescriptorLoad = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Tracker.TrackWalletAsync(first, harness.Token));
        Assert.Null(await harness.InitializeAsync());
        Assert.Equal(2, harness.LoadedDescriptors.Count);
        var checks = harness.DescriptorChecks;

        Assert.Null(await harness.InitializeAsync());
        Assert.Equal(checks, harness.DescriptorChecks);

        var second = new ExtKey().Neuter().ToString(Network.RegTest);
        harness.FailNextDescriptorLoad = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Tracker.TrackWalletAsync(second, harness.Token));
        checks = harness.DescriptorChecks;
        Assert.Null(await harness.InitializeAsync());
        Assert.Equal(checks + 1, harness.DescriptorChecks);
        Assert.Equal(4, harness.LoadedDescriptors.Count);

        // A new Electrum generation must prepare both wallets again.
        checks = harness.DescriptorChecks;
        await harness.Electrum.DisconnectAsync();
        Assert.Null(await harness.InitializeAsync());
        Assert.Equal(checks + 2, harness.DescriptorChecks);
    }

    [Fact]
    public async Task PreparationSettingsChangedDuringIbdTakeEffectWithoutReconnecting()
    {
        await using var harness = new Harness();
        harness.BlockchainInfo = _ => Harness.Info(true);
        harness.UpdateSettings(settings => settings.AutoRegisterDescriptors = false);
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        await harness.Tracker.TrackWalletAsync(wallet, harness.Token);
        Assert.Null(await harness.InitializeAsync());
        Assert.Empty(harness.LoadedDescriptors);
        var generation = harness.Electrum.ConnectionGeneration;

        harness.UpdateSettings(settings => settings.AutoRegisterDescriptors = true);
        Assert.Null(await harness.InitializeAsync());
        Assert.Equal(generation, harness.Electrum.ConnectionGeneration);
        Assert.Equal(2, harness.LoadedDescriptors.Count);
    }

    [Fact]
    public async Task SharedPreparationIsCachedWithTheSettingsItActuallyUsed()
    {
        await using var harness = new Harness();
        harness.BlockchainInfo = _ => Harness.Info(true);
        harness.UpdateSettings(settings => settings.AutoRegisterDescriptors = false);
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        await harness.Tracker.TrackWalletAsync(wallet, harness.Token);
        Assert.Null(await harness.InitializeAsync());
        var oldPreparation = harness.CapturePreparation(wallet);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shared = harness.Tracker.GetOrStartWalletPreparationAsync(wallet, async ct =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(ct);
            return oldPreparation;
        }, harness.Token);
        await entered.Task.WaitAsync(harness.Token);
        harness.UpdateSettings(settings => settings.AutoRegisterDescriptors = true);

        // In-memory queries and cached settings complete synchronously; this
        // pass joins the shared preparation before yielding to its completion.
        var initializing = harness.Tracker.InitializeAsync(harness.Token, isInitialBlockDownload: true);
        Assert.False(initializing.IsCompleted);
        release.SetResult();
        await shared;
        await initializing;
        Assert.Empty(harness.LoadedDescriptors);

        Assert.Null(await harness.InitializeAsync());
        Assert.Equal(2, harness.LoadedDescriptors.Count);
    }

    [Fact]
    public async Task IbdEndingDuringAReusedPreparationStillRequiresAReadyPreparationPass()
    {
        await using var harness = new Harness();
        harness.BlockchainInfo = call => Harness.Info(call <= 4);
        await harness.AddCachedWalletAsync();
        Assert.Null(await harness.InitializeAsync());
        Assert.Equal(1, harness.SubscriptionRequests);

        Assert.Null(await harness.InitializeAsync());
        Assert.Equal(1, harness.SubscriptionRequests);
        Assert.Equal(90, await harness.InitializeAsync());
        Assert.Equal(2, harness.SubscriptionRequests);
    }

    [Fact]
    public async Task PendingRescanRunsAfterIbdWithDescriptorsAlreadyLoadedAndStopsAfterAcceptance()
    {
        await using var harness = new Harness();
        harness.UpdateSettings(settings => settings.AutoRescanOnNewDescriptor = true);
        harness.BlockchainInfo = _ => Harness.Info(true);
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        await harness.Tracker.TrackWalletAsync(wallet, harness.Token);
        Assert.Equal(FlorestaWalletRescanStatus.Pending, (await harness.GetWalletAsync(wallet)).RescanStatus);
        Assert.Null(await harness.InitializeAsync());
        Assert.Equal(0, harness.RescanRequests);

        harness.BlockchainInfo = _ => Harness.Info(false);
        Assert.Equal(90, await harness.InitializeAsync());
        Assert.Equal(2, harness.LoadedDescriptors.Count);
        Assert.Equal(1, harness.RescanRequests);
        Assert.Equal(FlorestaWalletRescanStatus.Requested, (await harness.GetWalletAsync(wallet)).RescanStatus);
        Assert.Null(await harness.InitializeAsync());
        await harness.Tracker.TrackWalletAsync(wallet, harness.Token);
        Assert.Equal(1, harness.RescanRequests);
    }

    [Fact]
    public async Task IbdRejectionAfterReadinessReopensAnAlreadyInitializedListener()
    {
        await using var harness = new Harness();
        harness.UpdateSettings(settings => settings.AutoRescanOnNewDescriptor = true);
        Assert.Equal(90, await harness.InitializeAsync());
        var generation = harness.Electrum.ConnectionGeneration;
        harness.Rescan = _ => Task.FromResult(Harness.RpcError(-32093));
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        await harness.Tracker.TrackWalletAsync(wallet, harness.Token);
        Assert.Equal(1, harness.RescanRequests);
        Assert.Equal(FlorestaWalletRescanStatus.Pending, (await harness.GetWalletAsync(wallet)).RescanStatus);

        Assert.Null(await harness.InitializeAsync());
        Assert.Equal(2, harness.RescanRequests);
        harness.Rescan = _ => Task.FromResult(Harness.Json(true));
        await harness.RetryUntilRescanRequestedAsync(wallet);
        Assert.Equal(generation, harness.Electrum.ConnectionGeneration);
        Assert.Equal(3, harness.RescanRequests);
        Assert.Null(await harness.InitializeAsync());
        Assert.Equal(3, harness.RescanRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingRescanSurvivesPluginRestartIncludingCancellation(bool cancelRequest)
    {
        var database = new DatabaseFactory();
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        var descriptors = new HashSet<string>();
        await using (var first = new Harness(database))
        {
            first.UpdateSettings(settings =>
            {
                settings.AutoRescanOnNewDescriptor = true;
                settings.DefaultRescanStartHeight = 12;
            });
            first.BlockchainInfo = _ => Harness.Info(!cancelRequest);
            if (cancelRequest)
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                first.Rescan = async ct =>
                {
                    entered.SetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                    return Harness.Json(true);
                };
                var preparing = first.Tracker.PrepareWalletTrackingAsync(wallet, first.Token);
                await entered.Task.WaitAsync(first.Token);
                await first.Tracker.StopAsync(first.Token);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparing);
            }
            else
                await first.Tracker.TrackWalletAsync(wallet, first.Token);
            Assert.Equal(FlorestaWalletRescanStatus.Pending, (await first.GetWalletAsync(wallet)).RescanStatus);
            descriptors.UnionWith(first.LoadedDescriptors);
        }

        await using var restarted = new Harness(database);
        restarted.LoadedDescriptors.UnionWith(descriptors);
        restarted.UpdateSettings(settings =>
        {
            settings.AutoRescanOnNewDescriptor = true;
            settings.DefaultRescanStartHeight = 80;
        });
        Assert.Equal(90, await restarted.InitializeAsync());
        Assert.Equal(1, restarted.RescanRequests);
        Assert.Equal(12, (await restarted.GetWalletAsync(wallet)).RescanStartHeight);
        Assert.Equal(12, Assert.Single(restarted.LastRescanParameters).GetInt32());
    }

    [Theory]
    [InlineData("rpc-error")]
    [InlineData("false")]
    public async Task DefinitiveRejectionDoesNotRepeatRescans(string rejection)
    {
        await using var harness = new Harness();
        harness.UpdateSettings(settings => settings.AutoRescanOnNewDescriptor = true);
        harness.Rescan = _ => Task.FromResult(rejection == "rpc-error" ? Harness.RpcError(-32092) : Harness.Json(false));
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        await Assert.ThrowsAnyAsync<Exception>(() => harness.Tracker.TrackWalletAsync(wallet, harness.Token));
        Assert.Equal(FlorestaWalletRescanStatus.Failed, (await harness.GetWalletAsync(wallet)).RescanStatus);
        Assert.Equal(90, await harness.InitializeAsync());
        Assert.Equal(1, harness.RescanRequests);
    }

    [Fact]
    public async Task DisablingAutomaticRescanClearsPendingIntent()
    {
        await using var harness = new Harness();
        harness.UpdateSettings(settings => settings.AutoRescanOnNewDescriptor = true);
        harness.BlockchainInfo = _ => Harness.Info(true);
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        await harness.Tracker.TrackWalletAsync(wallet, harness.Token);
        harness.UpdateSettings(settings => settings.AutoRescanOnNewDescriptor = false);
        harness.BlockchainInfo = _ => Harness.Info(false);
        Assert.Equal(90, await harness.InitializeAsync());
        Assert.Equal(FlorestaWalletRescanStatus.NotRequested, (await harness.GetWalletAsync(wallet)).RescanStatus);
        Assert.Equal(0, harness.RescanRequests);
    }

    [Fact]
    public async Task IntentIsPersistedBeforeDescriptorImportAndRetainedAfterPartialFailure()
    {
        await using var harness = new Harness();
        harness.UpdateSettings(settings => settings.AutoRescanOnNewDescriptor = true);
        harness.BlockchainInfo = _ => Harness.Info(true);
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        var loads = 0;
        harness.BeforeDescriptorLoad = async () =>
        {
            Assert.Equal(FlorestaWalletRescanStatus.Pending, (await harness.GetWalletAsync(wallet)).RescanStatus);
            if (++loads == 2)
                throw new HttpRequestException("descriptor response lost");
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Tracker.TrackWalletAsync(wallet, harness.Token));
        Assert.Equal(FlorestaWalletRescanStatus.Pending, (await harness.GetWalletAsync(wallet)).RescanStatus);
        Assert.Single(harness.LoadedDescriptors);
        harness.BeforeDescriptorLoad = () => Task.CompletedTask;
        Assert.Null(await harness.InitializeAsync());
        Assert.Equal(2, harness.LoadedDescriptors.Count);
        harness.BlockchainInfo = _ => Harness.Info(false);
        Assert.Equal(90, await harness.InitializeAsync());
        Assert.Equal(1, harness.RescanRequests);
    }

    [Fact]
    public async Task PendingRescanRetriesAfterTransientDescriptorListFailure()
    {
        await using var harness = new Harness();
        harness.UpdateSettings(settings => settings.AutoRescanOnNewDescriptor = true);
        Assert.Equal(90, await harness.InitializeAsync());
        harness.Rescan = _ => Task.FromResult(Harness.RpcError(-32093));
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        await harness.Tracker.TrackWalletAsync(wallet, harness.Token);
        harness.FailNextDescriptorList = true;
        Assert.Null(await harness.InitializeAsync());
        harness.Rescan = _ => Task.FromResult(Harness.Json(true));
        await harness.RetryUntilRescanRequestedAsync(wallet);
        Assert.Equal(FlorestaWalletRescanStatus.Requested, (await harness.GetWalletAsync(wallet)).RescanStatus);
        Assert.Equal(2, harness.RescanRequests);
    }

    [Fact]
    public async Task PendingFailedRegistrationDoesNotPreventOtherWalletsFromReconciling()
    {
        await using var harness = new Harness();
        harness.UpdateSettings(settings => settings.AutoRescanOnNewDescriptor = true);
        await harness.AddCachedWalletAsync();
        harness.BeforeDescriptorLoad = () => throw new HttpRequestException("descriptor unavailable");
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Tracker.TrackWalletAsync(wallet, harness.Token));
        Assert.Equal(90, await harness.InitializeAsync());
        Assert.True(await harness.CachedOutputIsSpentAsync());
        Assert.Equal(1, harness.HistoryRequests);
        Assert.Equal(0, harness.RescanRequests);
    }

    [Fact]
    public async Task RepeatedPendingRetriesDoNotPrepareOrSynchronizeHealthyDescriptorWallets()
    {
        await using var harness = new Harness();
        harness.UpdateSettings(settings => settings.AutoRescanOnNewDescriptor = true);
        var healthy = new ExtKey().Neuter().ToString(Network.RegTest);
        await harness.Tracker.TrackWalletAsync(healthy, harness.Token);
        Assert.Equal(90, await harness.InitializeAsync());
        var histories = harness.HistoryRequests;
        var subscriptions = harness.SubscriptionRequests;
        var rescanRequests = harness.RescanRequests;
        harness.BeforeDescriptorLoad = () => throw new HttpRequestException("descriptor unavailable");
        var failing = new ExtKey().Neuter().ToString(Network.RegTest);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Tracker.TrackWalletAsync(failing, harness.Token));
        var descriptorChecks = harness.DescriptorChecks;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.Null(await harness.InitializeAsync());
            if (attempt == 0)
                Assert.Equal(descriptorChecks + 1, harness.DescriptorChecks);
            Assert.Equal(histories, harness.HistoryRequests);
            Assert.Equal(subscriptions, harness.SubscriptionRequests);
            Assert.Equal(rescanRequests, harness.RescanRequests);
        }
    }

    [Theory]
    [InlineData("http-error")]
    [InlineData("invalid-json")]
    public async Task ReadinessFailuresDuringRescanRecoveryDoNotLoseWalletReconciliation(string failure)
    {
        await using var harness = new Harness();
        harness.UpdateSettings(settings =>
        {
            settings.AutoRegisterDescriptors = false;
            settings.AutoRescanOnNewDescriptor = true;
            settings.DefaultRescanStartHeight = 12;
        });
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        var addresses = await harness.Tracker.PrepareWalletTrackingAsync(wallet, harness.Token);
        await harness.AddCachedOutputAsync(addresses[0]);
        Assert.Equal(FlorestaWalletRescanStatus.NotRequested, (await harness.GetWalletAsync(wallet)).RescanStatus);
        Assert.Empty(harness.LoadedDescriptors);
        harness.UpdateSettings(settings => settings.AutoRegisterDescriptors = true);

        var failAfterImport = false;
        var failAfterRescan = false;
        var readinessFailures = 0;
        var secondFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.BeforeDescriptorLoad = () =>
        {
            if (harness.LoadedDescriptors.Count == 1)
                failAfterImport = true;
            return Task.CompletedTask;
        };
        harness.Rescan = _ =>
        {
            failAfterRescan = true;
            return Task.FromResult(Harness.Json(true));
        };
        harness.BlockchainInfo = _ =>
        {
            if (!failAfterImport && !failAfterRescan)
                return Harness.Info(false);

            if (failAfterImport)
                failAfterImport = false;
            else
                failAfterRescan = false;
            if (Interlocked.Increment(ref readinessFailures) == 2)
                secondFailure.TrySetResult();
            return failure == "invalid-json"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not JSON") }
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        };

        await harness.InitializeAsync();
        Assert.Equal(FlorestaWalletRescanStatus.Pending, (await harness.GetWalletAsync(wallet)).RescanStatus);
        Assert.Equal(0, harness.RescanRequests);

        await harness.InitializeAsync();
        await secondFailure.Task.WaitAsync(harness.Token);
        // No block, notification, reconnect, invoice or manual sync repairs the cache.
        for (var pass = 0; pass < 3; pass++)
            await harness.InitializeAsync();

        Assert.Equal(2, readinessFailures);
        Assert.Equal(1, harness.RescanRequests);
        Assert.Equal(FlorestaWalletRescanStatus.Requested, (await harness.GetWalletAsync(wallet)).RescanStatus);
        Assert.Equal(12, (await harness.GetWalletAsync(wallet)).RescanStartHeight);
        Assert.True(await harness.CachedOutputIsSpentAsync());
    }

    [Theory]
    [InlineData(false, "http-error")]
    [InlineData(false, "invalid-json")]
    [InlineData(false, "ibd")]
    [InlineData(true, "http-error")]
    [InlineData(true, "invalid-json")]
    [InlineData(true, "ibd")]
    public async Task DeferredRescanRetryReconcilesOnlyItsWalletWithoutAnotherRescan(
        bool failStartupRegistration, string failure)
    {
        await using var harness = new Harness();
        var healthy = new ExtKey().Neuter().ToString(Network.RegTest);
        await harness.Tracker.TrackWalletAsync(healthy, harness.Token);
        if (!failStartupRegistration)
            Assert.Equal(90, await harness.InitializeAsync());

        harness.UpdateSettings(settings =>
        {
            settings.AutoRegisterDescriptors = false;
            settings.AutoRescanOnNewDescriptor = true;
            settings.DefaultRescanStartHeight = 12;
        });
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        var addresses = await harness.Tracker.PrepareWalletTrackingAsync(wallet, harness.Token);
        await harness.AddCachedOutputAsync(addresses[0]);
        harness.UpdateSettings(settings => settings.AutoRegisterDescriptors = true);
        if (failStartupRegistration)
        {
            harness.BeforeDescriptorLoad = () => throw new HttpRequestException("descriptor unavailable");
            Assert.Equal(90, await harness.InitializeAsync());
            harness.BeforeDescriptorLoad = () => Task.CompletedTask;
        }
        else
        {
            harness.BlockchainInfo = _ => Harness.Info(true);
            await harness.Tracker.TrackWalletAsync(wallet, harness.Token);
        }
        Assert.Equal(FlorestaWalletRescanStatus.Pending, (await harness.GetWalletAsync(wallet)).RescanStatus);
        Assert.False(await harness.CachedOutputIsSpentAsync());
        var histories = harness.HistoryRequests;
        var subscriptions = harness.SubscriptionRequests;
        var failAfterRescan = false;
        var deferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Rescan = _ =>
        {
            failAfterRescan = true;
            return Task.FromResult(Harness.Json(true));
        };
        harness.BlockchainInfo = _ =>
        {
            if (!failAfterRescan)
                return Harness.Info(false);
            failAfterRescan = false;
            deferred.TrySetResult();
            return failure switch
            {
                "http-error" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                "invalid-json" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not JSON") },
                _ => Harness.Info(true)
            };
        };

        Assert.Null(await harness.InitializeAsync());
        await deferred.Task.WaitAsync(harness.Token);
        Assert.Equal(FlorestaWalletRescanStatus.Requested, (await harness.GetWalletAsync(wallet)).RescanStatus);
        Assert.False(await harness.CachedOutputIsSpentAsync());
        var descriptorChecks = harness.DescriptorChecks;

        // Only the listener retries; no block, notification, reconnect or manual sync.
        await harness.RetryUntilWalletReconciledAsync();
        Assert.Equal(1, harness.RescanRequests);
        Assert.Equal(12, (await harness.GetWalletAsync(wallet)).RescanStartHeight);
        Assert.Equal(descriptorChecks, harness.DescriptorChecks);
        Assert.Equal(histories + addresses.Count, harness.HistoryRequests);
        Assert.Equal(subscriptions + addresses.Count * 2, harness.SubscriptionRequests);

        for (var pass = 0; pass < 3; pass++)
            Assert.Null(await harness.InitializeAsync());
        Assert.Equal(histories + addresses.Count, harness.HistoryRequests);
        Assert.Equal(subscriptions + addresses.Count * 2, harness.SubscriptionRequests);
        Assert.Equal(1, harness.RescanRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryRetryJoiningAnImportRetainsDeferredSyncEvenIfWaiterCancels(bool cancelWaiter)
    {
        await using var harness = new Harness();
        Assert.Equal(90, await harness.InitializeAsync());
        harness.UpdateSettings(settings =>
        {
            settings.AutoRegisterDescriptors = false;
            settings.AutoRescanOnNewDescriptor = true;
        });
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        var addresses = await harness.Tracker.PrepareWalletTrackingAsync(wallet, harness.Token);
        await harness.AddCachedOutputAsync(addresses[0]);
        harness.UpdateSettings(settings => settings.AutoRegisterDescriptors = true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.BeforeDescriptorLoad = async () =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(harness.Token);
        };
        var deferSync = false;
        var deferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Rescan = _ =>
        {
            deferSync = true;
            return Task.FromResult(Harness.Json(true));
        };
        harness.BlockchainInfo = _ =>
        {
            if (!deferSync)
                return Harness.Info(false);
            deferSync = false;
            deferred.TrySetResult();
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        };

        var import = harness.Tracker.PrepareAndStartTrackingAsync(wallet, harness.Token);
        await entered.Task.WaitAsync(harness.Token);
        using var waiter = CancellationTokenSource.CreateLinkedTokenSource(harness.Token);
        var retry = harness.Tracker.RetryPendingWalletsAsync(waiter.Token);
        Assert.False(retry.IsCompleted);
        if (cancelWaiter)
        {
            waiter.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retry);
        }
        release.TrySetResult();
        await import;
        if (!cancelWaiter)
            await retry;
        await deferred.Task.WaitAsync(harness.Token);
        Assert.False(await harness.CachedOutputIsSpentAsync());
        Assert.Equal(FlorestaWalletRescanStatus.Requested, (await harness.GetWalletAsync(wallet)).RescanStatus);

        await harness.RetryUntilWalletReconciledAsync();
        Assert.Equal(1, harness.RescanRequests);
        Assert.Equal(addresses.Count, harness.HistoryRequests);
    }

    [Fact]
    public async Task RestartReconcilesDeferredRecoveryWithoutResubmittingAcceptedRescan()
    {
        var database = new DatabaseFactory();
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        List<string> descriptors;
        await using (var first = new Harness(database))
        {
            Assert.Equal(90, await first.InitializeAsync());
            first.UpdateSettings(settings =>
            {
                settings.AutoRegisterDescriptors = false;
                settings.AutoRescanOnNewDescriptor = true;
                settings.DefaultRescanStartHeight = 12;
            });
            var addresses = await first.Tracker.PrepareWalletTrackingAsync(wallet, first.Token);
            await first.AddCachedOutputAsync(addresses[0]);
            first.UpdateSettings(settings => settings.AutoRegisterDescriptors = true);
            first.BlockchainInfo = _ => Harness.Info(true);
            await first.Tracker.TrackWalletAsync(wallet, first.Token);
            var deferSync = false;
            var deferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            first.Rescan = _ =>
            {
                deferSync = true;
                return Task.FromResult(Harness.Json(true));
            };
            first.BlockchainInfo = _ =>
            {
                if (!deferSync)
                    return Harness.Info(false);
                deferSync = false;
                deferred.TrySetResult();
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            };
            Assert.Null(await first.InitializeAsync());
            await deferred.Task.WaitAsync(first.Token);
            Assert.False(await first.CachedOutputIsSpentAsync());
            Assert.Equal(FlorestaWalletRescanStatus.Requested, (await first.GetWalletAsync(wallet)).RescanStatus);
            descriptors = new List<string>(first.LoadedDescriptors);
        }

        await using var restarted = new Harness(database);
        restarted.LoadedDescriptors.UnionWith(descriptors);
        restarted.UpdateSettings(settings => settings.AutoRescanOnNewDescriptor = true);
        Assert.Equal(90, await restarted.InitializeAsync());
        Assert.True(await restarted.CachedOutputIsSpentAsync());
        Assert.Equal(FlorestaWalletRescanStatus.Requested, (await restarted.GetWalletAsync(wallet)).RescanStatus);
        Assert.Equal(12, (await restarted.GetWalletAsync(wallet)).RescanStartHeight);
        Assert.Equal(0, restarted.RescanRequests);
        Assert.False(restarted.Tracker.HasDeferredWalletSynchronizations);
    }

    [Fact]
    public async Task ImportStillValidatesDescriptorsDuringAnAcceptedRescanSynchronizationRetry()
    {
        await using var harness = new Harness();
        Assert.Equal(90, await harness.InitializeAsync());
        harness.UpdateSettings(settings =>
        {
            settings.AutoRegisterDescriptors = false;
            settings.AutoRescanOnNewDescriptor = true;
        });
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        var addresses = await harness.Tracker.PrepareWalletTrackingAsync(wallet, harness.Token);
        await harness.AddCachedOutputAsync(addresses[0]);
        harness.UpdateSettings(settings => settings.AutoRegisterDescriptors = true);
        harness.BlockchainInfo = _ => Harness.Info(true);
        await harness.Tracker.TrackWalletAsync(wallet, harness.Token);
        var deferSync = false;
        var deferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Rescan = _ =>
        {
            deferSync = true;
            return Task.FromResult(Harness.Json(true));
        };
        harness.BlockchainInfo = _ =>
        {
            if (!deferSync)
                return Harness.Info(false);
            deferSync = false;
            deferred.TrySetResult();
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        };
        await harness.InitializeAsync();
        await deferred.Task.WaitAsync(harness.Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.BeforeSubscription = async () =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(harness.Token);
        };
        Task<int?> recovery;
        do
        {
            recovery = harness.InitializeAsync();
            await Task.WhenAny(recovery, entered.Task).WaitAsync(harness.Token);
            if (!entered.Task.IsCompleted)
            {
                await recovery;
                await Task.Delay(10, harness.Token);
            }
        } while (!entered.Task.IsCompleted);

        try
        {
            harness.FailNextDescriptorList = true;
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                harness.Tracker.PrepareAndStartTrackingAsync(wallet, harness.Token));
            Assert.False(harness.FailNextDescriptorList);
        }
        finally
        {
            release.TrySetResult();
            await recovery;
        }
        Assert.Equal(1, harness.RescanRequests);
        Assert.True(await harness.CachedOutputIsSpentAsync());
    }

    [Fact]
    public async Task FailedPreparationDoesNotBecomeASyncOnlyRetryAfterRescanIsDisabled()
    {
        await using var harness = new Harness();
        Assert.Equal(90, await harness.InitializeAsync());
        harness.UpdateSettings(settings =>
        {
            settings.AutoRegisterDescriptors = false;
            settings.AutoRescanOnNewDescriptor = true;
        });
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        var addresses = await harness.Tracker.PrepareWalletTrackingAsync(wallet, harness.Token);
        await harness.AddCachedOutputAsync(addresses[0]);
        harness.UpdateSettings(settings => settings.AutoRegisterDescriptors = true);
        harness.BeforeDescriptorLoad = () => throw new HttpRequestException("descriptor unavailable");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Tracker.TrackWalletAsync(wallet, harness.Token));
        await harness.InitializeAsync();
        harness.UpdateSettings(settings => settings.AutoRescanOnNewDescriptor = false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(harness.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        do
        {
            await harness.InitializeAsync();
            await Task.Delay(10, deadline.Token);
        } while ((await harness.GetWalletAsync(wallet)).RescanStatus != FlorestaWalletRescanStatus.NotRequested ||
                 harness.Tracker.HasDeferredWalletSynchronizations);

        Assert.False(await harness.CachedOutputIsSpentAsync());
        Assert.Equal(0, harness.HistoryRequests);
        Assert.Equal(0, harness.RescanRequests);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Tracker.PrepareAndStartTrackingAsync(wallet, harness.Token));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    public async Task RecoveryDeliversTransactionsOnlyAfterSyncIncludingSpentAndPartialHistory(
        bool syncOnlyRetry, bool alreadySpent, bool partial)
    {
        await using var harness = new Harness();
        Assert.Equal(90, await harness.InitializeAsync());
        harness.UpdateSettings(settings =>
        {
            settings.AutoRegisterDescriptors = false;
            settings.AutoRescanOnNewDescriptor = true;
        });
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        var addresses = await harness.Tracker.PrepareWalletTrackingAsync(wallet, harness.Token);
        harness.UpdateSettings(settings => settings.AutoRegisterDescriptors = true);
        harness.BlockchainInfo = _ => Harness.Info(true);
        await harness.Tracker.TrackWalletAsync(wallet, harness.Token);
        var transaction = harness.SetRecoveredHistory(addresses[0], alreadySpent);
        if (partial)
            harness.FailedHistoryScripts.Add(addresses[1].Scripthash);
        var deferSync = false;
        var deferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Rescan = _ =>
        {
            deferSync = syncOnlyRetry;
            return Task.FromResult(Harness.Json(true));
        };
        harness.BlockchainInfo = _ =>
        {
            if (!deferSync)
                return Harness.Info(false);
            deferSync = false;
            deferred.TrySetResult();
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        };
        if (syncOnlyRetry)
        {
            await harness.InitializeAsync();
            await deferred.Task.WaitAsync(harness.Token);
            Assert.Empty(harness.Tracker.RecoveredTransactions);
        }

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.BeforeSubscription = async () =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(harness.Token);
        };
        Task<int?> recovery;
        do
        {
            recovery = harness.InitializeAsync();
            await Task.WhenAny(recovery, entered.Task).WaitAsync(harness.Token);
            if (!entered.Task.IsCompleted)
            {
                await recovery;
                await Task.Delay(10, harness.Token);
            }
        } while (!entered.Task.IsCompleted);

        var delivered = new List<FlorestaWalletTracker.NewTransactionInfo>();
        Task<bool> Process(FlorestaWalletTracker.NewTransactionInfo tx, CancellationToken _)
        {
            delivered.Add(tx);
            return Task.FromResult(true);
        }
        try
        {
            // A returned preparation is not a completed sync or a payment signal.
            if (!syncOnlyRetry)
                await recovery.WaitAsync(harness.Token);
            await harness.Listener.ProcessRecoveredTransactionsAsync(Process, harness.Token);
            Assert.Empty(delivered);
            Assert.Empty(harness.Tracker.RecoveredTransactions);
        }
        finally
        {
            release.TrySetResult();
            await recovery;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(harness.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (harness.Tracker.RecoveredTransactions.IsEmpty)
            await Task.Delay(10, deadline.Token);
        await harness.Listener.ProcessRecoveredTransactionsAsync(Process, harness.Token);
        var received = Assert.Single(delivered);
        Assert.Equal(transaction.GetHash().ToString(), received.TxId);
        Assert.Equal(1234, Assert.Single(received.Outputs).Value.Satoshi);
        Assert.Equal(addresses[0].ScriptPubKey, received.Outputs[0].ScriptPubKey.ToBytes());
        Assert.Equal(1, harness.RescanRequests);

        // The tx remains deliverable even when another address failed, or its
        // received output is no longer in listunspent.
        harness.FailedHistoryScripts.Clear();
        while (harness.Tracker.HasDeferredWalletSynchronizations)
        {
            await harness.InitializeAsync();
            await Task.Delay(10, deadline.Token);
        }
        for (var pass = 0; pass < 3; pass++)
        {
            await harness.InitializeAsync();
            await harness.Listener.ProcessRecoveredTransactionsAsync(Process, harness.Token);
        }
        Assert.Single(delivered);
        Assert.Empty(harness.Tracker.RecoveredTransactions);
        Assert.Equal(1, harness.RescanRequests);
    }

    [Theory]
    [InlineData("exception")]
    [InlineData("unavailable")]
    [InlineData("cancel")]
    public async Task FailedRecoveredDeliveryIsRetainedWithoutBlockingOtherTransactions(string failure)
    {
        await using var harness = new Harness();
        var first = new FlorestaWalletTracker.NewTransactionInfo { TxId = uint256.One.ToString() };
        var second = new FlorestaWalletTracker.NewTransactionInfo { TxId = uint256.Zero.ToString() };
        harness.Tracker.RecoveredTransactions.Enqueue(first);
        harness.Tracker.RecoveredTransactions.Enqueue(second);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(harness.Token);
        var delivered = new List<string>();
        var attempts = 0;
        Task<bool> Fail(FlorestaWalletTracker.NewTransactionInfo tx, CancellationToken ct)
        {
            attempts++;
            if (ReferenceEquals(tx, first))
            {
                if (failure == "exception") throw new InvalidOperationException("consumer unavailable");
                if (failure == "cancel")
                {
                    canceled.Cancel();
                    throw new OperationCanceledException(ct);
                }
                return Task.FromResult(false);
            }
            delivered.Add(tx.TxId);
            return Task.FromResult(true);
        }
        if (failure == "cancel")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                harness.Listener.ProcessRecoveredTransactionsAsync(Fail, canceled.Token));
        else
            await harness.Listener.ProcessRecoveredTransactionsAsync(Fail, canceled.Token);
        Assert.Equal(failure == "cancel" ? 1 : 2, attempts);
        Assert.Equal(failure == "cancel" ? 2 : 1, harness.Tracker.RecoveredTransactions.Count);

        await harness.Listener.ProcessRecoveredTransactionsAsync((tx, _) =>
        {
            delivered.Add(tx.TxId);
            return Task.FromResult(true);
        }, harness.Token);
        Assert.Equal(2, delivered.Distinct().Count());
        Assert.Equal(2, delivered.Count);
        Assert.Empty(harness.Tracker.RecoveredTransactions);
    }

    [Fact]
    public async Task TransactionsQueuedDuringDeliveryRemainForTheNextPass()
    {
        await using var harness = new Harness();
        var first = new FlorestaWalletTracker.NewTransactionInfo { TxId = uint256.One.ToString() };
        var second = new FlorestaWalletTracker.NewTransactionInfo { TxId = uint256.Zero.ToString() };
        harness.Tracker.RecoveredTransactions.Enqueue(first);
        var delivered = new List<string>();
        await harness.Listener.ProcessRecoveredTransactionsAsync((tx, _) =>
        {
            delivered.Add(tx.TxId);
            harness.Tracker.RecoveredTransactions.Enqueue(second);
            return Task.FromResult(true);
        }, harness.Token);
        Assert.Equal(first.TxId, Assert.Single(delivered));
        Assert.Same(second, Assert.Single(harness.Tracker.RecoveredTransactions));
        await harness.Listener.ProcessRecoveredTransactionsAsync((tx, _) =>
        {
            delivered.Add(tx.TxId);
            return Task.FromResult(true);
        }, harness.Token);
        Assert.Equal(new[] { first.TxId, second.TxId }, delivered);
        Assert.Empty(harness.Tracker.RecoveredTransactions);
    }

    [Theory]
    [InlineData(false, "ready")]
    [InlineData(true, "ready")]
    [InlineData(true, "join-import")]
    [InlineData(true, "partial-transaction")]
    [InlineData(true, "rpc-refresh")]
    [InlineData(true, "reconnect")]
    [InlineData(true, "ibd-initialization")]
    [InlineData(true, "direct-deferred")]
    [InlineData(true, "direct-partial")]
    [InlineData(true, "requested-reinitialization")]
    [InlineData(true, "stale-removed")]
    [InlineData(true, "stale-mempool")]
    [InlineData(true, "stale-confirmed")]
    [InlineData(true, "stale-other-wallet")]
    public async Task QuietListenerRecordsRecoveredInvoicePaymentEvenWhenReceiptIsAlreadySpent(
        bool alreadySpent, string scenario)
    {
        var invoices = new InvoiceDatabase();
        await using var harness = new Harness(invoices: invoices);
        Assert.Equal(90, await harness.InitializeAsync());
        harness.UpdateSettings(settings =>
        {
            settings.AutoRegisterDescriptors = false;
            settings.AutoRescanOnNewDescriptor = true;
        });
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        var addresses = await harness.Tracker.PrepareWalletTrackingAsync(wallet, harness.Token);
        var invoiceId = await harness.CreateInvoiceAsync(addresses[0]);
        harness.UpdateSettings(settings => settings.AutoRegisterDescriptors = true);
        var directImport = scenario.StartsWith("direct-", StringComparison.Ordinal);
        if (!directImport)
        {
            harness.BlockchainInfo = _ => Harness.Info(true);
            await harness.Tracker.TrackWalletAsync(wallet, harness.Token);
        }
        if (scenario == "ibd-initialization")
        {
            // The listener itself observes IBD, so its transition to ready must
            // prepare the rescan through full initialization, not retry-only.
            Assert.Null(await harness.InitializeAsync());
            Assert.True(harness.Monitor.IsInitialBlockDownload);
        }
        var transaction = harness.SetRecoveredHistory(addresses[0], alreadySpent);
        var recoveredHistory = harness.HistoryResponse;
        if (scenario == "requested-reinitialization")
            harness.HistoryResponse = _ => Array.Empty<object>();
        if (scenario == "stale-other-wallet")
        {
            await using var ctx = harness.Database.CreateContext();
            ctx.TrackedWallets.Add(new TrackedWallet { Id = "other-wallet", CryptoCode = "BTC" });
            ctx.Transactions.Add(new TrackedTransaction
            {
                WalletId = "other-wallet", Txid = transaction.GetHash().ToString(),
                RawTx = transaction.ToBytes(), BlockHeight = 80,
                SeenAt = DateTimeOffset.UtcNow,
                RemovedAt = DateTimeOffset.UtcNow
            });
            await ctx.SaveChangesAsync(harness.Token);
        }
        if (scenario == "direct-partial")
            harness.FailedHistoryScripts.Add(addresses[0].Scripthash);
        var deferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deferSync = false;
        if (scenario is "rpc-refresh" or "reconnect" or "ibd-initialization" or "direct-deferred")
            harness.Rescan = _ =>
            {
                deferSync = true;
                return Task.FromResult(Harness.Json(true));
            };
        harness.BlockchainInfo = _ =>
        {
            if (!deferSync)
                return Harness.Info(false);
            deferSync = false;
            deferred.TrySetResult();
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        };
        if (scenario == "partial-transaction")
        {
            var unavailableTx = uint256.One.ToString();
            harness.FailedTransactions.Add(unavailableTx);
            harness.HistoryResponse = script => script == addresses[0].Scripthash
                ? new object[]
                {
                    new { tx_hash = transaction.GetHash().ToString(), height = 80 },
                    new { tx_hash = unavailableTx, height = 80 }
                } : Array.Empty<object>();
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(harness.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        if (directImport)
        {
            await harness.ImportWalletAsync(wallet);
            while (!harness.Tracker.HasDeferredWalletSynchronizations)
                await Task.Delay(10, deadline.Token);
            Assert.Equal(FlorestaWalletRescanStatus.Requested, (await harness.GetWalletAsync(wallet)).RescanStatus);
            harness.FailedHistoryScripts.Clear();
        }
        else if (scenario == "join-import")
        {
            harness.LoadedDescriptors.Clear();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            harness.BeforeDescriptorLoad = async () =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(harness.Token);
            };
            var import = harness.Tracker.PrepareAndStartTrackingAsync(wallet, harness.Token);
            await entered.Task.WaitAsync(harness.Token);
            var retry = harness.InitializeAsync();
            Assert.False(retry.IsCompleted);
            release.TrySetResult();
            await Task.WhenAll(import, retry);
        }
        else
            await harness.InitializeAsync();
        if (scenario is "rpc-refresh" or "reconnect" or "ibd-initialization")
        {
            await deferred.Task.WaitAsync(deadline.Token);
            if (scenario != "ibd-initialization")
                Assert.True(harness.Tracker.HasDeferredWalletSynchronizations);
            Assert.Empty(harness.Tracker.RecoveredTransactions);
            var generation = harness.Electrum.ConnectionGeneration;
            if (scenario == "rpc-refresh")
            {
                harness.BlockchainInfo = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                Assert.Null(await harness.InitializeAsync());
                Assert.False(harness.Monitor.IsNetworkCompatible);
                harness.BlockchainInfo = _ => Harness.Info(false);
            }
            else if (scenario == "reconnect")
                await harness.Electrum.DisconnectAsync();
            // The same process still owes recovery delivery even though the
            // next listener pass takes the full-initialization path.
            Assert.Equal(90, await harness.InitializeAsync());
            if (scenario == "reconnect")
                Assert.True(harness.Electrum.ConnectionGeneration > generation);
        }
        if (scenario == "requested-reinitialization")
        {
            while (harness.Tracker.HasDeferredWalletSynchronizations)
                await Task.Delay(10, deadline.Token);
            Assert.Empty(harness.Tracker.RecoveredTransactions);
            harness.HistoryResponse = recoveredHistory;
            await harness.Electrum.DisconnectAsync();
            await EnvironmentVariableTestHelper.WithBackendReplacementEnvironmentAsync("true", async () =>
            {
                await harness.Monitor.RefreshAsync(harness.Token);
                return true;
            });
            Assert.Equal(90, await harness.InitializeAsync());
        }
        while (harness.Tracker.RecoveredTransactions.IsEmpty)
        {
            if (directImport)
                await harness.InitializeAsync();
            await Task.Delay(10, deadline.Token);
        }
        if (scenario == "partial-transaction")
        {
            Assert.True(harness.Tracker.HasDeferredWalletSynchronizations);
            harness.FailedTransactions.Clear();
            harness.SetRecoveredHistory(addresses[0], alreadySpent);
        }
        while (harness.Tracker.HasDeferredWalletSynchronizations)
        {
            await harness.InitializeAsync();
            await Task.Delay(10, deadline.Token);
        }
        var expectedStatus = PaymentStatus.Settled;
        if (scenario.StartsWith("stale-", StringComparison.Ordinal))
        {
            // Retain the discovery snapshot across a failed delivery and a
            // later wallet sync before the real payment consumer runs.
            await harness.Listener.ProcessRecoveredTransactionsAsync((_, _) =>
                Task.FromException<bool>(new IOException("consumer temporarily unavailable")), harness.Token);
            if (scenario == "stale-removed")
            {
                harness.HistoryResponse = _ => Array.Empty<object>();
                expectedStatus = PaymentStatus.Unaccounted;
            }
            else if (scenario == "stale-mempool")
            {
                harness.HistoryResponse = script => script == addresses[0].Scripthash
                    ? new object[] { new { tx_hash = transaction.GetHash().ToString(), height = 0 } }
                    : Array.Empty<object>();
                expectedStatus = PaymentStatus.Processing;
            }
            Assert.Empty(await harness.Tracker.HandleNewBlockAsync(91, harness.Token));
        }
        var current = await harness.Tracker.GetTransactionInfoAsync(wallet, transaction.GetHash().ToString(), harness.Token);
        Assert.NotNull(current);
        if (scenario == "stale-other-wallet")
        {
            Assert.Equal(-1, (await harness.Tracker.GetTransactionResultAsync(transaction.GetHash().ToString(), harness.Token)).Confirmations);
            Assert.True(current.Confirmations > 0);
        }
        await using (var ctx = invoices.CreateContext())
            Assert.Empty(await ctx.Payments.ToListAsync(harness.Token));

        // No further block, notification or reconnect can deliver this payment.
        await harness.RunUntilPaymentAsync();
        await using (var ctx = invoices.CreateContext())
        {
            var payment = Assert.Single(await ctx.Payments.ToListAsync(harness.Token));
            Assert.Equal(invoiceId, payment.InvoiceDataId);
            Assert.Equal(new OutPoint(transaction.GetHash(), 0).ToString(), payment.Id);
            Assert.Equal(0.00001234m, payment.Amount);
            Assert.Equal(expectedStatus, payment.Status);
        }
        Assert.NotNull(harness.LastOnChainTransaction);
        Assert.Equal((long)current.Confirmations, (long)harness.LastOnChainTransaction.Confirmations);
        Assert.Equal(current.Height, harness.LastOnChainTransaction.Height);
        Assert.Equal(1, harness.ReceivedPayments);
        Assert.Equal(1, harness.OnChainEvents);
        Assert.Empty(harness.Tracker.RecoveredTransactions);
        Assert.Equal(1, harness.RescanRequests);
    }

    [Fact]
    public async Task RequestedWalletInitializationDeliversSpentReceiptAfterRestart()
    {
        var database = new DatabaseFactory();
        var invoices = new InvoiceDatabase();
        string wallet;
        List<TrackedAddress> addresses;
        string invoiceId;
        List<string> descriptors;
        await using (var first = new Harness(database, invoices))
        {
            Assert.Equal(90, await first.InitializeAsync());
            first.UpdateSettings(s => { s.AutoRegisterDescriptors = false; s.AutoRescanOnNewDescriptor = true; });
            wallet = new ExtKey().Neuter().ToString(Network.RegTest);
            addresses = await first.Tracker.PrepareWalletTrackingAsync(wallet, first.Token);
            invoiceId = await first.CreateInvoiceAsync(addresses[0]);
            first.UpdateSettings(s => s.AutoRegisterDescriptors = true);
            first.BlockchainInfo = _ => Harness.Info(true);
            await first.Tracker.TrackWalletAsync(wallet, first.Token);
            var defer = false;
            var deferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            first.Rescan = _ => { defer = true; return Task.FromResult(Harness.Json(true)); };
            first.BlockchainInfo = _ =>
            {
                if (!defer) return Harness.Info(false);
                defer = false;
                deferred.TrySetResult();
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            };
            await first.InitializeAsync();
            await deferred.Task.WaitAsync(first.Token);
            Assert.Equal(FlorestaWalletRescanStatus.Requested, (await first.GetWalletAsync(wallet)).RescanStatus);
            descriptors = first.LoadedDescriptors.ToList();
        }
        // A fresh service graph loses memory debt but shares persisted wallets
        // and invoices. The receipt is first discovered after this restart.
        await using var restarted = new Harness(database, invoices);
        restarted.LoadedDescriptors.UnionWith(descriptors);
        restarted.UpdateSettings(s => s.AutoRescanOnNewDescriptor = true);
        var transaction = restarted.SetRecoveredHistory(addresses[0], true);
        Assert.Equal(90, await restarted.InitializeAsync());
        Assert.Single(restarted.Tracker.RecoveredTransactions);
        await restarted.RunUntilPaymentAsync();
        await using (var ctx = invoices.CreateContext())
        {
            var payment = Assert.Single(await ctx.Payments.ToListAsync(restarted.Token));
            Assert.Equal(invoiceId, payment.InvoiceDataId);
            Assert.Equal(new OutPoint(transaction.GetHash(), 0).ToString(), payment.Id);
            Assert.Equal(PaymentStatus.Settled, payment.Status);
        }
        Assert.Equal(1, restarted.ReceivedPayments);
        Assert.Equal(0, restarted.RescanRequests);
        Assert.False(restarted.Tracker.HasDeferredWalletSynchronizations);
    }

    [Fact]
    public async Task FailedRecoveryDoesNotPollMonitoredInvoices()
    {
        var invoices = new InvoiceDatabase();
        await using var harness = new Harness(invoices: invoices);
        Assert.Equal(90, await harness.InitializeAsync());
        harness.UpdateSettings(settings =>
        {
            settings.AutoRegisterDescriptors = false;
            settings.AutoRescanOnNewDescriptor = true;
        });
        var wallet = new ExtKey().Neuter().ToString(Network.RegTest);
        var addresses = await harness.Tracker.PrepareWalletTrackingAsync(wallet, harness.Token);
        await harness.CreateInvoiceAsync(addresses[0]);
        harness.UpdateSettings(settings => settings.AutoRegisterDescriptors = true);
        harness.BeforeDescriptorLoad = () => throw new HttpRequestException("descriptor unavailable");
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Tracker.TrackWalletAsync(wallet, harness.Token));
        var contexts = invoices.ContextsCreated;
        for (var pass = 0; pass < 3; pass++)
            Assert.Null(await harness.InitializeAsync());
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.BeforeDescriptorLoad = () =>
        {
            retried.TrySetResult();
            throw new HttpRequestException("descriptor unavailable");
        };
        await EnvironmentVariableTestHelper.WithBackendReplacementEnvironmentAsync("true", async () =>
        {
            await harness.Listener.StartAsync(harness.Token);
            try
            {
                await retried.Task.WaitAsync(harness.Token);
                // Observe the quiet loop after its failed recovery attempt,
                // including work outside InitializeListenerAsync.
                await Task.Delay(250, harness.Token);
                Assert.Equal(contexts, invoices.ContextsCreated);
            }
            finally
            {
                await harness.Listener.StopAsync(harness.Token);
            }
            return true;
        });
        Assert.Empty(harness.Tracker.RecoveredTransactions);
        Assert.Equal(0, harness.ReceivedPayments);
    }

    // These tests exercise the actual tracker/listener and RPC/Electrum clients.
    // Most cases use InMemory for orchestration; the payment persistence cases
    // supply PostgreSQL for real invoice/payment queries and duplicate handling.
    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));
        private readonly TcpListener _server = new(IPAddress.Loopback, 0);
        private readonly Task _serverTask;
        private readonly HttpClient _http;
        private readonly MemoryCache _settingsCache = new(new MemoryCacheOptions());
        private readonly EventAggregator _events = new(new Logs());
        private readonly DatabaseFactory _database;
        private readonly FlorestaSettings _settings;
        private readonly FlorestaDescriptorService _descriptors;
        private readonly ApplicationDbContextFactory? _invoiceDatabase;
        private readonly BitcoinLikePaymentHandler? _paymentHandler;
        private readonly BTCPayNetwork _bitcoinNetwork;
        private readonly HttpClient? _walletHttp;
        private readonly IDisposable? _invoiceEvents;
        private readonly IDisposable? _chainEvents;
        private int _receivedPayments;
        private int _onChainEvents;
        public int ReceivedPayments => Volatile.Read(ref _receivedPayments);
        public int OnChainEvents => Volatile.Read(ref _onChainEvents);
        public NBXplorer.Models.TransactionResult? LastOnChainTransaction { get; private set; }
        public DatabaseFactory Database => _database;
        private int _blockchainInfoCalls;
        public Func<int, HttpResponseMessage> BlockchainInfo { get; set; } = _ => Info(false);
        public Func<CancellationToken, Task<HttpResponseMessage>> Rescan { get; set; } = _ => Task.FromResult(Json(true));
        public int RescanRequests { get; private set; }
        public JsonElement[] LastRescanParameters { get; private set; } = [];
        public bool FailNextDescriptorLoad { get; set; }
        public bool FailNextDescriptorList { get; set; }
        public Func<Task> BeforeDescriptorLoad { get; set; } = () => Task.CompletedTask;
        public Func<Task> BeforeSubscription { get; set; } = () => Task.CompletedTask;
        public HashSet<string> LoadedDescriptors { get; } = new();
        public Func<string, object[]> HistoryResponse { get; set; } = _ => Array.Empty<object>();
        public Func<string, object[]> UnspentResponse { get; set; } = _ => Array.Empty<object>();
        public Func<string, object> BalanceResponse { get; set; } = _ => new { confirmed = 0, unconfirmed = 0 };
        public Dictionary<string, string> RawTransactions { get; } = new();
        public HashSet<string> FailedHistoryScripts { get; } = new();
        public HashSet<string> FailedTransactions { get; } = new();
        public int DescriptorChecks { get; private set; }
        public int HistoryRequests { get; private set; }
        public int SubscriptionRequests { get; private set; }
        public FlorestaElectrumClient Electrum { get; }
        public FlorestaWalletTracker Tracker { get; }
        public FlorestaListener Listener { get; }
        public FlorestaStatusMonitor Monitor { get; }
        public CancellationToken Token => _timeout.Token;

        public Task<int?> InitializeAsync() => EnvironmentVariableTestHelper.WithBackendReplacementEnvironmentAsync(
            "true", () => Listener.InitializeListenerAsync(Token));

        public async Task RetryUntilRescanRequestedAsync(string wallet)
        {
            // A tracking job returns after preparation and keeps synchronizing
            // in the background. Later loop passes may join that existing job.
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            do
            {
                Assert.Null(await InitializeAsync());
                if ((await GetWalletAsync(wallet)).RescanStatus == FlorestaWalletRescanStatus.Requested)
                    return;
                await Task.Delay(10, Token);
            } while (DateTimeOffset.UtcNow < deadline);
            Assert.Equal(FlorestaWalletRescanStatus.Requested, (await GetWalletAsync(wallet)).RescanStatus);
        }

        public async Task RetryUntilWalletReconciledAsync()
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while ((!await CachedOutputIsSpentAsync() || Tracker.HasDeferredWalletSynchronizations) &&
                   DateTimeOffset.UtcNow < deadline)
            {
                await InitializeAsync();
                await Task.Delay(10, Token);
            }
            Assert.True(await CachedOutputIsSpentAsync());
            Assert.False(Tracker.HasDeferredWalletSynchronizations);
        }

        public Harness(DatabaseFactory? database = null, ApplicationDbContextFactory? invoices = null)
        {
            _database = database ?? new DatabaseFactory();
            _server.Start();
            _serverTask = ServeElectrumAsync();
            var settings = new FlorestaSettings
            {
                Network = "regtest", ElectrumHost = "127.0.0.1",
                ElectrumPort = ((IPEndPoint)_server.LocalEndpoint).Port,
                ElectrumUseTls = false, RpcUrl = "http://floresta.test",
                AutoRegisterDescriptors = true, AutoRescanOnNewDescriptor = false, GapLimit = 2,
                Enabled = true, UseFlorestaAsBitcoinBackend = true
            };
            _settings = settings;
            _settingsCache.Set("SettingsRepository_" + SettingsRepository.KeyNameByType(typeof(FlorestaSettings)),
                Newtonsoft.Json.JsonConvert.SerializeObject(settings));
            var settingsRepository = new SettingsRepository(null!, _events, _settingsCache);
            _http = new HttpClient(new RpcHandler(HandleRpc));
            var rpc = new FlorestaRpcClient(settings, NullLogger<FlorestaRpcClient>.Instance, _http);
            Electrum = new FlorestaElectrumClient(settings, NullLogger<FlorestaElectrumClient>.Instance);
            var nbx = new NBXplorerNetworkProvider(ChainName.Regtest);
            var btc = new BTCPayNetwork
            {
                CryptoCode = "BTC", NBXplorerNetwork = nbx.GetFromCryptoCode("BTC"), CoinType = new KeyPath("1'")
            }.SetDefaultElectrumMapping(ChainName.Regtest);
            _bitcoinNetwork = btc;
            var networks = new BTCPayNetworkProvider(new BTCPayNetworkBase[] { btc }, nbx, new Logs());
            var descriptors = new FlorestaDescriptorService(networks);
            _descriptors = descriptors;
            Tracker = new FlorestaWalletTracker(Electrum, null!, rpc, descriptors,
                new FlorestaDescriptorRegistry(descriptors, rpc), _database, settingsRepository,
                networks, NullLogger<FlorestaWalletTracker>.Instance);
            Monitor = new FlorestaStatusMonitor(Electrum, rpc, settingsRepository, null!,
                new NBXplorerDashboard(), networks, _events, NullLogger<FlorestaStatusMonitor>.Instance);
            BTCPayWalletProvider? wallets = null;
            InvoiceRepository? repository = null;
            PaymentService? payments = null;
            PaymentMethodHandlerDictionary? handlers = null;
            _invoiceDatabase = invoices;
            if (invoices is not null)
            {
                var dashboard = new NBXplorerDashboard();
                _walletHttp = new HttpClient(new FlorestaHttpHandler(Tracker, settingsRepository,
                    networks, Monitor, NullLogger<FlorestaHttpHandler>.Instance));
                var explorer = btc.NBXplorerNetwork.CreateExplorerClient(new Uri("http://floresta.test"));
                explorer.SetClient(_walletHttp);
                explorer.SetNoAuth();
                var clients = new FixtureExplorerProvider(networks, dashboard, explorer);
                wallets = new BTCPayWalletProvider(clients, Options.Create(new MemoryCacheOptions()),
                    invoices, networks, null!, null!, dashboard, new Logs());
                _paymentHandler = new BitcoinLikePaymentHandler(PaymentTypes.CHAIN.GetPaymentMethodId("BTC"),
                    clients, btc, null!, null!, dashboard, null!, wallets);
                handlers = new PaymentMethodHandlerDictionary(new[] { _paymentHandler });
                repository = new InvoiceRepository(invoices, _events);
                payments = new PaymentService(_events, invoices, handlers, repository);
                _invoiceEvents = _events.Subscribe<InvoiceEvent>(evt =>
                {
                    if (evt.Name == InvoiceEvent.ReceivedPayment)
                        Interlocked.Increment(ref _receivedPayments);
                });
                _chainEvents = _events.Subscribe<NewOnChainTransactionEvent>(evt =>
                {
                    LastOnChainTransaction = evt.NewTransactionEvent.TransactionData;
                    Interlocked.Increment(ref _onChainEvents);
                });
            }
            Listener = new FlorestaListener(Electrum, Tracker, Monitor, settingsRepository,
                wallets!, repository!, _events, payments!, handlers!, networks, NullLogger<FlorestaListener>.Instance);
        }

        public async Task<string> CreateInvoiceAsync(TrackedAddress address)
        {
            var handler = _paymentHandler!;
            var pmi = PaymentTypes.CHAIN.GetPaymentMethodId("BTC");
            var invoice = new InvoiceRepository(_invoiceDatabase!, _events).CreateNewInvoice("recovery-store");
            invoice.Currency = "BTC";
            invoice.Price = 0.00001234m;
            invoice.SpeedPolicy = SpeedPolicy.LowSpeed;
            invoice.InvoiceTime = DateTimeOffset.UtcNow;
            invoice.ExpirationTime = invoice.InvoiceTime.AddHours(1);
            invoice.MonitoringExpiration = invoice.ExpirationTime.AddHours(1);
            invoice.SetPaymentPrompt(pmi, new PaymentPrompt
            {
                Currency = "BTC", Divisibility = 8, Destination = address.Address,
                Details = Newtonsoft.Json.Linq.JObject.FromObject(new BitcoinPaymentPromptDetails
                {
                    AccountDerivation = new NBXplorer.DerivationStrategy.DerivationStrategyFactory(Network.RegTest)
                        .Parse(address.WalletId)
                }, handler.Serializer)
            });
            await using var ctx = _invoiceDatabase!.CreateContext();
            await ctx.Database.EnsureCreatedAsync(Token);
            if (!await ctx.Stores.AnyAsync(s => s.Id == invoice.StoreId, Token))
                ctx.Stores.Add(new StoreData { Id = invoice.StoreId, StoreName = "Recovery fixture", StoreBlob = "{}" });
            var row = new InvoiceData { Id = invoice.Id, StoreDataId = invoice.StoreId, Status = "New", ExceptionStatus = "None" };
            row.SetBlob(invoice);
            ctx.Invoices.Add(row);
            ctx.AddressInvoices.Add(new AddressInvoiceData
            {
                InvoiceDataId = invoice.Id, PaymentMethodId = pmi.ToString(),
                Address = _bitcoinNetwork.GetTrackedDestination(new Script(address.ScriptPubKey))
            });
            await ctx.SaveChangesAsync(Token);
            return invoice.Id;
        }

        public Task ImportWalletAsync(string wallet) => EnvironmentVariableTestHelper.WithBackendReplacementEnvironmentAsync(
            "true", async () =>
            {
                using var response = await _walletHttp!.PostAsync(
                    $"http://floresta.test/v1/cryptos/BTC/derivations/{Uri.EscapeDataString(wallet)}", null, Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                return true;
            });

        public Task RunUntilPaymentAsync() => EnvironmentVariableTestHelper.WithBackendReplacementEnvironmentAsync(
            "true", async () =>
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                await Listener.StartAsync(Token);
                try
                {
                    while (ReceivedPayments == 0 || !Tracker.RecoveredTransactions.IsEmpty)
                        await Task.Delay(10, deadline.Token);
                }
                finally { await Listener.StopAsync(Token); }
                return true;
            });

        public async Task AddCachedWalletAsync()
        {
            await using var ctx = _database.CreateContext();
            ctx.TrackedWallets.Add(new TrackedWallet { Id = "wallet", CryptoCode = "BTC" });
            ctx.TrackedAddresses.Add(new TrackedAddress
            {
                WalletId = "wallet", Scripthash = "known", Address = "known", ScriptPubKey = Array.Empty<byte>()
            });
            ctx.Utxos.Add(new TrackedUtxo
            {
                WalletId = "wallet", Scripthash = "known", Txid = uint256.One.ToString(),
                Outpoint = uint256.One + ":0", ScriptPubKey = Array.Empty<byte>(), Value = 1000
            });
            await ctx.SaveChangesAsync(Token);
        }

        public async Task AddCachedOutputAsync(TrackedAddress address)
        {
            await using var ctx = _database.CreateContext();
            ctx.Utxos.Add(new TrackedUtxo
            {
                WalletId = address.WalletId, Scripthash = address.Scripthash,
                Txid = uint256.One.ToString(), Outpoint = uint256.One + ":0",
                ScriptPubKey = address.ScriptPubKey, KeyPath = address.KeyPath, BlockHeight = 80, Value = 1000
            });
            await ctx.SaveChangesAsync(Token);
        }

        public Transaction SetRecoveredHistory(TrackedAddress address, bool alreadySpent)
        {
            var tx = Network.RegTest.CreateTransaction();
            tx.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
            tx.Outputs.Add(Money.Satoshis(1234), new Script(address.ScriptPubKey));
            var txid = tx.GetHash().ToString();
            RawTransactions[txid] = tx.ToHex();
            HistoryResponse = script => script == address.Scripthash
                ? new object[] { new { tx_hash = txid, height = 80 } } : Array.Empty<object>();
            UnspentResponse = script => !alreadySpent && script == address.Scripthash
                ? new object[] { new { tx_hash = txid, tx_pos = 0, value = 1234, height = 80 } } : Array.Empty<object>();
            BalanceResponse = script => new { confirmed = !alreadySpent && script == address.Scripthash ? 1234 : 0, unconfirmed = 0 };
            return tx;
        }

        public void UpdateSettings(Action<FlorestaSettings> update)
        {
            update(_settings);
            _settingsCache.Set("SettingsRepository_" + SettingsRepository.KeyNameByType(typeof(FlorestaSettings)),
                Newtonsoft.Json.JsonConvert.SerializeObject(_settings));
        }

        public FlorestaWalletTracker.WalletPreparationContext CapturePreparation(string wallet)
        {
            var settings = Newtonsoft.Json.JsonConvert.DeserializeObject<FlorestaSettings>(
                Newtonsoft.Json.JsonConvert.SerializeObject(_settings))!;
            return new(settings, new FlorestaDescriptorRegistrationResult(
                _descriptors.CreateDescriptors("BTC", wallet), 0, 0, null), null);
        }

        public async Task<bool> CachedOutputIsSpentAsync()
        {
            await using var ctx = _database.CreateContext();
            return (await ctx.Utxos.SingleAsync(Token)).IsSpent;
        }

        public static HttpResponseMessage Info(bool ibd) => Json(new
        {
            chain = "regtest", blocks = 90, headers = 100, initialblockdownload = ibd
        });

        public async Task<TrackedWallet> GetWalletAsync(string wallet)
        {
            await using var ctx = _database.CreateContext();
            return await ctx.TrackedWallets.SingleAsync(w => w.Id == wallet, Token);
        }

        public static HttpResponseMessage RpcError(int code) => new(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { error = new { code, message = "rescan rejected" } }))
        };

        public static HttpResponseMessage Json(object? result) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { result }))
        };

        private async Task<HttpResponseMessage> HandleRpc(JsonElement request, CancellationToken ct)
        {
            switch (request.GetProperty("method").GetString())
            {
                case "getblockchaininfo": return BlockchainInfo(++_blockchainInfoCalls);
                case "listdescriptors":
                    if (FailNextDescriptorList)
                    {
                        FailNextDescriptorList = false;
                        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    }
                    DescriptorChecks++; return Json(LoadedDescriptors);
                case "loaddescriptor":
                    await BeforeDescriptorLoad();
                    if (FailNextDescriptorLoad)
                    {
                        FailNextDescriptorLoad = false;
                        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    }
                    LoadedDescriptors.Add(request.GetProperty("params")[0].GetString()!);
                    return Json(true);
                case "rescanblockchain":
                    RescanRequests++;
                    var parameters = new List<JsonElement>();
                    foreach (var parameter in request.GetProperty("params").EnumerateArray())
                        parameters.Add(parameter.Clone());
                    LastRescanParameters = parameters.ToArray();
                    return await Rescan(ct);
                default: throw new InvalidOperationException("Unexpected RPC in initialization test");
            }
        }

        private async Task ServeElectrumAsync()
        {
            try
            {
                while (!Token.IsCancellationRequested)
                {
                    using var client = await _server.AcceptTcpClientAsync(Token);
                    using var reader = new StreamReader(client.GetStream());
                    await using var writer = new StreamWriter(client.GetStream()) { AutoFlush = true };
                    while (await reader.ReadLineAsync(Token) is { } line)
                    {
                        using var request = JsonDocument.Parse(line);
                        if (request.RootElement.GetProperty("method").GetString() == "blockchain.scripthash.subscribe")
                            await BeforeSubscription();
                        var historyFailed = request.RootElement.GetProperty("method").GetString() == "blockchain.scripthash.get_history" &&
                            FailedHistoryScripts.Contains(request.RootElement.GetProperty("params")[0].GetString()!);
                        var transactionFailed = request.RootElement.GetProperty("method").GetString() == "blockchain.transaction.get" &&
                            FailedTransactions.Contains(request.RootElement.GetProperty("params")[0].GetString()!);
                        object? result = request.RootElement.GetProperty("method").GetString() switch
                        {
                            "server.ping" => null,
                            "server.version" => new[] { "floresta-test", "1.4" },
                            "server.features" => new { genesis_hash = Network.RegTest.GenesisHash.ToString() },
                            "blockchain.headers.subscribe" => new { height = 100, hex = Convert.ToHexString(Network.RegTest.GetGenesis().Header.ToBytes()) },
                            "blockchain.scripthash.subscribe" => Subscribe(),
                            "blockchain.scripthash.get_history" => historyFailed ? null : History(request.RootElement.GetProperty("params")[0].GetString()!),
                            "blockchain.scripthash.listunspent" => UnspentResponse(request.RootElement.GetProperty("params")[0].GetString()!),
                            "blockchain.scripthash.get_balance" => BalanceResponse(request.RootElement.GetProperty("params")[0].GetString()!),
                            "blockchain.transaction.get" => transactionFailed ? null : RawTransactions[request.RootElement.GetProperty("params")[0].GetString()!],
                            _ => throw new InvalidOperationException("Unexpected Electrum request in initialization test")
                        };
                        await writer.WriteLineAsync(JsonSerializer.Serialize(new
                        {
                            jsonrpc = "2.0", id = request.RootElement.GetProperty("id").GetInt32(), result,
                            error = historyFailed || transactionFailed ? new { code = -32000, message = "Electrum result temporarily unavailable" } : null
                        }));
                    }
                }
            }
            catch (OperationCanceledException) when (Token.IsCancellationRequested) { }
        }

        private object[] History(string script)
        {
            HistoryRequests++;
            return HistoryResponse(script);
        }

        private object? Subscribe()
        {
            SubscriptionRequests++;
            return null;
        }

        public async ValueTask DisposeAsync()
        {
            await Tracker.StopAsync(CancellationToken.None);
            await _timeout.CancelAsync();
            await Electrum.DisposeAsync();
            await _serverTask;
            _server.Stop();
            _http.Dispose();
            _walletHttp?.Dispose();
            _invoiceEvents?.Dispose();
            _chainEvents?.Dispose();
            _events.Dispose();
            _settingsCache.Dispose();
            _timeout.Dispose();
        }
    }

    private sealed class RpcHandler(Func<JsonElement, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            return await respond(json.RootElement, ct);
        }
    }

    private sealed class DatabaseFactory() : FlorestaDbContextFactory(Options.Create(new DatabaseOptions()))
    {
        private readonly DbContextOptions<FlorestaDbContext> _options =
            new DbContextOptionsBuilder<FlorestaDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

        public override FlorestaDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptionsAction = null) => new(_options);
    }

    private sealed class FixtureExplorerProvider : BTCPayServer.ExplorerClientProvider
    {
        public FixtureExplorerProvider(BTCPayNetworkProvider networks, NBXplorerDashboard dashboard, ExplorerClient client)
            : base(networks, dashboard) => _Clients["BTC"] = client;
    }

    private sealed class InvoiceDatabase() : ApplicationDbContextFactory(Options.Create(new DatabaseOptions()), NullLoggerFactory.Instance)
    {
        private readonly DbContextOptions<ApplicationDbContext> _options =
            new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        private int _contextsCreated;
        public int ContextsCreated => Volatile.Read(ref _contextsCreated);
        public override ApplicationDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder>? configure = null)
        {
            Interlocked.Increment(ref _contextsCreated);
            return new(_options);
        }
    }
}
