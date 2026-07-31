using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Logging;
using BTCPayServer.Plugins.Floresta.Data;
using BTCPayServer.Plugins.Floresta.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NBXplorer;
using NBXplorer.Models;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests;

public class FlorestaWalletJobLifecycleTests
{
    [Fact]
    public async Task ConcurrentTrackingRequestsSharePreparation()
    {
        var tracker = CreateTracker();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var syncEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var preparationCalls = 0;
        var syncCalls = 0;

        Task PrepareAndStart(CancellationToken ct)
        {
            return tracker.PrepareAndStartTrackingAsync(
                "wallet",
                async token =>
                {
                    Interlocked.Increment(ref preparationCalls);
                    entered.TrySetResult();
                    await release.Task.WaitAsync(token);
                    return new List<TrackedAddress>();
                },
                (_, _) =>
                {
                    Interlocked.Increment(ref syncCalls);
                    syncEntered.TrySetResult();
                    return Task.CompletedTask;
                },
                ct);
        }

        var first = PrepareAndStart(timeout.Token);
        await entered.Task.WaitAsync(timeout.Token);
        var second = PrepareAndStart(timeout.Token);

        Assert.Equal(1, Volatile.Read(ref preparationCalls));
        release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(timeout.Token);
        await syncEntered.Task.WaitAsync(timeout.Token);
        Assert.Equal(1, Volatile.Read(ref preparationCalls));
        Assert.Equal(1, Volatile.Read(ref syncCalls));

        await tracker.StopAsync(timeout.Token);
    }

    [Fact]
    public async Task CanceledTrackingRequestDoesNotCancelSharedPreparation()
    {
        var tracker = CreateTracker();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var syncEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var ownerCanceled = new CancellationTokenSource();
        var preparationCalls = 0;
        var syncCalls = 0;

        Task PrepareAndStart(CancellationToken ct)
        {
            return tracker.PrepareAndStartTrackingAsync(
                "wallet",
                async token =>
                {
                    Interlocked.Increment(ref preparationCalls);
                    entered.TrySetResult();
                    await release.Task.WaitAsync(token);
                    return new List<TrackedAddress>();
                },
                async (_, token) =>
                {
                    Interlocked.Increment(ref syncCalls);
                    syncEntered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                },
                ct);
        }

        var canceledRequest = PrepareAndStart(ownerCanceled.Token);
        await entered.Task.WaitAsync(timeout.Token);
        var healthyRequest = PrepareAndStart(timeout.Token);

        ownerCanceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledRequest);
        release.SetResult();

        await healthyRequest.WaitAsync(timeout.Token);
        await syncEntered.Task.WaitAsync(timeout.Token);
        Assert.Equal(1, Volatile.Read(ref preparationCalls));
        Assert.Equal(1, Volatile.Read(ref syncCalls));

