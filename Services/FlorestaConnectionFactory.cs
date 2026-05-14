using BTCPayServer.Logging;
using BTCPayServer.Services;

namespace BTCPayServer.Plugins.Floresta.Services;

public class FlorestaConnectionFactory : NBXplorerConnectionFactory
{
    public FlorestaConnectionFactory()
        : base(Microsoft.Extensions.Options.Options.Create(
            new BTCPayServer.Configuration.NBXplorerOptions()), new Logs())
    {
        Available = false;
    }
}
