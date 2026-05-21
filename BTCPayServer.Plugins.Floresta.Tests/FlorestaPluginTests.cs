using BTCPayServer.Plugins.Floresta.Filters;
using BTCPayServer.Plugins.Floresta.Services;
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
}
