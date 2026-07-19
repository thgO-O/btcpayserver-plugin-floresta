using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
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

    [Fact]
    public async Task SuccessfulConnectionsAdvanceConnectionGeneration()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var client = new FlorestaElectrumClient(
            new FlorestaSettings
            {
                ElectrumHost = IPAddress.Loopback.ToString(),
                ElectrumPort = port
            },
            NullLogger<FlorestaElectrumClient>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var firstAccept = listener.AcceptTcpClientAsync(cts.Token);
        await client.ConnectAsync(cts.Token);
        using var firstConnection = await firstAccept;
        var firstGeneration = client.ConnectionGeneration;

        Assert.True(client.IsConnected);
        Assert.True(firstGeneration > 0);

        await client.DisconnectAsync();
        Assert.False(client.IsConnected);

        var secondAccept = listener.AcceptTcpClientAsync(cts.Token);
        await client.ConnectAsync(cts.Token);
        using var secondConnection = await secondAccept;

        Assert.True(client.IsConnected);
        Assert.Equal(firstGeneration + 1, client.ConnectionGeneration);
    }
}
