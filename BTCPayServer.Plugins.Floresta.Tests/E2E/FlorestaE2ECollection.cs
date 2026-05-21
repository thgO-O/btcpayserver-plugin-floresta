using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests.E2E;

[CollectionDefinition(Name)]
public sealed class FlorestaE2ECollection : ICollectionFixture<BtcpayFlorestaWebAppFixture>
{
    public const string Name = "Floresta Playwright E2E";
}