        await tracker.StopAsync(timeout.Token);
    }

    [Fact]
    public async Task ShutdownCancelsTrackingPreparation()
    {
        var tracker = CreateTracker();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var tracking = tracker.PrepareAndStartTrackingAsync(
            "wallet",
            async token =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new List<TrackedAddress>();
            },
            (_, _) => throw new InvalidOperationException("Sync should not start."),
            timeout.Token);
        await entered.Task.WaitAsync(timeout.Token);

        var stopping = tracker.StopAsync(timeout.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tracking);
        await stopping;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentTrackingAndScanSharePreparationWithoutSerializingTails(
        bool trackingStartsFirst)
    {
        var tracker = CreateTracker();
        var preparationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var trackingWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePreparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var trackingTailEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanTailEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var trackingSyncEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTails = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var preparationCalls = 0;
        var trackingTailCalls = 0;
        var trackingSyncCalls = 0;
        var scanTailCalls = 0;

        async Task<FlorestaWalletTracker.WalletPreparationContext> Prepare(CancellationToken token)
        {
            Interlocked.Increment(ref preparationCalls);
            preparationEntered.TrySetResult();
            await releasePreparation.Task.WaitAsync(token);
            return CreateWalletPreparation();
        }

        Task StartTracking()
        {
            return tracker.PrepareAndStartTrackingAsync(
                "wallet",
                async token =>
                {
                    var shared = tracker.GetOrStartWalletPreparationAsync("wallet", Prepare, token);
                    trackingWaiting.TrySetResult();
                    await shared;
                    Interlocked.Increment(ref trackingTailCalls);
                    trackingTailEntered.TrySetResult();
                    await releaseTails.Task.WaitAsync(token);
                    return new List<TrackedAddress>();
                },
                (_, _) =>
                {
                    Interlocked.Increment(ref trackingSyncCalls);
                    trackingSyncEntered.TrySetResult();
                    return Task.CompletedTask;
                },
                timeout.Token);
        }

        bool StartScan()
        {
            return tracker.StartScan("wallet", 25, 0, async (_, token) =>
            {
                var shared = tracker.GetOrStartWalletPreparationAsync("wallet", Prepare, token);
                scanWaiting.TrySetResult();
                await shared;
                Interlocked.Increment(ref scanTailCalls);
                scanTailEntered.TrySetResult();
                await releaseTails.Task.WaitAsync(token);
            });
        }

        Task tracking;
        if (trackingStartsFirst)
        {
            tracking = StartTracking();
            await preparationEntered.Task.WaitAsync(timeout.Token);
            Assert.True(StartScan());
        }
        else
        {
            Assert.True(StartScan());
            await preparationEntered.Task.WaitAsync(timeout.Token);
            tracking = StartTracking();
        }

        await Task.WhenAll(trackingWaiting.Task, scanWaiting.Task).WaitAsync(timeout.Token);
        Assert.Equal(1, Volatile.Read(ref preparationCalls));

        releasePreparation.SetResult();
        await Task.WhenAll(trackingTailEntered.Task, scanTailEntered.Task).WaitAsync(timeout.Token);

        Assert.Equal(1, Volatile.Read(ref preparationCalls));
        Assert.Equal(1, Volatile.Read(ref trackingTailCalls));
        Assert.Equal(1, Volatile.Read(ref scanTailCalls));

        releaseTails.SetResult();
        await tracking.WaitAsync(timeout.Token);
        await WaitUntilAsync(
            () => tracker.GetScanState("wallet")?.Status == ScanUTXOStatus.Complete,
            timeout.Token);
        await trackingSyncEntered.Task.WaitAsync(timeout.Token);

        Assert.Equal(1, Volatile.Read(ref trackingSyncCalls));
        await tracker.StopAsync(timeout.Token);
    }

    [Fact]
    public async Task CanceledTrackingWaiterDoesNotCancelPreparationSharedWithScan()
    {
        var tracker = CreateTracker();
        var preparationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePreparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var trackingFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var requestCanceled = new CancellationTokenSource();
        var preparationCalls = 0;

        async Task<FlorestaWalletTracker.WalletPreparationContext> Prepare(CancellationToken token)
        {
            Interlocked.Increment(ref preparationCalls);
            preparationEntered.TrySetResult();
            await releasePreparation.Task.WaitAsync(token);
            return CreateWalletPreparation();
        }

        var tracking = tracker.PrepareAndStartTrackingAsync(
            "wallet",
            async token =>
            {
                await tracker.GetOrStartWalletPreparationAsync("wallet", Prepare, token);
                return new List<TrackedAddress>();
            },
            (_, _) =>
            {
                trackingFinished.TrySetResult();
                return Task.CompletedTask;
            },
            requestCanceled.Token);
        await preparationEntered.Task.WaitAsync(timeout.Token);

        Assert.True(tracker.StartScan("wallet", 25, 0, async (_, token) =>
        {
            var shared = tracker.GetOrStartWalletPreparationAsync("wallet", Prepare, token);
            scanWaiting.TrySetResult();
            await shared;
        }));
        await scanWaiting.Task.WaitAsync(timeout.Token);

        requestCanceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tracking);

        releasePreparation.SetResult();
        await trackingFinished.Task.WaitAsync(timeout.Token);
        await WaitUntilAsync(
            () => tracker.GetScanState("wallet")?.Status == ScanUTXOStatus.Complete,
            timeout.Token);

        Assert.Equal(1, Volatile.Read(ref preparationCalls));
        await tracker.StopAsync(timeout.Token);
    }

    [Fact]
    public async Task SharedPreparationFailureReachesTrackingAndScanAndAllowsRetry()
    {
        var tracker = CreateTracker();
        var preparationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var trackingWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePreparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var expected = new InvalidOperationException("shared preparation failed");
        var preparationCalls = 0;
        var trackingTailCalls = 0;
        var scanTailCalls = 0;

        async Task<FlorestaWalletTracker.WalletPreparationContext> FailPreparation(CancellationToken token)
        {
            Interlocked.Increment(ref preparationCalls);
            preparationEntered.TrySetResult();
            await releasePreparation.Task.WaitAsync(token);
            throw expected;
        }

        var tracking = tracker.PrepareAndStartTrackingAsync(
            "wallet",
            async token =>
            {
                var shared = tracker.GetOrStartWalletPreparationAsync("wallet", FailPreparation, token);
                trackingWaiting.TrySetResult();
                await shared;
                Interlocked.Increment(ref trackingTailCalls);
                return new List<TrackedAddress>();
            },
            (_, _) => Task.CompletedTask,
            timeout.Token);
        await preparationEntered.Task.WaitAsync(timeout.Token);

        Assert.True(tracker.StartScan("wallet", 25, 0, async (_, token) =>
        {
            var shared = tracker.GetOrStartWalletPreparationAsync("wallet", FailPreparation, token);
            scanWaiting.TrySetResult();
            await shared;
            Interlocked.Increment(ref scanTailCalls);
        }));
        await Task.WhenAll(trackingWaiting.Task, scanWaiting.Task).WaitAsync(timeout.Token);

        releasePreparation.SetResult();
        var trackingError = await Assert.ThrowsAsync<InvalidOperationException>(() => tracking);
        await WaitUntilAsync(
            () => tracker.GetScanState("wallet")?.Status == ScanUTXOStatus.Error,
            timeout.Token);

        Assert.Same(expected, trackingError);
        Assert.Equal(expected.Message, tracker.GetScanState("wallet")?.Error);
        Assert.Equal(1, Volatile.Read(ref preparationCalls));
        Assert.Equal(0, Volatile.Read(ref trackingTailCalls));
        Assert.Equal(0, Volatile.Read(ref scanTailCalls));

        var retryCalls = 0;
        var retry = await tracker.GetOrStartWalletPreparationAsync(
            "wallet",
            _ =>
            {
                Interlocked.Increment(ref retryCalls);
                return Task.FromResult(CreateWalletPreparation());
            },
            timeout.Token);

        Assert.NotNull(retry);
        Assert.Equal(1, Volatile.Read(ref retryCalls));
        await tracker.StopAsync(timeout.Token);
    }

    [Fact]
    public async Task DifferentWalletsPrepareInParallel()
    {
        var tracker = CreateTracker();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        async Task<FlorestaWalletTracker.WalletPreparationContext> Prepare(
            TaskCompletionSource entered,
            CancellationToken token)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return CreateWalletPreparation();
        }

        var first = tracker.GetOrStartWalletPreparationAsync(
            "wallet-a",
            token => Prepare(firstEntered, token),
            timeout.Token);
        var second = tracker.GetOrStartWalletPreparationAsync(
            "wallet-b",
            token => Prepare(secondEntered, token),
            timeout.Token);

        await Task.WhenAll(firstEntered.Task, secondEntered.Task).WaitAsync(timeout.Token);
        release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(timeout.Token);

        await tracker.StopAsync(timeout.Token);
    }

    [Fact]
    public async Task ShutdownCancelsAndAwaitsSharedWalletPreparation()
    {
        var tracker = CreateTracker();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var preparation = tracker.GetOrStartWalletPreparationAsync(
            "wallet",
            async token =>
            {
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                finally
                {
                    cancellationObserved.TrySetResult();
                    await releaseCleanup.Task;
                    finished.TrySetResult();
                }

                return CreateWalletPreparation();
            },
            CancellationToken.None);
        await entered.Task.WaitAsync(timeout.Token);

        var stopping = tracker.StopAsync(timeout.Token);

        await cancellationObserved.Task.WaitAsync(timeout.Token);
        try
        {
            Assert.False(stopping.IsCompleted);
            Assert.False(preparation.IsCompleted);
        }
        finally
        {
            releaseCleanup.TrySetResult();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation);
        await stopping;
        Assert.True(finished.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DuplicateScanKeepsExistingStateAndShutdownCancelsIt()
    {
        var tracker = CreateTracker();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        Assert.True(tracker.StartScan("wallet", 25, 0, async (_, ct) =>
        {
            entered.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            finally
            {
                finished.SetResult();
            }
        }));
        await entered.Task.WaitAsync(timeout.Token);
        var originalState = tracker.GetScanState("wallet");

        Assert.False(tracker.StartScan("wallet", 50, 10, (_, _) =>
            throw new InvalidOperationException("Duplicate scan ran.")));
        Assert.Same(originalState, tracker.GetScanState("wallet"));

        await tracker.StopAsync(timeout.Token);

        Assert.True(finished.Task.IsCompletedSuccessfully);
        Assert.Equal(ScanUTXOStatus.Error, originalState.Status);
        Assert.Equal(FlorestaWalletTracker.ScanCanceledError, originalState.Error);
        Assert.NotEqual(100, originalState.Progress.OverallProgress);
        Assert.False(tracker.StartScan("wallet", 25, 0, (_, _) => Task.CompletedTask));
        Assert.Same(originalState, tracker.GetScanState("wallet"));
    }

    [Fact]
    public async Task CompletedScanCanStartAgain()
    {
        var tracker = CreateTracker();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        Assert.True(tracker.StartScan("wallet", 25, 0, (_, _) => Task.CompletedTask));
        await WaitUntilAsync(
            () => tracker.GetScanState("wallet")?.Status == ScanUTXOStatus.Complete,
            timeout.Token);
        var firstState = tracker.GetScanState("wallet");

        var restarted = false;
        while (!restarted)
        {
            timeout.Token.ThrowIfCancellationRequested();
            restarted = tracker.StartScan("wallet", 25, 0, (_, _) => Task.CompletedTask);
            if (!restarted)
                await Task.Delay(10, timeout.Token);
        }

        await WaitUntilAsync(
            () => !ReferenceEquals(firstState, tracker.GetScanState("wallet")) &&
                  tracker.GetScanState("wallet")?.Status == ScanUTXOStatus.Complete,
            timeout.Token);

        await tracker.StopAsync(timeout.Token);
    }

    [Fact]
    public async Task FailedScanCanStartAgain()
    {
        var tracker = CreateTracker();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        Assert.True(tracker.StartScan("wallet", 25, 0, (_, _) =>
            Task.FromException(new InvalidOperationException(
                FlorestaWalletTracker.ScanPartialError,
                new InvalidOperationException("electrum-sensitive-detail")))));
        await WaitUntilAsync(
            () => tracker.GetScanState("wallet")?.Status == ScanUTXOStatus.Error,
            timeout.Token);
        var failedState = tracker.GetScanState("wallet");

        Assert.Equal(FlorestaWalletTracker.ScanPartialError, failedState.Error);
        Assert.DoesNotContain("electrum-sensitive-detail", failedState.Error);
        Assert.NotNull(failedState.Progress.CompletedAt);
        Assert.True(failedState.Progress.OverallProgress < 100);

        var restarted = false;
        while (!restarted)
        {
            timeout.Token.ThrowIfCancellationRequested();
            restarted = tracker.StartScan("wallet", 25, 0, (_, _) => Task.CompletedTask);
            if (!restarted)
                await Task.Delay(10, timeout.Token);
        }

        await WaitUntilAsync(
            () => !ReferenceEquals(failedState, tracker.GetScanState("wallet")) &&
                  tracker.GetScanState("wallet")?.Status == ScanUTXOStatus.Complete,
            timeout.Token);

        await tracker.StopAsync(timeout.Token);
    }

    [Fact]
    public async Task PartialScanResultCapsProgressStoresFoundAndSafeError()
    {
        var info = CreateScanInfo();
        var summary = new FlorestaWalletSync.WalletAddressSyncSummary(
            Attempted: 3,
            Succeeded: 2,
            Failed: 1);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FlorestaWalletTracker.RunScanAsync(info, _ =>
            {
                info.Progress.OverallProgress = 100;
                FlorestaWalletTracker.ApplyScanSyncResult(info, summary, found: 2);
                return Task.CompletedTask;
            }, CancellationToken.None));

        Assert.Equal(FlorestaWalletTracker.ScanPartialError, exception.Message);
        Assert.Equal(ScanUTXOStatus.Error, info.Status);
        Assert.Equal(FlorestaWalletTracker.ScanPartialError, info.Error);
        Assert.DoesNotContain("electrum-sensitive-detail", info.Error);
        Assert.Equal(2, info.Progress.Found);
        Assert.NotNull(info.Progress.CompletedAt);
        Assert.Equal(99, info.Progress.OverallProgress);
    }

    [Fact]
    public async Task CancellationRequestedByCompletedScanBodyIsNotReportedAsComplete()
    {
        var info = CreateScanInfo();
        using var cts = new CancellationTokenSource();

        await FlorestaWalletTracker.RunScanAsync(info, _ =>
        {
            info.Progress.OverallProgress = 100;
            cts.Cancel();
            return Task.CompletedTask;
        }, cts.Token);

        Assert.Equal(ScanUTXOStatus.Error, info.Status);
        Assert.Equal(FlorestaWalletTracker.ScanCanceledError, info.Error);
        Assert.Equal(99, info.Progress.OverallProgress);
    }

    [Fact]
    public async Task UnkeyedBackgroundTaskIsCanceledAndAwaitedOnShutdown()
    {
        var jobs = new FlorestaBackgroundTasks();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        _ = jobs.Run(async ct =>
        {
            entered.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            finally
            {
                finished.SetResult();
            }
        });
        await entered.Task.WaitAsync(timeout.Token);

        await jobs.StopAsync(timeout.Token);

        Assert.True(finished.Task.IsCompletedSuccessfully);
        var ranAfterStop = false;
        await jobs.Run(_ =>
        {
            ranAfterStop = true;
            return Task.CompletedTask;
        });
        Assert.False(ranAfterStop);
    }

    [Fact]
    public async Task ShutdownPropagatesExternalCancellation()
    {
        var jobs = new FlorestaBackgroundTasks();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var job = jobs.Run(async _ =>
        {
            entered.SetResult();
            await release.Task;
        });
        await entered.Task.WaitAsync(timeout.Token);
        using var stopCanceled = new CancellationTokenSource();
        stopCanceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            jobs.StopAsync(stopCanceled.Token));

        release.SetResult();
        await job.WaitAsync(timeout.Token);
    }

    private static ScanUTXOInformation CreateScanInfo()
    {
        return new ScanUTXOInformation
        {
            Status = ScanUTXOStatus.Queued,
            Progress = new ScanUTXOProgress()
        };
    }

    private static FlorestaWalletTracker.WalletPreparationContext CreateWalletPreparation()
    {
        return new FlorestaWalletTracker.WalletPreparationContext(
            new FlorestaSettings(),
            new FlorestaDescriptorRegistrationResult(
                new FlorestaDescriptorSet(
                    "wallet",
                    "descriptor-hash",
                    "receive-descriptor",
                    "change-descriptor"),
                2,
                0,
                null),
            null);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
            await Task.Delay(10, ct);
    }

    private static FlorestaWalletTracker CreateTracker()
    {
        var chainName = ChainName.Regtest;
        var nbxplorerNetworkProvider = new NBXplorerNetworkProvider(chainName);
        var nbxplorerNetwork = nbxplorerNetworkProvider.GetFromCryptoCode("BTC");
        var bitcoinNetwork = new BTCPayNetwork
        {
            CryptoCode = nbxplorerNetwork.CryptoCode,
            DisplayName = "Bitcoin",
            NBXplorerNetwork = nbxplorerNetwork,
            CoinType = new KeyPath("1'")
        }.SetDefaultElectrumMapping(chainName);
        var networkProvider = new BTCPayNetworkProvider(
            new BTCPayNetworkBase[] { bitcoinNetwork },
            nbxplorerNetworkProvider,
            new Logs());

        return new FlorestaWalletTracker(
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            networkProvider,
            NullLogger<FlorestaWalletTracker>.Instance);
    }
}
