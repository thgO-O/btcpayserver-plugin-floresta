using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Floresta.Services;

public class FlorestaDescriptorRegistry
{
    private readonly FlorestaDescriptorService _descriptorService;
    private readonly FlorestaRpcClient _rpcClient;

    public FlorestaDescriptorRegistry(
        FlorestaDescriptorService descriptorService,
        FlorestaRpcClient rpcClient)
    {
        _descriptorService = descriptorService;
        _rpcClient = rpcClient;
    }

    public async Task<FlorestaDescriptorRegistrationResult> RegisterAsync(
        string cryptoCode,
        string derivationStrategy,
        IReadOnlySet<string> loadedDescriptors,
        CancellationToken ct)
    {
        return await RegisterAsync(cryptoCode, derivationStrategy, loadedDescriptors, _rpcClient, ct);
    }

    public async Task<FlorestaDescriptorRegistrationResult> RegisterAsync(
        string cryptoCode,
        string derivationStrategy,
        IReadOnlySet<string> loadedDescriptors,
        FlorestaRpcClient rpcClient,
        CancellationToken ct)
    {
        var descriptors = _descriptorService.CreateDescriptors(cryptoCode, derivationStrategy);
        var alreadyRegistered = 0;
        var registered = 0;

        try
        {
            var loaded = loadedDescriptors is null
                ? (await rpcClient.ListDescriptorsAsync(ct) ?? Array.Empty<string>()).ToHashSet(StringComparer.Ordinal)
                : loadedDescriptors.ToHashSet(StringComparer.Ordinal);

            foreach (var descriptor in new[] { descriptors.ReceiveDescriptor, descriptors.ChangeDescriptor })
            {
                if (loaded.Contains(descriptor))
                {
                    alreadyRegistered++;
                    continue;
                }

                var loadedDescriptor = await rpcClient.LoadDescriptorAsync(descriptor, ct);
                if (!loadedDescriptor)
                {
                    return new FlorestaDescriptorRegistrationResult(
                        descriptors,
                        alreadyRegistered,
                        registered,
                        $"Floresta rejected descriptor {descriptors.DescriptorHash}");
                }

                loaded.Add(descriptor);
                registered++;
            }

            return new FlorestaDescriptorRegistrationResult(descriptors, alreadyRegistered, registered, null);
        }
        catch (Exception ex)
        {
            return new FlorestaDescriptorRegistrationResult(descriptors, alreadyRegistered, registered, ex.Message);
        }
    }

    public Task<FlorestaDescriptorRegistrationResult> RegisterAsync(
        string cryptoCode,
        string derivationStrategy,
        CancellationToken ct)
    {
        return RegisterAsync(cryptoCode, derivationStrategy, null, ct);
    }
}

public sealed record FlorestaDescriptorRegistrationResult(
    FlorestaDescriptorSet Descriptors,
    int AlreadyRegistered,
    int Registered,
    string Error)
{
    public bool Succeeded => string.IsNullOrEmpty(Error);
}
