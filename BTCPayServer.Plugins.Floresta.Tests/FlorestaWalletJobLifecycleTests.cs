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
    public async Task CancellationRequestedByCompletedScanBodyIsNotReportedAsComplete()
    {
        var info = CreateScanInfo();
        using var cts = new CancellationTokenSource();

        await FlorestaWalletTracker.RunScanAsync(info, _ =>
        {
            cts.Cancel();
            return Task.CompletedTask;
        }, cts.Token);

        Assert.Equal(ScanUTXOStatus.Error, info.Status);
        Assert.Equal(FlorestaWalletTracker.ScanCanceledError, info.Error);
        Assert.NotEqual(100, info.Progress.OverallProgress);
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
