using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.IntegrationTests;

public sealed class FlorestaIntegrationFixture : IAsyncLifetime
{
    public FlorestaSettings Settings { get; } = new()
    {
        Enabled = true,
        CryptoCode = "BTC",
        Network = Environment.GetEnvironmentVariable("FLORESTA_NETWORK") ?? "regtest",
        ElectrumHost = Environment.GetEnvironmentVariable("FLORESTA_ELECTRUM_HOST") ?? "127.0.0.1",
        ElectrumPort = int.TryParse(Environment.GetEnvironmentVariable("FLORESTA_ELECTRUM_PORT"), out var electrumPort)
            ? electrumPort
            : 20001,
        ElectrumUseTls = bool.TryParse(Environment.GetEnvironmentVariable("FLORESTA_ELECTRUM_TLS"), out var electrumTls) && electrumTls,
        RpcUrl = Environment.GetEnvironmentVariable("FLORESTA_RPC_URL") ?? "http://127.0.0.1:18442",
        GapLimit = 100
    };

    public FlorestaRpcClient Rpc { get; private set; }

    public async Task InitializeAsync()
    {
        Rpc = new FlorestaRpcClient(Settings, NullLogger<FlorestaRpcClient>.Instance);
        var timeout = TimeSpan.FromSeconds(
            int.TryParse(Environment.GetEnvironmentVariable("FLORESTA_READY_TIMEOUT_SECONDS"), out var seconds)
                ? seconds
                : 90);
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception last = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await Rpc.PingAsync(cts.Token);

                await using var electrum = CreateElectrumClient();
                await electrum.ConnectAsync(cts.Token);
                var version = await electrum.ServerVersionAsync("btcpay-floresta-tests", "1.4", cts.Token);
                if (!string.IsNullOrWhiteSpace(version.serverSoftware))
                    return;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }

        throw new TimeoutException(
            $"florestad was not ready within {timeout}. RPC={Settings.RpcUrl}, Electrum={Settings.Server}",
            last);
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public FlorestaElectrumClient CreateElectrumClient()
    {
        return new FlorestaElectrumClient(Settings, NullLogger<FlorestaElectrumClient>.Instance);
    }
}
