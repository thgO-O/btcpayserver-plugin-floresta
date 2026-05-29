using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Common;
using BTCPayServer.Plugins.Floresta.Filters;
using BTCPayServer.Plugins.Floresta.Services;
using BTCPayServer.HostedServices;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Services;
using BTCPayServer.Services.Fees;
using BTCPayServer.Services.Reporting;
using BTCPayServer.Services.Wallets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using static BTCPayServer.Plugins.Floresta.Tests.EnvironmentVariableTestHelper;
using Xunit;

namespace BTCPayServer.Plugins.Floresta.Tests;

public class FlorestaPluginTests
{
    [Fact]
    public void DoesNotRegisterBackendReplacementServicesWhenStartupGateIsDisabled()
    {
        WithBackendReplacementEnvironment(null, () =>
        {
            var services = new ServiceCollection();

            new FlorestaPlugin().Execute(services);

            Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(FlorestaElectrumClient));
            Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(FlorestaRpcClient));
            Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(FlorestaDescriptorService));
            Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(FlorestaStatusMonitor));

            Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(FlorestaWalletTracker));
            Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(FlorestaHttpHandler));
            Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(FlorestaWatchOnlyWalletSetupFilter));
            Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
        });
    }

    [Fact]
    public void RegistersBackendReplacementServicesWhenStartupGateIsEnabled()
    {
        WithBackendReplacementEnvironment("true", () =>
        {
            var services = new ServiceCollection();

            new FlorestaPlugin().Execute(services);

            Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(FlorestaWalletTracker));
            Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(FlorestaHttpHandler));
            Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(FlorestaWatchOnlyWalletSetupFilter));
            Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
        });
    }

    [Fact]
    public void ReplacesBitcoinNbxplorerServicesWhenStartupGateIsEnabled()
    {
        WithBackendReplacementEnvironment("true", () =>
        {
            var services = new ServiceCollection();
            RegisterCoreLikeNbxplorerServices(services);
            var unrelatedFactoryInvoked = false;
            services.AddSingleton<IHostedService>(_ =>
            {
                unrelatedFactoryInvoked = true;
                return new UnrelatedHostedService();
            });

            new FlorestaPlugin().Execute(services);

            Assert.False(unrelatedFactoryInvoked);
            Assert.DoesNotContain(services, descriptor =>
                descriptor.ServiceType == typeof(IExplorerClientProvider) &&
                descriptor.ImplementationType == typeof(ExplorerClientProvider));
            Assert.DoesNotContain(services, descriptor =>
                IsHostedServiceRegistrationFor<NBXplorerListener>(services, descriptor));
            Assert.DoesNotContain(services, descriptor =>
                descriptor.ServiceType == typeof(IFeeProviderFactory) &&
                descriptor.ImplementationType == typeof(FeeProviderFactory));
            Assert.DoesNotContain(services, descriptor =>
                descriptor.ServiceType == typeof(ISyncSummaryProvider) &&
                descriptor.ImplementationType == typeof(NBXSyncSummaryProvider));
            Assert.DoesNotContain(services, descriptor =>
                descriptor.ServiceType == typeof(ReportProvider) &&
                descriptor.ImplementationType == typeof(OnChainWalletReportProvider));
            Assert.DoesNotContain(services, descriptor =>
                descriptor.ServiceType == typeof(WalletHistogramService) &&
                descriptor.ImplementationType == typeof(WalletHistogramService));

            Assert.Single(services, descriptor => descriptor.ServiceType == typeof(NBXplorerDashboard));
            Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(ExplorerClientProvider));
            Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IExplorerClientProvider));
            Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IFeeProviderFactory));
            Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(FeeProviderFactory));
            Assert.Contains(services, descriptor => IsScheduledTaskRegistrationFor<FeeProviderFactory>(services, descriptor));
            Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(FlorestaStatusMonitor));
            Assert.Contains(services, descriptor =>
                descriptor.ServiceType == typeof(IHostedService) &&
                descriptor.ImplementationFactory is not null);
            Assert.Contains(services, descriptor =>
                descriptor.ServiceType == typeof(NBXplorerConnectionFactory));
            Assert.DoesNotContain(services, descriptor =>
                IsHostedServiceRegistrationFor<NBXplorerConnectionFactory>(services, descriptor));
            Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(FlorestaNonBitcoinNbxplorerConnectionHostedService));
            Assert.Contains(services, descriptor =>
                IsHostedServiceRegistrationFor<FlorestaNonBitcoinNbxplorerConnectionHostedService>(services, descriptor));
            Assert.Contains(services, descriptor =>
                IsHostedServiceRegistrationFor<NBXplorerWaiters>(services, descriptor));
            Assert.Contains(services, descriptor =>
                IsHostedServiceRegistrationFor<FlorestaListener>(services, descriptor));
            Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(FlorestaNonBitcoinSyncSummaryProvider));
            Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(FlorestaOnChainWalletReportProvider));
            Assert.Contains(services, descriptor =>
                descriptor.ServiceType == typeof(ReportProvider) &&
                descriptor.ImplementationFactory is not null);
            Assert.Contains(services, descriptor =>
                descriptor.ServiceType == typeof(WalletHistogramService) &&
                descriptor.ImplementationType == typeof(FlorestaWalletHistogramService));
            Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(FlorestaDbMigrationHostedService));
            Assert.Contains(services, descriptor =>
                IsHostedServiceRegistrationFor<FlorestaDbMigrationHostedService>(services, descriptor));
            Assert.Contains(services, descriptor =>
                IsHostedServiceRegistrationFor<UnrelatedHostedService>(services, descriptor));
            Assert.Contains(services, descriptor =>
                IsScheduledTaskRegistrationFor<UnrelatedPeriodicTask>(services, descriptor));
            Assert.False(unrelatedFactoryInvoked);
        });
    }

    private static void RegisterCoreLikeNbxplorerServices(IServiceCollection services)
    {
        services.AddSingleton<ExplorerClientProvider>();
        services.AddSingleton<IExplorerClientProvider, ExplorerClientProvider>();
        services.AddSingleton<NBXplorerConnectionFactory>();
        services.AddSingleton<IHostedService, NBXplorerConnectionFactory>(
            provider => provider.GetRequiredService<NBXplorerConnectionFactory>());
        services.AddSingleton<IHostedService, NBXplorerListener>();
        services.AddSingleton<IHostedService, NBXplorerWaiters>();
        services.AddSingleton<NBXplorerDashboard>();
        services.AddSingleton<ISyncSummaryProvider, NBXSyncSummaryProvider>();
        services.AddSingleton<OnChainWalletReportProvider>();
        services.AddSingleton<ReportProvider, OnChainWalletReportProvider>();
        services.AddSingleton<WalletHistogramService>();
        services.AddSingleton<FeeProviderFactory>();
        services.AddTransient(_ => new ScheduledTask(typeof(FeeProviderFactory), TimeSpan.FromMinutes(3)));
        services.AddSingleton<IFeeProviderFactory, FeeProviderFactory>();

        services.AddSingleton<UnrelatedHostedService>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<UnrelatedHostedService>());
        services.AddSingleton<UnrelatedPeriodicTask>();
        services.AddTransient(_ => new ScheduledTask(typeof(UnrelatedPeriodicTask), TimeSpan.FromMinutes(5)));
    }

    private static bool IsHostedServiceRegistrationFor<T>(IServiceCollection services, ServiceDescriptor descriptor)
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
        return hostedServiceIndex > 0 &&
               IsServiceRegistrationFor<T>(services[hostedServiceIndex - 1]);
    }

    private static bool IsScheduledTaskRegistrationFor<T>(IServiceCollection services, ServiceDescriptor descriptor)
    {
        if (descriptor.ServiceType != typeof(ScheduledTask) ||
            descriptor.ImplementationFactory is null)
            return false;

        var scheduledTaskIndex = services.IndexOf(descriptor);
        return scheduledTaskIndex > 0 &&
               IsServiceRegistrationFor<T>(services[scheduledTaskIndex - 1]);
    }

    private static bool IsServiceRegistrationFor<T>(ServiceDescriptor descriptor)
    {
        return descriptor.ServiceType == typeof(T) ||
               (descriptor.ImplementationType is not null && typeof(T).IsAssignableFrom(descriptor.ImplementationType)) ||
               descriptor.ImplementationInstance is T;
    }

    private sealed class UnrelatedHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class UnrelatedPeriodicTask : IPeriodicTask
    {
        public Task Do(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
