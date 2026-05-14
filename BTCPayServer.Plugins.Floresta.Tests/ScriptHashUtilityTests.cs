using BTCPayServer.Plugins.Floresta;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests;

public class ScriptHashUtilityTests
{
    [Theory]
    [InlineData("0014751e76e8199196d454941c45d1b3a323f1433bd6", "9623df75239b5daa7f5f03042d325b51498c4bb7059c7748b17049bf96f73888")]
    [InlineData("76a914751e76e8199196d454941c45d1b3a323f1433bd688ac", "8bd2c4f79944cd6a3cb1730cf92c513ae259eb271d81918457f3753eebe14a3f")]
    [InlineData("a914751e76e8199196d454941c45d1b3a323f1433bd687", "5a29e8e4026531293483370fe8133bda5c2e28c882b597902515f9b9fcfa7e95")]
    public void ComputesElectrumScriptHash(string scriptHex, string expectedScriptHash)
    {
        var script = Script.FromHex(scriptHex);

        var actual = ScriptHashUtility.ComputeScriptHash(script);

        Assert.Equal(expectedScriptHash, actual);
        Assert.Equal(actual.ToLowerInvariant(), actual);
    }
}
