using System;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace BTCPayServer.Plugins.Floresta.Data;

public class FlorestaDbContextFactory : BaseDbContextFactory<FlorestaDbContext>
{
    public FlorestaDbContextFactory(IOptions<DatabaseOptions> options)
        : base(options, "BTCPayServer.Plugins.Floresta")
    {
    }

    public override FlorestaDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder> npgsqlOptionsAction = null)
    {
        var builder = new DbContextOptionsBuilder<FlorestaDbContext>();
        builder.AddInterceptors(MigrationInterceptor.Instance);
        ConfigureBuilder(builder, npgsqlOptionsAction);
        return new FlorestaDbContext(builder.Options);
    }
}

public class DesignTimeFlorestaDbContextFactory : IDesignTimeDbContextFactory<FlorestaDbContext>
{
    public FlorestaDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<FlorestaDbContext>();
        builder.UseNpgsql("Host=localhost;Database=btcpayserver");
        return new FlorestaDbContext(builder.Options);
    }
}
