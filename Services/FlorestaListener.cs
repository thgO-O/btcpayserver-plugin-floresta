using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Wallets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;

namespace BTCPayServer.Plugins.Floresta.Services;

/// <summary>
/// Replaces NBXplorerListener. Subscribes to Electrum notifications for
/// scripthash changes and new blocks, then matches incoming transactions
/// against open invoices.
/// </summary>
public class FlorestaListener : IHostedService
{
    private readonly FlorestaElectrumClient _electrumClient;
    private readonly FlorestaWalletTracker _tracker;
    private readonly FlorestaStatusMonitor _statusMonitor;
    private readonly SettingsRepository _settingsRepository;
    private readonly BTCPayWalletProvider _walletProvider;
    private readonly InvoiceRepository _invoiceRepository;
    private readonly EventAggregator _eventAggregator;
    private readonly PaymentService _paymentService;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly ILogger<FlorestaListener> _logger;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly object _paymentPollingGate = new();
    private CancellationTokenSource _cts;
    private Task _listenTask;
    private volatile bool _listenerInitialized;
    private long _initializedConnectionGeneration = -1;
    private bool _paymentPollingRunning;
    private bool _paymentPollingPending;

    public FlorestaListener(
        FlorestaElectrumClient electrumClient,
        FlorestaWalletTracker tracker,
        FlorestaStatusMonitor statusMonitor,
        SettingsRepository settingsRepository,
        BTCPayWalletProvider walletProvider,
        InvoiceRepository invoiceRepository,
        EventAggregator eventAggregator,
        PaymentService paymentService,
        PaymentMethodHandlerDictionary handlers,
        BTCPayNetworkProvider networkProvider,
        ILogger<FlorestaListener> logger)
    {
        _electrumClient = electrumClient;
        _tracker = tracker;
        _statusMonitor = statusMonitor;
        _settingsRepository = settingsRepository;
        _walletProvider = walletProvider;
        _invoiceRepository = invoiceRepository;
        _eventAggregator = eventAggregator;
        _paymentService = paymentService;
        _handlers = handlers;
        _networkProvider = networkProvider;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var bitcoinNetwork = _networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        var bitcoinWallet = bitcoinNetwork is null ? null : _walletProvider.GetWallet(bitcoinNetwork);
        if (bitcoinWallet is not null)
            bitcoinWallet.ForceInefficientPath = true;

        _electrumClient.OnScripthashNotification += OnScripthashNotification;
        _electrumClient.OnNewBlock += OnNewBlock;
        _electrumClient.OnReconnected += OnReconnected;

        _listenTask = RunAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _electrumClient.OnScripthashNotification -= OnScripthashNotification;
        _electrumClient.OnNewBlock -= OnNewBlock;
        _electrumClient.OnReconnected -= OnReconnected;

        _cts?.Cancel();
        if (_listenTask != null)
        {
            try { await _listenTask; } catch (OperationCanceledException) { }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await IsBitcoinBackendActiveAsync(ct))
                {
                    _listenerInitialized = false;
                    await Task.Delay(TimeSpan.FromSeconds(10), ct);
                    continue;
                }

                if (!_electrumClient.IsConnected)
                {
                    _listenerInitialized = false;
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    continue;
                }

                var initializedHeight = await InitializeListenerAsync(ct);
                if (initializedHeight is not null)
                    await RequestPaymentPollingAsync(ct);

                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _listenerInitialized = false;
                _logger.LogError(ex, "Error during Electrum listener initialization; retrying");
                try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task<int?> InitializeListenerAsync(CancellationToken ct)
    {
        await _initializationLock.WaitAsync(ct);
        try
        {
            if (_listenerInitialized &&
                _initializedConnectionGeneration == _electrumClient.ConnectionGeneration)
                return null;

            await _statusMonitor.RefreshAsync(ct);
            if (!_statusMonitor.IsNetworkCompatible)
            {
                _listenerInitialized = false;
                return null;
            }

            await _tracker.InitializeAsync(ct);

            var header = await _electrumClient.HeadersSubscribeAsync(ct);
            _statusMonitor.SetTipHeight(header.Height);
            _tracker.SetTipHeight(header.Height);
            await _statusMonitor.RefreshAsync(ct);
            if (!_statusMonitor.IsNetworkCompatible)
            {
                _listenerInitialized = false;
                return null;
            }

            _listenerInitialized = true;
            _initializedConnectionGeneration = _electrumClient.ConnectionGeneration;
            _logger.LogInformation("Electrum listener initialized, tip height: {Height}", header.Height);
            return header.Height;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private void OnScripthashNotification(string scripthash, string status)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var ct = _cts?.Token ?? CancellationToken.None;
                if (!await IsBitcoinBackendActiveAsync(ct) || !_statusMonitor.IsNetworkCompatible)
                    return;

                var newTxs = await _tracker.HandleScripthashNotificationAsync(scripthash, status, ct);

                await ProcessNewTransactions(newTxs, ct);
                await RequestPaymentPollingAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing scripthash notification for {Scripthash}", scripthash);
            }
        });
    }

    private void OnNewBlock(FlorestaElectrumHeaderNotification header)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var ct = _cts?.Token ?? CancellationToken.None;
                if (!await IsBitcoinBackendActiveAsync(ct))
                    return;

                _statusMonitor.UpdateTipHeight(header.Height);
                await _statusMonitor.RefreshAsync(ct);
                if (!_statusMonitor.IsNetworkCompatible)
                    return;

                var newTxs = await _tracker.HandleNewBlockAsync(header.Height, ct);

                await ProcessNewTransactions(newTxs, ct);
                await RequestPaymentPollingAsync(ct);
                await PublishChainUpdate(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing new block at height {Height}", header.Height);
            }
        });
    }

    private async Task OnReconnected()
    {
        try
        {
            var ct = _cts?.Token ?? CancellationToken.None;
            if (!await IsBitcoinBackendActiveAsync(ct))
                return;

            _logger.LogInformation("Electrum reconnected, re-initializing tracker");
            var initializedHeight = await InitializeListenerAsync(ct);
            var catchUpHeight = GetReconnectCatchUpHeight(
                initializedHeight,
                _statusMonitor.TipHeight,
                _statusMonitor.IsNetworkCompatible);
            if (catchUpHeight is null)
                return;

            var newTxs = await _tracker.HandleNewBlockAsync(catchUpHeight.Value, ct);
            await ProcessNewTransactions(newTxs, ct);
            await RequestPaymentPollingAsync(ct);
            await PublishChainUpdate(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during reconnection re-initialization");
        }
    }

    internal static int? GetReconnectCatchUpHeight(
        int? initializedHeight,
        int currentTipHeight,
        bool isNetworkCompatible)
    {
        return isNetworkCompatible ? initializedHeight ?? currentTipHeight : null;
    }

    private async Task ProcessNewTransactions(
        IReadOnlyCollection<FlorestaWalletTracker.NewTransactionInfo> newTxs,
        CancellationToken ct)
    {
        if (newTxs == null || newTxs.Count == 0 || ct.IsCancellationRequested)
            return;

        var network = _networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        if (network == null) return;

        var wallet = _walletProvider.GetWallet(network);
        if (wallet == null) return;

        var pmi = PaymentTypes.CHAIN.GetPaymentMethodId(network.CryptoCode);

        foreach (var newTx in newTxs)
        {
            ct.ThrowIfCancellationRequested();
            await ProcessNewTransaction(newTx, network, wallet, pmi, ct);
        }
    }

    private async Task PublishChainUpdate(CancellationToken ct)
    {
        var network = _networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        if (network == null) return;

        var pmi = PaymentTypes.CHAIN.GetPaymentMethodId(network.CryptoCode);
        _eventAggregator.Publish(new BTCPayServer.Events.NewBlockEvent { PaymentMethodId = pmi });
        await UpdatePaymentStates(network, pmi, ct);
    }

    private async Task ProcessNewTransaction(
        FlorestaWalletTracker.NewTransactionInfo txInfo,
        BTCPayNetwork network, BTCPayWallet wallet, PaymentMethodId pmi, CancellationToken ct)
    {
        if (txInfo.DerivationStrategy != null)
            wallet.InvalidateCache(txInfo.DerivationStrategy);

        PublishNewOnChainTransaction(txInfo, network, pmi);

        if (txInfo.DerivationStrategy == null)
            return;

        foreach (var output in txInfo.Outputs)
        {
            var invoice = await _invoiceRepository.GetInvoiceFromAddress(pmi, output.TrackedDestination);
            if (invoice == null)
                continue;

            var outpoint = CreatePaymentOutPoint(txInfo.TxId, output.Index);

            var paymentData = new PaymentData
            {
                Id = outpoint.ToString(),
                InvoiceDataId = invoice.Id,
                PaymentMethodId = pmi.ToString(),
                Status = txInfo.Confirmations > 0 ? PaymentStatus.Settled : PaymentStatus.Processing,
                Amount = output.Value.ToDecimal(MoneyUnit.BTC),
                Currency = network.CryptoCode,
                Created = txInfo.SeenAt
            };

            var handler = _handlers.GetBitcoinHandler(network);
            if (handler == null) continue;

            var details = new BitcoinLikePaymentData(
                outpoint,
                txInfo.IsRbf,
                new KeyPath(output.KeyPath),
                output.KeyIndex)
            {
                ConfirmationCount = ClampConfirmations(txInfo.Confirmations, network)
            };

            paymentData.Set(invoice, handler, details);
            paymentData.Status = GetPaymentStatus(invoice, details);

            var payment = await _paymentService.AddPayment(paymentData, [txInfo.TxId]);
            if (payment != null)
            {
                _logger.LogInformation("Recorded payment {PaymentId} for invoice {InvoiceId}",
                    payment.Id, invoice.Id);
                _eventAggregator.Publish(new InvoiceEvent(invoice, InvoiceEvent.ReceivedPayment)
                {
                    Payment = payment
                });
            }
            else
            {
                if (await UpdatePaymentStatesForInvoice(network, pmi, invoice, ct))
                    _eventAggregator.Publish(new InvoiceNeedUpdateEvent(invoice.Id));
            }
        }
    }

    internal static OutPoint CreatePaymentOutPoint(string txId, int outputIndex)
    {
        return new OutPoint(uint256.Parse(txId), (uint)outputIndex);
    }

    private void PublishNewOnChainTransaction(
        FlorestaWalletTracker.NewTransactionInfo txInfo,
        BTCPayNetwork network,
        PaymentMethodId pmi)
    {
        if (txInfo.TrackedSource == null || txInfo.Transaction == null)
            return;

        var txHash = uint256.Parse(txInfo.TxId);
        var evt = new NBXplorer.Models.NewTransactionEvent
        {
            CryptoCode = network.CryptoCode,
            DerivationStrategy = txInfo.DerivationStrategy,
            TrackedSource = txInfo.TrackedSource,
            TransactionData = CreateTransactionResult(txInfo, txHash),
            Outputs = txInfo.Outputs.Select(output => new MatchedOutput
            {
                Address = string.IsNullOrWhiteSpace(output.Address)
                    ? output.ScriptPubKey?.GetDestinationAddress(network.NBitcoinNetwork)
                    : BitcoinAddress.Create(output.Address, network.NBitcoinNetwork),
                ScriptPubKey = output.ScriptPubKey,
                Index = output.Index,
                Value = output.Value,
                KeyPath = string.IsNullOrWhiteSpace(output.KeyPath) ? null : KeyPath.Parse(output.KeyPath),
                KeyIndex = output.KeyIndex,
                Feature = output.KeyPath?.StartsWith("1/", StringComparison.Ordinal) == true
                    ? DerivationFeature.Change
                    : string.IsNullOrWhiteSpace(output.KeyPath)
                        ? null
                        : DerivationFeature.Deposit
            }).ToList()
        };

        _eventAggregator.Publish(new NewOnChainTransactionEvent
        {
            PaymentMethodId = pmi,
            NewTransactionEvent = evt
        });
    }

    internal static NBXplorer.Models.TransactionResult CreateTransactionResult(
        FlorestaWalletTracker.NewTransactionInfo txInfo,
        uint256 txHash)
    {
        return new NBXplorer.Models.TransactionResult
        {
            TransactionHash = txHash,
            Transaction = txInfo.Transaction,
            Confirmations = txInfo.Confirmations,
            Height = txInfo.Height,
            Timestamp = txInfo.SeenAt
        };
    }

    private async Task FindPaymentsViaPolling(CancellationToken ct)
    {
        var network = _networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        if (network == null) return;

        var pmi = PaymentTypes.CHAIN.GetPaymentMethodId(network.CryptoCode);
        var wallet = _walletProvider.GetWallet(network);
        if (wallet == null) return;

        var handler = _handlers.GetBitcoinHandler(network);
        if (handler == null) return;

        var invoices = await _invoiceRepository.GetMonitoredInvoices(pmi, ct);
        var paymentCount = 0;

        foreach (var invoice in invoices)
        {
            try
            {
                var prompt = invoice.GetPaymentPrompt(pmi);
                if (prompt?.Details == null) continue;

                var promptDetails = handler.ParsePaymentPromptDetails(prompt.Details);
                if (promptDetails?.AccountDerivation == null) continue;

                var strategy = promptDetails.AccountDerivation;
                wallet.InvalidateCache(strategy);
                var coins = await wallet.GetUnspentCoins(strategy, cancellation: ct);

                var alreadyAccounted = invoice.GetPayments(false)
                    .Select(p =>
                    {
                        var d = handler.ParsePaymentDetails(p.Details);
                        return d.Outpoint;
                    }).ToHashSet();

                foreach (var coin in coins)
                {
                    if (alreadyAccounted.Contains(coin.OutPoint))
                        continue;

                    if (!InvoiceTracksScriptPubKey(invoice, pmi, network, coin.ScriptPubKey))
                        continue;

                    var tx = await wallet.GetTransactionAsync(coin.OutPoint.Hash, cancellation: ct);
                    if (tx == null) continue;

                    var paymentData = new PaymentData
                    {
                        Id = coin.OutPoint.ToString(),
                        InvoiceDataId = invoice.Id,
                        PaymentMethodId = pmi.ToString(),
                        Status = tx.Confirmations > 0 ? PaymentStatus.Settled : PaymentStatus.Processing,
                        Amount = ((Money)coin.Value).ToDecimal(MoneyUnit.BTC),
                        Currency = network.CryptoCode,
                        Created = tx.Timestamp
                    };

                    var details = new BitcoinLikePaymentData(
                        coin.OutPoint,
                        tx.Transaction?.RBF ?? false,
                        coin.KeyPath,
                        coin.KeyIndex)
                    {
                        ConfirmationCount = ClampConfirmations(tx.Confirmations, network)
                    };

                    paymentData.Set(invoice, handler, details);
                    paymentData.Status = GetPaymentStatus(invoice, details);

                    var payment = await _paymentService.AddPayment(paymentData, [coin.OutPoint.Hash.ToString()]);
                    if (payment != null)
                    {
                        paymentCount++;
                        _eventAggregator.Publish(new InvoiceEvent(invoice, InvoiceEvent.ReceivedPayment)
                        {
                            Payment = payment
                        });
                    }
                    else
                    {
                        if (await UpdatePaymentStatesForInvoice(network, pmi, invoice, ct))
                            _eventAggregator.Publish(new InvoiceNeedUpdateEvent(invoice.Id));
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error polling payments for invoice {InvoiceId}", invoice.Id);
            }
        }

        if (paymentCount > 0)
            _logger.LogInformation("Found {Count} payments via polling", paymentCount);
    }

    private async Task RequestPaymentPollingAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        lock (_paymentPollingGate)
        {
            if (_paymentPollingRunning)
            {
                _paymentPollingPending = true;
                return;
            }

            _paymentPollingRunning = true;
        }

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                await FindPaymentsViaPolling(ct);

                lock (_paymentPollingGate)
                {
                    if (!_paymentPollingPending)
                    {
                        _paymentPollingRunning = false;
                        return;
                    }

                    _paymentPollingPending = false;
                }
            }
        }
        catch
        {
            lock (_paymentPollingGate)
            {
                _paymentPollingRunning = false;
                _paymentPollingPending = false;
            }

            throw;
        }
    }

    internal static bool InvoiceTracksScriptPubKey(
        InvoiceEntity invoice,
        PaymentMethodId paymentMethodId,
        BTCPayNetwork network,
        Script scriptPubKey)
    {
        return invoice.Addresses?.Contains((paymentMethodId, network.GetTrackedDestination(scriptPubKey))) == true;
    }

    private async Task UpdatePaymentStates(BTCPayNetwork network, PaymentMethodId pmi, CancellationToken ct)
    {
        var invoices = await _invoiceRepository.GetMonitoredInvoices(pmi, ct);
        foreach (var invoice in invoices)
        {
            if (await UpdatePaymentStatesForInvoice(network, pmi, invoice, ct))
                _eventAggregator.Publish(new InvoiceNeedUpdateEvent(invoice.Id));
        }
    }

    private async Task<bool> UpdatePaymentStatesForInvoice(
        BTCPayNetwork network,
        PaymentMethodId pmi,
        InvoiceEntity invoice,
        CancellationToken ct)
    {
        var wallet = _walletProvider.GetWallet(network);
        if (wallet == null) return false;

        var handler = _handlers.GetBitcoinHandler(network);
        if (handler == null) return false;

        var updated = new List<PaymentEntity>();
        foreach (var payment in invoice.GetPayments(false).Where(p => p.PaymentMethodId == pmi))
        {
            ct.ThrowIfCancellationRequested();

            var details = handler.ParsePaymentDetails(payment.Details);
            if (details == null)
                continue;

            var tx = await wallet.GetTransactionAsync(details.Outpoint.Hash, cancellation: ct);
            if (tx == null)
                continue;

            var oldConfirmationCount = details.ConfirmationCount;
            var oldRbf = details.RBF;
            var oldStatus = payment.Status;

            details.ConfirmationCount = ClampConfirmations(tx.Confirmations, network);
            details.RBF = tx.Transaction?.RBF ?? details.RBF;
            payment.Status = GetPaymentStatus(invoice, details);

            if (oldConfirmationCount == details.ConfirmationCount &&
                oldRbf == details.RBF &&
                oldStatus == payment.Status)
            {
                continue;
            }

            payment.SetDetails(handler, details);
            updated.Add(payment);
        }

        if (updated.Count == 0)
            return false;

        await _paymentService.UpdatePayments(updated);
        return true;
    }

    internal static long ClampConfirmations(long confirmations, BTCPayNetwork network)
    {
        if (confirmations < 0)
            return confirmations;

        return Math.Min(Math.Max(confirmations, 0), network.MaxTrackedConfirmation);
    }

    private static PaymentStatus GetPaymentStatus(InvoiceEntity invoice, BitcoinLikePaymentData paymentData)
    {
        if (paymentData.ConfirmationCount < 0)
            return PaymentStatus.Unaccounted;

        return IsSettled(invoice, paymentData) ? PaymentStatus.Settled : PaymentStatus.Processing;
    }

    private static int ConfirmationRequired(InvoiceEntity invoice, BitcoinLikePaymentData paymentData)
        => (invoice, paymentData) switch
        {
            ({ SpeedPolicy: SpeedPolicy.HighSpeed }, { RBF: true }) => 1,
            ({ SpeedPolicy: SpeedPolicy.HighSpeed }, _) => 0,
            ({ SpeedPolicy: SpeedPolicy.MediumSpeed }, _) => 1,
            ({ SpeedPolicy: SpeedPolicy.LowMediumSpeed }, _) => 2,
            ({ SpeedPolicy: SpeedPolicy.LowSpeed }, _) => 6,
            _ => 6
        };

    internal static bool IsSettled(InvoiceEntity invoice, BitcoinLikePaymentData paymentData)
    {
        return ConfirmationRequired(invoice, paymentData) <= paymentData.ConfirmationCount;
    }

    private async Task<bool> IsBitcoinBackendActiveAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var settings = await _settingsRepository.GetSettingAsync<FlorestaSettings>() ?? new FlorestaSettings();
        return settings.IsBitcoinBackendActive();
    }
}
