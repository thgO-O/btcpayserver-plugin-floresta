using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests.Integration;

[CollectionDefinition(Name)]
public sealed class FlorestaIntegrationCollection : ICollectionFixture<FlorestaIntegrationFixture>
{
    public const string Name = "florestad integration";
}
