using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Logging;
using BTCPayServer.Plugins.Floresta.Data;
using BTCPayServer.Plugins.Floresta.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NBXplorer;
using NBXplorer.DerivationStrategy;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests;

public class FlorestaConcurrencyTests
{
    [Fact]
    public async Task WalletLockSerializesActionsForSameWallet()
    {
        var sync = new FlorestaWalletSync(
            null!,
            null!,
            null!,
            null!,
            new ConcurrentDictionary<string, DerivationStrategyBase>(),
            () => 0,
            Network.RegTest,
            NullLogger.Instance);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var first = sync.RunWithWalletLockAsync("wallet", async () =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task.WaitAsync(cts.Token);
        }, CancellationToken.None);
        await firstEntered.Task.WaitAsync(cts.Token);

        var second = sync.RunWithWalletLockAsync("wallet", () =>
        {
            secondEntered.SetResult();
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.False(secondEntered.Task.IsCompleted);
        Assert.False(second.IsCompleted);

        releaseFirst.SetResult();
        await Task.WhenAll(first, second).WaitAsync(cts.Token);
        Assert.True(secondEntered.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public void StatusMonitorKeepsTipHeightMonotonic()
    {
        var monitor = CreateStatusMonitor();

        monitor.UpdateTipHeight(12);
        monitor.UpdateTipHeight(10);

        Assert.Equal(12, monitor.TipHeight);
    }

    [Fact]
    public void StatusMonitorAcceptsAuthoritativeTipHeightDecrease()
    {
        var monitor = CreateStatusMonitor();

        monitor.SetTipHeight(12);
        monitor.SetTipHeight(10);

        Assert.Equal(10, monitor.TipHeight);
    }

    [Fact]
    public void StatusMonitorDoesNotApplyChainStateFromMismatchedNetwork()
    {
        var monitor = CreateStatusMonitor(ChainName.Regtest);
        monitor.SetTipHeight(12);

        var applied = monitor.TryApplyBackendInfo(
            new FlorestaChainInfo(
                Height: 999,
                IsInitialBlockDownload: false,
                ValidatedHeight: 998,
                Chain: "bitcoin"),
            CreateServerFeatures(monitor.ExpectedNetwork),
            connectionGeneration: 0,
            out var error);

        Assert.False(applied);
        Assert.Contains("expects Regtest", error);
        Assert.Equal(12, monitor.TipHeight);
        Assert.Null(monitor.IsInitialBlockDownload);
        Assert.Null(monitor.ValidatedHeight);
        Assert.Equal("bitcoin", monitor.ChainInfo.Chain);
        Assert.False(monitor.IsNetworkCompatible);

        var health = monitor.GetHealthSnapshot();
        Assert.Equal("bitcoin", health.RpcNetwork);
        Assert.Equal(999, health.Height);
    }

    [Fact]
    public void StatusMonitorAppliesChainStateFromMatchingNetwork()
    {
        var monitor = CreateStatusMonitor(ChainName.Regtest);

        var applied = monitor.TryApplyBackendInfo(
            new FlorestaChainInfo(
                Height: 12,
                IsInitialBlockDownload: false,
                ValidatedHeight: 11,
                Chain: "regtest"),
            CreateServerFeatures(monitor.ExpectedNetwork),
            connectionGeneration: 0,
            out var error);

        Assert.True(applied);
        Assert.Null(error);
        Assert.Equal(12, monitor.TipHeight);
        Assert.False(monitor.IsInitialBlockDownload);
        Assert.Equal(11, monitor.ValidatedHeight);
    }

    [Fact]
    public void StatusMonitorDoesNotApplyChainStateFromMismatchedElectrumNetwork()
    {
        var monitor = CreateStatusMonitor(ChainName.Regtest);
        monitor.SetTipHeight(12);

        var applied = monitor.TryApplyBackendInfo(
            new FlorestaChainInfo(
                Height: 999,
                IsInitialBlockDownload: false,
                ValidatedHeight: 998,
                Chain: "regtest"),
            CreateServerFeatures(Network.Main),
            connectionGeneration: 0,
            out var error);

        Assert.False(applied);
        Assert.Contains("Electrum network mismatch", error);
        Assert.Equal(12, monitor.TipHeight);
        Assert.Null(monitor.IsInitialBlockDownload);
        Assert.Null(monitor.ValidatedHeight);
        Assert.False(monitor.IsNetworkCompatible);
    }

    [Fact]
    public void StatusMonitorFailsClosedWhenBtcNetworkIsMissing()
    {
        var nbxplorerNetworkProvider = new NBXplorerNetworkProvider(ChainName.Regtest);
        var networkProvider = new BTCPayNetworkProvider(
            Array.Empty<BTCPayNetworkBase>(),
            nbxplorerNetworkProvider,
            new Logs());
        var monitor = CreateStatusMonitor(networkProvider);

        var exception = Assert.Throws<InvalidOperationException>(() => _ = monitor.ExpectedChainName);

        Assert.Contains("does not have a BTC network configured", exception.Message);
    }

    [Fact]
    public void ConfirmationsNeverGoNegativeWhenTipDropsBelowCachedBlock()
    {
        Assert.Equal(0, FlorestaWalletTracker.GetConfirmations(12, 10));
        Assert.Equal(1, FlorestaWalletTracker.GetConfirmations(10, 10));
        Assert.Equal(3, FlorestaWalletTracker.GetConfirmations(8, 10));
        Assert.Equal(0, FlorestaWalletTracker.GetConfirmations(0, 10));
        Assert.Equal(0, FlorestaWalletTracker.GetConfirmations(null, 10));
    }

    [Fact]
    public void RemovedTransactionReportsNegativeConfirmation()
    {
        var tx = new TrackedTransaction { BlockHeight = null, RemovedAt = DateTimeOffset.UtcNow };

        Assert.Equal(-1, FlorestaWalletTracker.GetTransactionConfirmations(tx, 10));
    }

    [Fact]
    public void ConfirmedHeightIsZeroForUnconfirmedResponses()
    {
        Assert.Equal(0, FlorestaWalletTracker.GetConfirmedHeight(12, 0));
        Assert.Equal(12, FlorestaWalletTracker.GetConfirmedHeight(12, 1));
        Assert.Equal(0, FlorestaWalletTracker.GetConfirmedHeight(0, 1));
        Assert.Equal(0, FlorestaWalletTracker.GetConfirmedHeight(null, 1));
    }

    [Fact]
    public void HistoryBlockHeightDemotesCachedTransactionWhenUnconfirmed()
    {
        var tx = new TrackedTransaction { BlockHeight = 12 };

        FlorestaWalletSync.ApplyHistoryBlockHeight(tx, 0);

        Assert.Null(tx.BlockHeight);
    }

    [Fact]
    public void HistoryBlockHeightUpdatesCachedTransactionWhenConfirmed()
    {
        var tx = new TrackedTransaction { BlockHeight = null };

        FlorestaWalletSync.ApplyHistoryBlockHeight(tx, 12);

        Assert.Equal(12, tx.BlockHeight);
    }

    [Fact]
    public void HistoryBlockHeightClearsRemovalStateWhenTransactionReappears()
    {
        var tx = new TrackedTransaction
        {
            BlockHeight = null,
            RemovedAt = DateTimeOffset.UtcNow
        };

        FlorestaWalletSync.ApplyHistoryBlockHeight(tx, 0);

        Assert.Null(tx.BlockHeight);
        Assert.Null(tx.RemovedAt);
    }

    [Fact]
    public void MissingHistoryMarksActiveTransactionsRemoved()
    {
        var present = new TrackedTransaction { Txid = "present", BlockHeight = 12 };
        var missing = new TrackedTransaction { Txid = "missing", BlockHeight = 12 };
        var unconfirmed = new TrackedTransaction { Txid = "unconfirmed", BlockHeight = null };
        var alreadyRemoved = new TrackedTransaction { Txid = "removed", RemovedAt = DateTimeOffset.UtcNow };

        var removed = FlorestaWalletSync.MarkTransactionsMissingFromHistory(
            new[] { present, missing, unconfirmed, alreadyRemoved },
            new HashSet<string> { "present" },
            syncComplete: true);

        Assert.Equal(2, removed);
        Assert.Equal(12, present.BlockHeight);
        Assert.NotNull(missing.RemovedAt);
        Assert.NotNull(unconfirmed.RemovedAt);
        Assert.NotNull(alreadyRemoved.RemovedAt);
    }

    [Fact]
    public void IncompleteSyncDoesNotMarkTransactionsAsRemoved()
    {
        var missing = new TrackedTransaction { Txid = "missing", BlockHeight = 12 };
        var unconfirmed = new TrackedTransaction { Txid = "unconfirmed", BlockHeight = null };

        var removed = FlorestaWalletSync.MarkTransactionsMissingFromHistory(
            new[] { missing, unconfirmed },
            new HashSet<string>(),
            syncComplete: false);

        Assert.Equal(0, removed);
        Assert.Null(missing.RemovedAt);
        Assert.Null(unconfirmed.RemovedAt);
    }

    private static FlorestaStatusMonitor CreateStatusMonitor(ChainName? chainName = null)
    {
        return CreateStatusMonitor(chainName is null ? null! : CreateNetworkProvider(chainName));
    }

    private static FlorestaStatusMonitor CreateStatusMonitor(BTCPayNetworkProvider networkProvider)
    {
        return new FlorestaStatusMonitor(
            null!,
            null!,
            null!,
            null!,
            null!,
            networkProvider,
            null!,
            NullLogger<FlorestaStatusMonitor>.Instance);
    }

    private static BTCPayNetworkProvider CreateNetworkProvider(ChainName chainName)
    {
        var nbxplorerNetworkProvider = new NBXplorerNetworkProvider(chainName);
        var nbxplorerNetwork = nbxplorerNetworkProvider.GetFromCryptoCode("BTC");
        var bitcoinNetwork = new BTCPayNetwork
        {
            CryptoCode = nbxplorerNetwork.CryptoCode,
            DisplayName = "Bitcoin",
            NBXplorerNetwork = nbxplorerNetwork,
            CoinType = chainName == ChainName.Mainnet ? new KeyPath("0'") : new KeyPath("1'")
        }.SetDefaultElectrumMapping(chainName);

        return new BTCPayNetworkProvider(
            new BTCPayNetworkBase[] { bitcoinNetwork },
            nbxplorerNetworkProvider,
            new Logs());
    }

    private static JsonElement CreateServerFeatures(Network network)
    {
        return JsonSerializer.SerializeToElement(new
        {
            genesis_hash = network.GetGenesis().GetHash().ToString()
        });
    }
}
