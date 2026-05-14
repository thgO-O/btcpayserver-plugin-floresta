using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NBitcoin;
using NBXplorer.DerivationStrategy;

namespace BTCPayServer.Plugins.Floresta;

public class FlorestaDescriptorService
{
    private readonly BTCPayNetworkProvider _networkProvider;

    public FlorestaDescriptorService(BTCPayNetworkProvider networkProvider)
    {
        _networkProvider = networkProvider;
    }

    public FlorestaDescriptorSet CreateDescriptors(string cryptoCode, string derivationStrategy)
    {
        var network = _networkProvider.GetNetwork<BTCPayNetwork>(cryptoCode ?? "BTC")
                      ?? throw new NotSupportedException($"Unsupported crypto code for Floresta: {cryptoCode}");
        var factory = network.NBXplorerNetwork.DerivationStrategyFactory;
        var strategy = factory.Parse(derivationStrategy);
        var extPubKeys = strategy.GetExtPubKeys().ToArray();
        if (extPubKeys.Length != 1)
            throw new NotSupportedException("Floresta MVP supports only single-sig derivation schemes.");

        var xpub = extPubKeys[0].GetWif(network.NBitcoinNetwork).ToString();
        var receive = strategy.GetLineFor(DerivationFeature.Deposit).Derive(0);
        var scriptKind = GetDescriptorWrapper(receive);

        var receiveDescriptor = Wrap(scriptKind, $"{xpub}/0/*");
        var changeDescriptor = Wrap(scriptKind, $"{xpub}/1/*");
        return new FlorestaDescriptorSet(
            derivationStrategy,
            DescriptorHash(receiveDescriptor, changeDescriptor),
            receiveDescriptor,
            changeDescriptor);
    }

    private static string GetDescriptorWrapper(Derivation derivation)
    {
        if (derivation.ScriptPubKey.IsScriptType(ScriptType.P2WPKH))
            return "wpkh";
        if (derivation.ScriptPubKey.IsScriptType(ScriptType.P2PKH))
            return "pkh";
        if (derivation.Redeem is not null &&
            derivation.ScriptPubKey.IsScriptType(ScriptType.P2SH) &&
            derivation.Redeem.IsScriptType(ScriptType.P2WPKH))
            return "sh-wpkh";

        throw new NotSupportedException("Floresta MVP supports p2wpkh, p2sh-p2wpkh and p2pkh single-sig wallets only.");
    }

    private static string Wrap(string kind, string keyExpression)
    {
        return kind switch
        {
            "wpkh" => $"wpkh({keyExpression})",
            "pkh" => $"pkh({keyExpression})",
            "sh-wpkh" => $"sh(wpkh({keyExpression}))",
            _ => throw new NotSupportedException($"Unsupported descriptor wrapper: {kind}")
        };
    }

    private static string DescriptorHash(string receiveDescriptor, string changeDescriptor)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{receiveDescriptor}\n{changeDescriptor}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public record FlorestaDescriptorSet(
    string DerivationStrategy,
    string DescriptorHash,
    string ReceiveDescriptor,
    string ChangeDescriptor);
