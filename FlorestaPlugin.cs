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
using Microsoft.Extensions.Hosting;

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
        services.AddSingleton<FlorestaDescriptorRegistry>();
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
        RemoveHostedService<NBXplorerConnectionFactory>(services);
        RemoveByImplementation<NBXplorerConnectionFactory>(services);

        // NBXplorerListener (hosted service)
        RemoveHostedService<NBXplorerListener>(services);

        // NBXplorerWaiters (hosted service)
        RemoveHostedService<NBXplorerWaiters>(services);

        // NBXplorerDashboard
        RemoveByImplementation<NBXplorerDashboard>(services);

        // ISyncSummaryProvider
        RemoveByServiceType<ISyncSummaryProvider>(services);

        // FeeProviderFactory (singleton + interface + scheduled task)
        RemoveScheduledTask<FeeProviderFactory>(services);
        RemoveByImplementation<FeeProviderFactory>(services);
        RemoveByServiceType<IFeeProviderFactory>(services);


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
        var descriptors = services.Where(d => (d.ImplementationType is not null &&
                                               typeof(T).IsAssignableFrom(d.ImplementationType)) ||
                                              d.ImplementationInstance is T ||
                                              (d.ImplementationFactory?.Method.ReturnType is { } returnType &&
                                               typeof(T).IsAssignableFrom(returnType)) ||
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
        var descriptors = services
            .Where(d => IsScheduledTaskDescriptorFor<T>(services, d))
            .ToList();

        foreach (var d in descriptors)
            services.Remove(d);
    }

    private static bool IsScheduledTaskDescriptorFor<T>(IServiceCollection services, ServiceDescriptor descriptor)
    {
        if (descriptor.ServiceType != typeof(ScheduledTask) ||
            descriptor.ImplementationFactory is null)
            return false;

        var scheduledTaskIndex = services.IndexOf(descriptor);
        if (scheduledTaskIndex <= 0)
            return false;

        return IsServiceRegistrationFor<T>(services[scheduledTaskIndex - 1]);
    }

    private static void RemoveHostedService<T>(IServiceCollection services)
        where T : class, IHostedService
    {
        var descriptors = services
            .Where(d => IsHostedServiceDescriptorFor<T>(services, d))
            .ToList();

        foreach (var d in descriptors)
        {
            services.Remove(d);
        }
    }

    private static bool IsHostedServiceDescriptorFor<T>(IServiceCollection services, ServiceDescriptor descriptor)
        where T : class, IHostedService
    {
        if (descriptor.ServiceType != typeof(IHostedService))
            return false;

        if (descriptor.ImplementationType is not null)
            return typeof(T).IsAssignableFrom(descriptor.ImplementationType);

        if (descriptor.ImplementationInstance is not null)
            return descriptor.ImplementationInstance is T;

        if (descriptor.ImplementationFactory is null)
            return false;

        var hostedServiceIndex = services.IndexOf(descriptor);
        if (hostedServiceIndex <= 0)
            return false;

        return IsServiceRegistrationFor<T>(services[hostedServiceIndex - 1]);
    }

    private static bool IsServiceRegistrationFor<T>(ServiceDescriptor descriptor)
    {
        return descriptor.ServiceType == typeof(T) ||
               (descriptor.ImplementationType is not null && typeof(T).IsAssignableFrom(descriptor.ImplementationType)) ||
               descriptor.ImplementationInstance is T;
    }
}
