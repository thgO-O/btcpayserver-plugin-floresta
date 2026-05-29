using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Services;
using Microsoft.Extensions.Hosting;

namespace BTCPayServer.Plugins.Floresta.Services;

public class FlorestaNonBitcoinNbxplorerConnectionHostedService : IHostedService
{
    private readonly NBXplorerConnectionFactory _connectionFactory;
    private readonly BTCPayNetworkProvider _networkProvider;
    private bool _started;

    public FlorestaNonBitcoinNbxplorerConnectionHostedService(
        NBXplorerConnectionFactory connectionFactory,
        BTCPayNetworkProvider networkProvider)
    {
        _connectionFactory = connectionFactory;
        _networkProvider = networkProvider;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_networkProvider.GetAll().OfType<BTCPayNetwork>().Any(network => !network.IsBTC))
            return Task.CompletedTask;

        _started = true;
        return ((IHostedService)_connectionFactory).StartAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return _started
            ? ((IHostedService)_connectionFactory).StopAsync(cancellationToken)
            : Task.CompletedTask;
    }
}
