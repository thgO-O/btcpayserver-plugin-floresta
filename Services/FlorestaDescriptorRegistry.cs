using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace BTCPayServer.Plugins.Floresta.Services;

public class FlorestaDescriptorRegistry(
    FlorestaDescriptorService descriptorService,
    FlorestaRpcClient rpcClient)
{
    public async Task<FlorestaDescriptorRegistrationResult> RegisterAsync(
        string cryptoCode,
        string derivationStrategy,
        IReadOnlySet<string> loadedDescriptors,
        FlorestaRpcClient rpcClient,
        CancellationToken ct,
        Func<CancellationToken, Task> beforeDescriptorLoad = null)
    {
        var alreadyRegistered = 0;
        var registered = 0;
        FlorestaDescriptorSet descriptors = null;

        try
        {
            ct.ThrowIfCancellationRequested();
            descriptors = descriptorService.CreateDescriptors(cryptoCode, derivationStrategy);

            var loaded = loadedDescriptors is null
                ? (await rpcClient.ListDescriptorsAsync(ct) ?? []).ToHashSet(StringComparer.Ordinal)
                : loadedDescriptors.ToHashSet(StringComparer.Ordinal);

            foreach (var descriptor in new[] { descriptors.ReceiveDescriptor, descriptors.ChangeDescriptor })
            {
                if (loaded.Contains(descriptor))
                {
                    alreadyRegistered++;
                    continue;
                }

                if (beforeDescriptorLoad is not null)
                    await beforeDescriptorLoad(ct);
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new FlorestaDescriptorRegistrationResult(descriptors, alreadyRegistered, registered, ex.Message);
        }
    }

    public Task<FlorestaDescriptorRegistrationResult> RegisterAsync(
        string cryptoCode,
        string derivationStrategy,
        CancellationToken ct,
        Func<CancellationToken, Task> beforeDescriptorLoad = null)
    {
        return RegisterAsync(cryptoCode, derivationStrategy, null, rpcClient, ct, beforeDescriptorLoad);
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
