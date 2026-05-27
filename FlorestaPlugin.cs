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
using BTCPayServer.Services.Reporting;
using BTCPayServer.Services.Wallets;
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

        // NBXplorerListener (hosted service)
        RemoveHostedService<NBXplorerListener>(services);

        // NBXplorerDashboard
        RemoveByImplementation<NBXplorerDashboard>(services);

        // Replace only the default NBXplorer sync summary. Other summary providers
        // registered by BTCPay or plugins are unrelated.
        RemoveByImplementation<NBXSyncSummaryProvider>(services);

        // These DB-backed services can still use NBXplorer for non-BTC networks,
        // but must not query NBXplorer's BTC rows when BTC is backed by Floresta.
        RemoveByImplementation<OnChainWalletReportProvider>(services);
        RemoveByServiceType<WalletHistogramService>(services);

        // FeeProviderFactory interface only. Keep the concrete factory and its
        // scheduled task for non-BTC networks; FlorestaFeeProviderFactory wraps it.
        RemoveByServiceType<IFeeProviderFactory>(services);

        // Keep the concrete NBXplorer connection factory for non-BTC reports and
        // wallet queries, but do not let BTCPay start it for BTC-only Floresta mode.
        RemoveHostedService<NBXplorerConnectionFactory>(services);

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
        services.AddSingleton<FlorestaDbMigrationHostedService>();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp =>
            sp.GetRequiredService<FlorestaDbMigrationHostedService>());

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

        // Fee estimation
        services.AddSingleton<FlorestaFeeProvider>();
        services.AddSingleton<FlorestaFeeProviderFactory>();
        services.AddSingleton<IFeeProviderFactory>(sp => sp.GetRequiredService<FlorestaFeeProviderFactory>());

        // NBXplorer DB-backed UX for non-BTC networks only.
        services.AddSingleton<FlorestaNonBitcoinNbxplorerConnectionHostedService>();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp =>
            sp.GetRequiredService<FlorestaNonBitcoinNbxplorerConnectionHostedService>());
        services.AddSingleton<FlorestaOnChainWalletReportProvider>();
        services.AddSingleton<ReportProvider>(sp => sp.GetRequiredService<FlorestaOnChainWalletReportProvider>());
        services.AddSingleton<WalletHistogramService, FlorestaWalletHistogramService>();

        // BTC status monitoring. NBXplorerWaiters stays registered for non-BTC networks.
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp => sp.GetRequiredService<FlorestaStatusMonitor>());

        // Payment listener (replaces NBXplorerListener)
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService, FlorestaListener>();

        // Sync summary
        services.AddSingleton<FlorestaSyncSummaryProvider>();
        services.AddSingleton<ISyncSummaryProvider>(sp => sp.GetRequiredService<FlorestaSyncSummaryProvider>());
        services.AddSingleton<FlorestaNonBitcoinSyncSummaryProvider>();
        services.AddSingleton<ISyncSummaryProvider>(sp => sp.GetRequiredService<FlorestaNonBitcoinSyncSummaryProvider>());

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
