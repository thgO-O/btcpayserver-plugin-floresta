using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests;

public class FlorestaElectrumClientTests
{
    [Fact]
    public async Task DisposeBeforeConnectIsNoOp()
    {
        await using var client = new FlorestaElectrumClient(
            new FlorestaSettings(),
            NullLogger<FlorestaElectrumClient>.Instance);

        await client.DisposeAsync();
    }
}
