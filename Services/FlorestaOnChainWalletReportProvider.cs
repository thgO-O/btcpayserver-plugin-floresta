#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Rating;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Reporting;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Wallets;
using Dapper;
using NBitcoin;

namespace BTCPayServer.Plugins.Floresta.Services;

public class FlorestaOnChainWalletReportProvider : ReportProvider
{
    private readonly NBXplorerConnectionFactory _nbxplorerConnectionFactory;
    private readonly StoreRepository _storeRepository;
    private readonly InvoiceRepository _invoiceRepository;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly WalletRepository _walletRepository;
    private readonly BTCPayWalletProvider _walletProvider;
    private readonly SettingsRepository _settingsRepository;

    public FlorestaOnChainWalletReportProvider(
        NBXplorerConnectionFactory nbxplorerConnectionFactory,
        StoreRepository storeRepository,
        InvoiceRepository invoiceRepository,
        PaymentMethodHandlerDictionary handlers,
        WalletRepository walletRepository,
        BTCPayWalletProvider walletProvider,
        SettingsRepository settingsRepository)
    {
        _nbxplorerConnectionFactory = nbxplorerConnectionFactory;
        _storeRepository = storeRepository;
        _invoiceRepository = invoiceRepository;
        _handlers = handlers;
        _walletRepository = walletRepository;
        _walletProvider = walletProvider;
        _settingsRepository = settingsRepository;
    }

    public override string Name => "Wallets";

    public override bool IsAvailable() => _nbxplorerConnectionFactory.Available;

    public override async Task Query(QueryContext queryContext, CancellationToken cancellation)
    {
        queryContext.ViewDefinition = CreateViewDefinition();
        await using var conn = await _nbxplorerConnectionFactory.OpenConnection();
        var store = await _storeRepository.FindStore(queryContext.StoreId);
        if (store is null)
            return;

        var skipBitcoin = await IsFlorestaBitcoinBackendActiveAsync();
        var walletBooks = new Dictionary<(string CryptoCode, string TxId), RateBook>();
        var cryptoCodes = new HashSet<string>();
        var interval = DateTimeOffset.UtcNow - queryContext.From;

        foreach (var (pmi, settings) in store.GetPaymentMethodConfigs<DerivationSchemeSettings>(_handlers))
        {
            var network = ((IHasNetwork)_handlers[pmi]).Network;
            if (skipBitcoin && network.IsBTC)
                continue;

            cryptoCodes.Add(network.CryptoCode);
            var walletId = new WalletId(store.Id, network.CryptoCode);
            var wallet = _walletProvider.GetWallet(network);
            if (wallet is null)
                continue;
            var selectFee = SelectFeeColumns(wallet);

            var command = new CommandDefinition(
                commandText:
                $"""
                SELECT r.tx_id, r.seen_at, t.blk_id, t.blk_height, r.balance_change, {selectFee}
                FROM get_wallets_recent(@wallet_id, @code, @asset_id, @interval, NULL, NULL) r
                JOIN txs t USING (code, tx_id)
                ORDER BY r.seen_at
                """,
                parameters: new
                {
                    asset_id = GetAssetId(network),
                    wallet_id = NBXplorer.Client.DBUtils.nbxv1_get_wallet_id(
                        network.CryptoCode,
                        settings.AccountDerivation.ToString()),
                    code = network.CryptoCode,
                    interval
                },
                cancellationToken: cancellation);

            var rows = await conn.QueryAsync(command);
            foreach (var r in rows)
            {
                var date = (DateTimeOffset)r.seen_at;
                if (date > queryContext.To)
                    continue;

                var values = queryContext.AddData();
                var balanceChange = Money.Satoshis((long)r.balance_change).ToDecimal(MoneyUnit.BTC);
                values.Add(date);
                values.Add(network.CryptoCode);
                values.Add((string)r.tx_id);
                values.Add(null);
                values.Add((long?)r.blk_height is not null);
                values.Add(new FormattedAmount(balanceChange, network.Divisibility).ToJObject());

                decimal? fee = r.fee is null ? null : Money.Satoshis((long)r.fee).ToDecimal(MoneyUnit.BTC);
                decimal? feeRate = r.feerate is null ? null : (decimal)r.feerate;
                values.Add(fee is null ? null : new FormattedAmount(fee.Value, network.Divisibility).ToJObject());
                values.Add(feeRate);
            }

            var objects = await _walletRepository.GetWalletObjects(new GetWalletObjectsQuery
            {
                Ids = queryContext.Data
                    .Where(d => string.Equals((string?)d[1], network.CryptoCode, StringComparison.OrdinalIgnoreCase))
                    .Select(d => (string)d[2]!)
                    .ToArray(),
                WalletId = walletId,
                Type = WalletObjectData.Types.Tx
            });
            foreach (var row in queryContext.Data.Where(d =>
                         string.Equals((string?)d[1], network.CryptoCode, StringComparison.OrdinalIgnoreCase)))
            {
                if (!objects.TryGetValue(new WalletObjectId(walletId, WalletObjectData.Types.Tx, (string)row[2]!), out var txObject))
                    continue;

                var invoiceId = txObject.GetLinks()
                    .Where(t => t.type == WalletObjectData.Types.Invoice)
                    .Select(t => t.id)
                    .FirstOrDefault();
                row[3] = invoiceId;
                if (RateBook.FromTxWalletObject(txObject) is { } book)
                    walletBooks.Add(GetKey(row), book);
            }
        }

        var trackedCurrencies = store.GetStoreBlob().GetTrackedRates().ToHashSet();
        var rates = await _invoiceRepository.GetRatesOfInvoices(
            queryContext.Data.Select(r => r[3]).OfType<string>().ToHashSet());
        foreach (var book in rates.Select(r => r.Value))
        {
            book.AddCurrencies(trackedCurrencies);
        }
        foreach (var row in queryContext.Data)
        {
            walletBooks.TryGetValue(GetKey(row), out var rateData);
            rateData?.AddCurrencies(trackedCurrencies);
        }

        trackedCurrencies.ExceptWith(cryptoCodes);
        foreach (var trackedCurrency in trackedCurrencies)
        {
            queryContext.ViewDefinition.Fields.Add(new($"Rate ({trackedCurrency})", "number"));
        }

        foreach (var row in queryContext.Data)
        {
            var key = GetKey(row);
            walletBooks.TryGetValue(key, out var rateData);
            var invoiceId = row[3] as string;
            rates.TryGetValue(invoiceId ?? "", out var rateBook);
            rateBook ??= new("", new());
            rateBook.AddRates(rateData);
            foreach (var trackedCurrency in trackedCurrencies)
            {
                row.Add(rateBook.TryGetRate(new CurrencyPair(key.CryptoCode, trackedCurrency)) is decimal v
                    ? v
                    : null);
            }
        }
    }

