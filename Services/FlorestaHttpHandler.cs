using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Services;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Floresta.Services;

/// <summary>
/// Intercepts ExplorerClient HTTP requests and routes them to the Electrum engine.
/// This allows BTCPayWallet to work unmodified — it calls ExplorerClient methods which
/// make HTTP requests, and this handler returns NBXplorer-compatible JSON responses
/// built from our Electrum backend.
/// </summary>
public class FlorestaHttpHandler(
    FlorestaWalletTracker tracker,
    SettingsRepository settingsRepository,
    BTCPayNetworkProvider networkProvider,
    FlorestaStatusMonitor statusMonitor,
    ILogger<FlorestaHttpHandler> logger)
    : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        var method = request.Method;
        var cryptoCode = ExtractCryptoCode(path);

        if (cryptoCode != null && !string.Equals(cryptoCode, "BTC", StringComparison.OrdinalIgnoreCase))
            return NotFoundResponse();

        if (cryptoCode != null && !await IsBitcoinBackendActiveAsync())
        {
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("Floresta Bitcoin backend is disabled.")
            };
        }

        logger.LogDebug("Intercepting {Method} {Path}", method, SanitizePathForLog(path));

        try
        {
            // POST /v1/cryptos/{code}/derivations — GenerateWallet or Track
            if (method == HttpMethod.Post && Regex.IsMatch(path, @"/v1/cryptos/\w+/derivations(/[^/]+)?$"))
            {
                var strategy = ExtractStrategy(path);
                if (strategy == null)
                    return new HttpResponseMessage(HttpStatusCode.NotImplemented)
                    {
                        Content = new StringContent(
                            "Floresta plugin is watch-only. Configure an xpub or descriptor; private key generation/storage is not supported.")
                    };
                // Descriptor registration and local derivation are part of the import contract:
                // fail them before returning 200. Electrum subscription/sync is slow and can
                // continue in the background.
                var addresses = await tracker.PrepareWalletTrackingAsync(strategy, cancellationToken);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await tracker.SubscribeAndSyncWalletAsync(strategy, addresses, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Background tracking failed for wallet {WalletId}", LogSafeId.Hash(strategy));
                    }
                }, cancellationToken);
                // Return empty 200 — ExplorerClient.TrackAsync uses SendAsync<string>,
                // which returns default(string) when ContentLength == 0.
                // Returning JSON like {} would fail string deserialization.
                return new HttpResponseMessage(HttpStatusCode.OK);

            }

            // GET /v1/cryptos/{code}/derivations/{strategy}/addresses/unused — GetUnused
            if (method == HttpMethod.Get && path.Contains("/addresses/unused"))
            {
                var strategy = ExtractStrategy(path);
                var query = request.RequestUri?.Query ?? "";
                var isChange = query.Contains("feature=Change", StringComparison.OrdinalIgnoreCase);
                var reserve = query.Contains("reserve=True", StringComparison.OrdinalIgnoreCase) ||
                              query.Contains("reserve=true", StringComparison.OrdinalIgnoreCase);
                if (strategy == null) return NotFoundResponse();
                var result = await tracker.GetNextUnusedAddressAsync(strategy, isChange, reserve, cancellationToken);
                return result != null ? OkResponse(result) : NotFoundResponse();
            }

            // POST /v1/cryptos/{code}/derivations/{strategy}/addresses/cancelreservation — CancelReservation
            if (method == HttpMethod.Post && path.EndsWith("/addresses/cancelreservation"))
            {
                var strategy = ExtractStrategy(path);
                if (strategy == null) return NotFoundResponse();
                var body = request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                var network = networkProvider.GetNetwork<BTCPayNetwork>("BTC");
                if (network == null) return new HttpResponseMessage(HttpStatusCode.OK);
                var keyPaths = DeserializeReservationKeyPaths(body, network.NBXplorerNetwork.JsonSerializerSettings);
                await tracker.CancelReservationsAsync(strategy, keyPaths, cancellationToken);

                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            // GET /v1/cryptos/{code}/derivations/{strategy}/utxos — GetUTXOs
            if (method == HttpMethod.Get && path.EndsWith("/utxos"))
            {
                var strategy = ExtractStrategy(path);
                if (strategy == null) return NotFoundResponse();
                var result = await tracker.GetUTXOChangesAsync(strategy, cancellationToken);
                return OkResponse(result);
            }

            // GET /v1/cryptos/{code}/derivations/{strategy}/balance — GetBalance
            if (method == HttpMethod.Get && path.EndsWith("/balance"))
            {
                var strategy = ExtractStrategy(path);
                if (strategy == null) return NotFoundResponse();
                var result = await tracker.GetBalanceAsync(strategy, cancellationToken);
                return OkResponse(result);
            }

            // GET /v1/cryptos/{code}/derivations/{strategy}/transactions/{txId} — GetTransaction by strategy + txid
            if (method == HttpMethod.Get && Regex.IsMatch(path, @"/derivations/[^/]+/transactions/[0-9a-fA-F]{64}$"))
            {
                var strategy = ExtractStrategy(path);
                var txId = ExtractTxId(path);
                if (strategy == null || txId == null) return NotFoundResponse();
                var result = await tracker.GetTransactionInfoAsync(strategy, txId, cancellationToken);
                return result != null ? OkResponse(result) : NotFoundResponse();
            }

            // GET /v1/cryptos/{code}/derivations/{strategy}/transactions — GetTransactions
            if (method == HttpMethod.Get && Regex.IsMatch(path, @"/derivations/[^/]+/transactions$"))
            {
                var strategy = ExtractStrategy(path);
                if (strategy == null) return NotFoundResponse();
                var result = await tracker.GetTransactionsResponseAsync(strategy, cancellationToken);
                return OkResponse(result);
            }

            // GET /v1/cryptos/{code}/transactions/{txId} — GetTransaction by txid only
            if (method == HttpMethod.Get && Regex.IsMatch(path, @"/v1/cryptos/\w+/transactions/[0-9a-fA-F]{64}$"))
            {
                var txId = ExtractTxId(path);
                if (txId == null) return NotFoundResponse();
                var result = await tracker.GetTransactionResultAsync(txId, cancellationToken);
                return result != null ? OkResponse(result) : NotFoundResponse();
            }

            // POST /v1/cryptos/{code}/transactions — Broadcast
            if (method == HttpMethod.Post && Regex.IsMatch(path, @"/v1/cryptos/\w+/transactions$"))
            {
                var body = request.Content is null
                    ? []
                    : await request.Content.ReadAsByteArrayAsync(cancellationToken);
                var result = await tracker.BroadcastAsync(body, cancellationToken);
                return OkResponse(result);
            }

            // GET /v1/cryptos/{code}/fees/{blockTarget} — GetFeeRate
            if (method == HttpMethod.Get && Regex.IsMatch(path, @"/v1/cryptos/\w+/fees/\d+$"))
            {
                var blockTarget = int.Parse(path.Split('/').Last());
                var feeRate = await tracker.GetFeeRateAsync(blockTarget, cancellationToken);
                return OkResponse(feeRate);
            }

            // GET /v1/cryptos/{code}/status — GetStatus
            if (method == HttpMethod.Get && path.EndsWith("/status"))
            {
                var status = statusMonitor.GetStatusResult();
                return OkResponse(status);
            }

            // GET /v1/cryptos/{code}/derivations/{scheme}/metadata/{key} — GetMetadata
            if (method == HttpMethod.Get && Regex.IsMatch(path, @"/derivations/[^/]+/metadata/[^/]+$"))
            {
                var strategy = ExtractStrategy(path);
                var key = ExtractMetadataKey(path);
                if (strategy == null || key == null) return NotFoundResponse();
                var value = await tracker.GetMetadataAsync(strategy, key, cancellationToken);
                return value != null ? OkResponse(value) : NotFoundResponse();
            }

            // POST /v1/cryptos/{code}/derivations/{scheme}/metadata/{key} — SetMetadata
            if (method == HttpMethod.Post && Regex.IsMatch(path, @"/derivations/[^/]+/metadata/[^/]+$"))
            {
                var strategy = ExtractStrategy(path);
                var key = ExtractMetadataKey(path);
                if (strategy == null || key == null) return NotFoundResponse();
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                // NBXplorer sends the value JSON-serialized, so deserialize it
                var value = JsonConvert.DeserializeObject<string>(body);
                await tracker.SetMetadataAsync(strategy, key, value, cancellationToken);
                return OkResponse(new { });
            }

            // POST /v1/cryptos/{code}/derivations/{strategy}/psbt/create — CreatePSBT
            if (method == HttpMethod.Post && path.EndsWith("/psbt/create"))
            {
                var strategy = ExtractStrategy(path);
                if (strategy == null) return NotFoundResponse();
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                var result = await HandleCreatePsbtAsync(strategy, body, cancellationToken);
                return OkResponse(result);
            }

            // POST /v1/cryptos/{code}/psbt/update — UpdatePSBT
            if (method == HttpMethod.Post && path.EndsWith("/psbt/update"))
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                var result = HandleUpdatePsbt(body);
                return OkResponse(result);
            }

            // GET /v1/cryptos/{code}/derivations/{strategy}/utxos/scan — GetScanUTXOSetInformation
            if (method == HttpMethod.Get && path.EndsWith("/utxos/scan"))
            {
                var strategy = ExtractStrategy(path);
                if (strategy == null) return new HttpResponseMessage(HttpStatusCode.NotFound);
                var scanState = tracker.GetScanState(strategy);
                return scanState != null ? OkResponse(scanState) : new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            // POST /v1/cryptos/{code}/derivations/{strategy}/utxos/scan — ScanUTXOSetAsync
            if (method == HttpMethod.Post && path.EndsWith("/utxos/scan"))
            {
                var strategy = ExtractStrategy(path);
                if (strategy == null) return NotFoundResponse();
                var query = request.RequestUri?.Query ?? "";
                var scan = NormalizeScanParameters(
                    ExtractQueryInt(query, "gapLimit"),
                    ExtractQueryInt(query, "from"),
                    ExtractQueryInt(query, "batchSize"));
                tracker.StartScan(strategy, scan.GapLimit, scan.From, scan.BatchSize);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            // POST /v1/cryptos/{code}/derivations/{strategy}/utxos/wipe — WipeAsync
            if (method == HttpMethod.Post && path.EndsWith("/utxos/wipe"))
            {
                var strategy = ExtractStrategy(path);
                if (strategy == null) return NotFoundResponse();
                await tracker.WipeAsync(strategy, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            logger.LogWarning("Unhandled ExplorerClient request: {Method} {Path}", method, SanitizePathForLog(path));
            return new HttpResponseMessage(HttpStatusCode.NotImplemented);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling {Method} {Path}", method, SanitizePathForLog(path));
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent(ex.Message)
            };
        }
    }

    private string ExtractStrategy(string path)
    {
        var match = Regex.Match(path, @"/derivations/([^/]+)");
        return match.Success ? Uri.UnescapeDataString(match.Groups[1].Value) : null;
    }

    private string ExtractCryptoCode(string path)
    {
        var match = Regex.Match(path, @"/v1/cryptos/(\w+)/");
        return match.Success ? match.Groups[1].Value : null;
    }

    private string ExtractMetadataKey(string path)
    {
        var match = Regex.Match(path, @"/metadata/([^/]+)$");
        return match.Success ? Uri.UnescapeDataString(match.Groups[1].Value) : null;
    }

    private int? ExtractQueryInt(string query, string key)
    {
        var match = Regex.Match(query, $@"[?&]{key}=(\d+)", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var val) ? val : null;
    }

    private string ExtractTxId(string path)
    {
        var match = Regex.Match(path, @"/transactions/([0-9a-fA-F]{64})");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string SanitizePathForLog(string path)
    {
        return Regex.Replace(path, @"/derivations/[^/]+", "/derivations/{wallet}");
    }

    private async Task<object> HandleCreatePsbtAsync(string strategyStr, string body, CancellationToken ct)
    {
        var network = networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        var nbNetwork = network?.NBitcoinNetwork;
        var factory = new DerivationStrategyFactory(nbNetwork);
        var strategy = factory.Parse(strategyStr);
        var jsonSettings = network?.NBXplorerNetwork.JsonSerializerSettings;

        var request = JsonConvert.DeserializeObject<CreatePSBTRequest>(body, jsonSettings);

        // Get available UTXOs
        var utxoChanges = await tracker.GetUTXOChangesAsync(strategyStr, ct);
        var allUtxos = utxoChanges.Confirmed.UTXOs
            .Concat(utxoChanges.Unconfirmed.UTXOs)
            .Where(u => request.MinConfirmations <= 0 || u.Confirmations >= request.MinConfirmations)
            .Where(u => request.MinValue == null || (u.Value is Money m && m >= request.MinValue))
            .ToList();

        if (request.ExcludeOutpoints?.Count > 0)
            allUtxos = allUtxos.Where(u => !request.ExcludeOutpoints.Contains(u.Outpoint)).ToList();

        if (request.IncludeOnlyOutpoints?.Count > 0)
            allUtxos = allUtxos.Where(u => request.IncludeOnlyOutpoints.Contains(u.Outpoint)).ToList();

        // Get fee rate
        var feeRate = request.FeePreference?.ExplicitFee is null
            ? request.FeePreference?.ExplicitFeeRate
            : null;
        if (request.FeePreference?.ExplicitFee is null && feeRate == null)
        {
            var blockTarget = request.FeePreference?.BlockTarget ?? 1;
            var feeResult = await tracker.GetFeeRateAsync(blockTarget, ct);
            feeRate = ResolvePsbtFeeRate(request.FeePreference, feeResult);
        }

        // Build transaction using NBitcoin's TransactionBuilder
        var txBuilder = nbNetwork?.CreateTransactionBuilder();
        txBuilder?.SetSigningOptions(SigHash.All);
        ApplyPsbtBuilderOptions(txBuilder, request);

        // Add coins from UTXOs
        foreach (var coin in allUtxos.Select(utxo => utxo.AsCoin(strategy)).Where(coin => coin != null))
        {
            txBuilder?.AddCoins(coin);
        }

        // Add destinations
        var isSweep = false;
        foreach (var dest in request.Destinations ?? [])
        {
            ApplyPsbtDestination(txBuilder, dest);
            isSweep |= dest.SweepAll;
        }

        // Set change address
        BitcoinAddress changeAddress = null;
        Script changeScript = null;
        if (!isSweep)
        {
            if (request.ExplicitChangeAddress?.ScriptPubKey != null)
            {
                changeScript = request.ExplicitChangeAddress.ScriptPubKey;
                if (nbNetwork != null) changeAddress = changeScript.GetDestinationAddress(nbNetwork);
            }
            else
            {
                var changeInfo = await tracker.GetNextUnusedAddressAsync(strategyStr, true,
                    false, ct);
                if (changeInfo != null)
                {
                    changeScript = changeInfo.ScriptPubKey;
                    if (nbNetwork != null)
                        changeAddress = changeScript.GetDestinationAddress(nbNetwork) ??
                                        BitcoinAddress.Create(changeInfo.Address.ToString(), nbNetwork);
                }
            }

            if (changeScript != null) txBuilder?.SetChange(changeScript);
        }

        if (request.FeePreference?.ExplicitFee is { } explicitFee)
            txBuilder.SendFees(explicitFee);
        else
            txBuilder.SendEstimatedFees(feeRate);

        if (request.SpendAllMatchingOutpoints is true)
            txBuilder.SendAllRemainingToChange();

        var psbt = BuildPsbt(txBuilder, request);
        var hasChange = changeScript != null && psbt.Outputs.Any(o => o.ScriptPubKey == changeScript);
        if (hasChange && request.ExplicitChangeAddress == null && request.ReserveChangeAddress)
        {
            var reservedChange = await tracker.GetNextUnusedAddressAsync(strategyStr, true, true, ct);
            if (reservedChange != null && reservedChange.ScriptPubKey != changeScript)
            {
                changeScript = reservedChange.ScriptPubKey;
                changeAddress = changeScript.GetDestinationAddress(nbNetwork) ??
                                BitcoinAddress.Create(reservedChange.Address.ToString(), nbNetwork);
                txBuilder.SetChange(changeScript);
                psbt = BuildPsbt(txBuilder, request);
                hasChange = psbt.Outputs.Any(o => o.ScriptPubKey == changeScript);
            }
        }

        // Add HD key path and optional UTXO metadata to PSBT inputs
        foreach (var input in psbt.Inputs)
        {
            var utxo = allUtxos.FirstOrDefault(u => u.Outpoint == input.PrevOut);
            if (utxo?.KeyPath != null)
            {
                var pubKeys = strategy.GetExtPubKeys().ToArray();
                foreach (var pubKey in pubKeys)
                {
                    var derived = pubKey.Derive(utxo.KeyPath);
                    input.AddKeyPath(derived.GetPublicKey(),
                        new RootedKeyPath(pubKey.GetPublicKey().GetHDFingerPrint(), utxo.KeyPath));
                }
            }

            if (!request.AlwaysIncludeNonWitnessUTXO || input.NonWitnessUtxo is not null) continue;
            var tx = await tracker.GetTransactionResultAsync(input.PrevOut.Hash.ToString(), ct);
            if (tx?.Transaction != null)
                input.NonWitnessUtxo = tx.Transaction;
        }

        ApplyPsbtGlobalXPub(psbt, strategy, request.IncludeGlobalXPub, nbNetwork);
        ApplyPsbtRebaseKeyPaths(psbt, request.RebaseKeyPaths);

        var response = new JObject
        {
            ["psbt"] = psbt.ToBase64(),
            ["changeAddress"] = hasChange ? changeAddress?.ToString() : null
        };

        return response;
    }

    private JObject HandleUpdatePsbt(string body)
    {
        var network = networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        var jsonSettings = network?.NBXplorerNetwork.JsonSerializerSettings;

        var request = JsonConvert.DeserializeObject<UpdatePSBTRequest>(body, jsonSettings);

        var psbt = request.PSBT;

        // Rebase key paths if requested
        if (!(request.RebaseKeyPaths?.Count > 0)) return new JObject { ["psbt"] = psbt.ToBase64() };
        foreach (var rebase in request.RebaseKeyPaths)
        {
            psbt.RebaseKeyPaths(rebase.AccountKey, rebase.AccountKeyPath);
        }

        return new JObject { ["psbt"] = psbt.ToBase64() };
    }

    internal static FeeRate ResolvePsbtFeeRate(FeePreference preference, GetFeeRateResult feeResult)
    {
        return preference?.ExplicitFeeRate ??
               feeResult?.FeeRate ??
               preference?.FallbackFeeRate ??
               new FeeRate(FlorestaSettings.DefaultFallbackFeeRateSatsPerByte);
    }

    internal static void ApplyPsbtBuilderOptions(TransactionBuilder txBuilder, CreatePSBTRequest request)
    {
        if (request.MergeOutputs is { } mergeOutputs)
            txBuilder.MergeOutputs = mergeOutputs;

        if (request.Version is { } version)
            txBuilder.SetVersion(version);

        if (request.RBF == true || request.LockTime.HasValue)
            txBuilder.OptInRBF = true;

        if (request.LockTime is { } lockTime)
            txBuilder.SetLockTime(lockTime);
    }

    internal static void ApplyPsbtDestination(TransactionBuilder txBuilder, CreatePSBTDestination destination)
    {
        if (destination.SweepAll)
        {
            txBuilder.SendAll(destination.Destination.ScriptPubKey);
            return;
        }

        txBuilder.Send(destination.Destination.ScriptPubKey, destination.Amount);
        if (destination.SubstractFees)
            txBuilder.SubtractFees();
    }

    internal static PSBT BuildPsbt(TransactionBuilder txBuilder, CreatePSBTRequest request)
    {
        var psbtVersion = request.PSBTVersion == 2 ? PSBTVersion.PSBTv2 : PSBTVersion.PSBTv0;
        var psbt = txBuilder.BuildPSBT(false, psbtVersion);
        if (request.Version is { } version)
            psbt.GetGlobalTransaction().Version = version;
        return psbt;
    }

    internal static void ApplyPsbtGlobalXPub(
        PSBT psbt,
        DerivationStrategyBase strategy,
        bool? includeGlobalXPub,
        Network network)
    {
        if (includeGlobalXPub is not true)
            return;

        foreach (var extPubKey in strategy.GetExtPubKeys().Select(e => e.GetWif(network)))
            psbt.GlobalXPubs.AddOrReplace(extPubKey, new RootedKeyPath(extPubKey, new KeyPath()));
    }

    private static void ApplyPsbtRebaseKeyPaths(
        PSBT psbt,
        List<PSBTRebaseKeyRules> rebaseKeyPaths)
    {
        if (!(rebaseKeyPaths?.Count > 0)) return;
        foreach (var rebase in rebaseKeyPaths)
            psbt.RebaseKeyPaths(rebase.AccountKey, rebase.AccountKeyPath);
    }

    internal const int DefaultScanGapLimit = 100;
    internal const int DefaultScanBatchSize = 100;
    internal const int MaxScanBatchSize = 500;

    internal static (int GapLimit, int From, int BatchSize) NormalizeScanParameters(
        int? gapLimit,
        int? from,
        int? batchSize)
    {
        return (
            Math.Clamp(gapLimit ?? DefaultScanGapLimit, 1, FlorestaSettings.MaxGapLimit),
            Math.Max(from ?? 0, 0),
            Math.Clamp(batchSize ?? DefaultScanBatchSize, 1, MaxScanBatchSize));
    }

    internal static KeyPath[] DeserializeReservationKeyPaths(string body, JsonSerializerSettings jsonSettings)
    {
        if (string.IsNullOrWhiteSpace(body))
            return [];

        return JsonConvert.DeserializeObject<KeyPath[]>(body, jsonSettings) ?? [];
    }

    private HttpResponseMessage OkResponse(object data)
    {
        var network = networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        var jsonSettings = network?.NBXplorerNetwork.JsonSerializerSettings;
        var json = JsonConvert.SerializeObject(data, jsonSettings);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
    }

    private HttpResponseMessage NotFoundResponse()
    {
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private async Task<bool> IsBitcoinBackendActiveAsync()
    {
        var settings = await settingsRepository.GetSettingAsync<FlorestaSettings>() ?? new FlorestaSettings();
        return settings.IsBitcoinBackendActive();
    }
}
