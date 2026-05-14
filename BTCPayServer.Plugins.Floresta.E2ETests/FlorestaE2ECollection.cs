using Xunit;

namespace BTCPayServer.Plugins.Floresta.E2ETests;

[CollectionDefinition(Name)]
public sealed class FlorestaE2ECollection : ICollectionFixture<BtcpayFlorestaWebAppFixture>
{
    public const string Name = "Floresta Playwright E2E";
}
