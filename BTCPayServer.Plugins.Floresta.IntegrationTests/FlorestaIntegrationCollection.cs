using Xunit;

namespace BTCPayServer.Plugins.Floresta.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class FlorestaIntegrationCollection : ICollectionFixture<FlorestaIntegrationFixture>
{
    public const string Name = "florestad integration";
}
