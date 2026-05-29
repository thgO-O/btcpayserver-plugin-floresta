using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Floresta.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Floresta.Services;

public sealed class FlorestaDbMigrationHostedService : IHostedService
{
    private readonly FlorestaDbContextFactory _dbFactory;
    private readonly ILogger<FlorestaDbMigrationHostedService> _logger;

    public FlorestaDbMigrationHostedService(
        FlorestaDbContextFactory dbFactory,
        ILogger<FlorestaDbMigrationHostedService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var ctx = _dbFactory.CreateContext();
        await ctx.Database.MigrateAsync(cancellationToken);
        _logger.LogInformation("Floresta plugin DB schema migrated");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