    private static ViewDefinition CreateViewDefinition()
    {
        return new ViewDefinition
        {
            Fields =
            {
                new("Date", "datetime"),
                new("Crypto", "string"),
                new("TransactionId", "tx_id"),
                new("InvoiceId", "invoice_id"),
                new("Confirmed", "boolean"),
                new("BalanceChange", "amount"),
                new("Fee", "amount"),
                new("FeeRate", "number"),
            },
            Charts =
            {
                new()
                {
                    Name = "Group by Crypto",
                    Totals = { "Crypto" },
                    Groups = { "Crypto", "Confirmed" },
                    Aggregates = { "BalanceChange" }
                }
            }
        };
    }

    private async Task<bool> IsFlorestaBitcoinBackendActiveAsync()
    {
        var settings = await _settingsRepository.GetSettingAsync<FlorestaSettings>() ?? new FlorestaSettings();
        return settings.IsBitcoinBackendActive();
    }

    private static (string CryptoCode, string TxId) GetKey(IList<object?> row) => ((string)row[1]!, (string)row[2]!);

    private static string SelectFeeColumns(BTCPayWallet wallet)
    {
        var hasFeeInformation = wallet.ForceHasFeeInformation ??
                                AsVersion(wallet.Dashboard.Get(wallet.Network.CryptoCode)?.Status?.Version ?? "") >=
                                new Version("2.5.18");
        return hasFeeInformation
            ? "(metadata->'fees')::BIGINT AS fee, (metadata->'feeRate')::NUMERIC AS feerate"
            : "NULL AS fee, NULL AS feerate";
    }

    private static Version AsVersion(string version)
    {
        if (Version.TryParse(version.Split('-').FirstOrDefault(), out var parsed))
            return parsed;
        return new Version("0.0.0.0");
    }

    private static string? GetAssetId(BTCPayNetwork network)
    {
        if (network is BTCPayServer.Plugins.Altcoins.ElementsBTCPayNetwork elNetwork)
            return elNetwork.IsNativeAsset ? "" : elNetwork.AssetId.ToString();
        return null;
    }
}
