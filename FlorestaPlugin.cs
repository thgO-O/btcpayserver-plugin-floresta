using System;
using System.Linq;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.HostedServices;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Plugins.Floresta.Data;
using BTCPayServer.Plugins.Floresta.Filters;
using BTCPayServer.Plugins.Floresta.Services;
using BTCPayServer.Services;
using BTCPayServer.Services.Fees;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.Floresta;

public class FlorestaPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.3.7" }
    };

    public override void Execute(IServiceCollection services)
    {
        var replaceBackend = FlorestaBackendMode.IsBackendReplacementEnabled();

        services.AddSingleton<FlorestaElectrumClient>();
        services.AddSingleton<FlorestaRpcClient>();
        services.AddSingleton<FlorestaDescriptorService>();
        services.AddSingleton<FlorestaStatusMonitor>();
        services.AddUIExtension("server-nav", "/Views/Shared/Floresta/NavExtension.cshtml");

        if (!replaceBackend)
            return;

        // ──────────────────────────────────────────────
        // 1. Remove NBXplorer services
        // ──────────────────────────────────────────────

        // ExplorerClientProvider (singleton + interface registration)
        RemoveByImplementation<ExplorerClientProvider>(services);
        RemoveByServiceType<Common.IExplorerClientProvider>(services);

        // NBXplorerConnectionFactory (singleton + hosted service)
        RemoveByImplementation<NBXplorerConnectionFactory>(services);
        RemoveHostedService<NBXplorerConnectionFactory>(services);

        // NBXplorerListener (hosted service)
        RemoveHostedService<NBXplorerListener>(services);

        // NBXplorerWaiters (hosted service)
        RemoveHostedService<NBXplorerWaiters>(services);

        // NBXplorerDashboard
        RemoveByImplementation<NBXplorerDashboard>(services);

        // ISyncSummaryProvider
        RemoveByServiceType<ISyncSummaryProvider>(services);

        // FeeProviderFactory (singleton + interface + scheduled task)
        RemoveByImplementation<FeeProviderFactory>(services);
        RemoveByServiceType<IFeeProviderFactory>(services);
        RemoveScheduledTask<FeeProviderFactory>(services);


        // ──────────────────────────────────────────────
        // 2. Register Floresta engine
        // ──────────────────────────────────────────────

        services.AddSingleton<FlorestaWalletTracker>();

        // DB context
        services.AddSingleton<FlorestaDbContextFactory>();
        services.AddDbContext<FlorestaDbContext>((provider, o) =>
        {
            var factory = provider.GetRequiredService<FlorestaDbContextFactory>();
            factory.ConfigureBuilder(o);
        });

        // HTTP handler for shimming ExplorerClient calls
        services.AddSingleton<FlorestaHttpHandler>();

        // ──────────────────────────────────────────────
        // 4. Register shadow services
        // ──────────────────────────────────────────────

        // NBXplorerDashboard - reuse same type, we populate it from FlorestaStatusMonitor
        services.AddSingleton<NBXplorerDashboard>();

        // ExplorerClientProvider replacement
        services.AddSingleton<FlorestaExplorerClientProvider>();
        services.AddSingleton<ExplorerClientProvider>(sp => sp.GetRequiredService<FlorestaExplorerClientProvider>());
        services.AddSingleton<Common.IExplorerClientProvider>(sp => sp.GetRequiredService<FlorestaExplorerClientProvider>());

        // NBXplorerConnectionFactory replacement (Available = false)
        services.AddSingleton<FlorestaConnectionFactory>();
        services.AddSingleton<NBXplorerConnectionFactory>(sp => sp.GetRequiredService<FlorestaConnectionFactory>());

        // Fee estimation
        services.AddSingleton<FlorestaFeeProvider>();
        services.AddSingleton<FlorestaFeeProviderFactory>();
        services.AddSingleton<IFeeProviderFactory>(sp => sp.GetRequiredService<FlorestaFeeProviderFactory>());

        // Status monitoring (replaces NBXplorerWaiters)
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp => sp.GetRequiredService<FlorestaStatusMonitor>());

        // Payment listener (replaces NBXplorerListener)
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService, FlorestaListener>();

        // Sync summary
        services.AddSingleton<FlorestaSyncSummaryProvider>();
        services.AddSingleton<ISyncSummaryProvider>(sp => sp.GetRequiredService<FlorestaSyncSummaryProvider>());

        // ──────────────────────────────────────────────
        // 5. Admin UI
        // ──────────────────────────────────────────────

        services.AddUIExtension("onchain-wallet-setup-post-body", "/Views/Shared/Floresta/WatchOnlyWalletSetup.cshtml");
        services.AddScoped<FlorestaWatchOnlyWalletSetupFilter>();
        services.Configure<MvcOptions>(options =>
            options.Filters.AddService<FlorestaWatchOnlyWalletSetupFilter>());
    }

    private static void RemoveByImplementation<T>(IServiceCollection services)
    {
        var descriptors = services.Where(d => d.ImplementationType == typeof(T) ||
                                              (d.ImplementationFactory?.Method.ReturnType == typeof(T)) ||
                                              d.ServiceType == typeof(T)).ToList();
        foreach (var d in descriptors)
            services.Remove(d);
    }

    private static void RemoveByServiceType<T>(IServiceCollection services)
    {
        var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var d in descriptors)
            services.Remove(d);
    }

    private static void RemoveScheduledTask<T>(IServiceCollection services)
    {
        var descriptors = services.Where(d =>
            d.ServiceType == typeof(ScheduledTask) &&
            d.ImplementationFactory != null).ToList();
        foreach (var d in descriptors)
        {
            // ScheduledTask factories capture the type in their closure.
            // Instantiate to check PeriodicTaskType.
            try
            {
                var instance = (ScheduledTask)d.ImplementationFactory(null!);
                if (instance.PeriodicTaskType == typeof(T))
                    services.Remove(d);
            }
            catch
            {
                // Factory might need a real provider — skip
            }
        }
    }

    private static void RemoveHostedService<T>(IServiceCollection services)
    {
        var descriptors = services.Where(d =>
            d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) &&
            (d.ImplementationType == typeof(T) ||
             d.ImplementationFactory != null)).ToList();

        // Be selective - only remove if the implementation type matches
        foreach (var d in descriptors)
        {
            if (d.ImplementationType == typeof(T))
            {
                services.Remove(d);
            }
            else if (d.ImplementationFactory != null)
            {
                // Check if the factory resolves to our type
                // For factories like: sp => sp.GetRequiredService<T>()
                // The return type or generic args might indicate the type
                var factoryMethod = d.ImplementationFactory.Method;
                if (factoryMethod.ReturnType == typeof(T) ||
                    factoryMethod.ToString()?.Contains(typeof(T).Name) == true)
                {
                    services.Remove(d);
                }
            }
        }
    }
}
