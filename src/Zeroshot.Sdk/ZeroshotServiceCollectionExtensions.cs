using Zeroshot;
using Zeroshot.Native;

namespace Microsoft.Extensions.DependencyInjection;

public static class ZeroshotServiceCollectionExtensions
{
    /// <summary>
    /// Registers a singleton <see cref="NativeClient"/> over the SDK's own transport and a singleton
    /// <see cref="ZeroshotClient"/> over it. The container disposes both; disposal never stops a run.
    /// </summary>
    public static IServiceCollection AddZeroshotClient(this IServiceCollection services, ZeroshotClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        return services.AddZeroshotClient(_ => options);
    }

    /// <summary>As <see cref="AddZeroshotClient(IServiceCollection, ZeroshotClientOptions)"/>, with options built once from the container.</summary>
    public static IServiceCollection AddZeroshotClient(this IServiceCollection services,
        Func<IServiceProvider, ZeroshotClientOptions> options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(provider => new RegisteredOptions(
            options(provider) ?? throw new InvalidOperationException("The ZeroshotClientOptions factory returned null.")));
        services.AddSingleton(provider =>
        {
            var registered = provider.GetRequiredService<RegisteredOptions>().Value;
            return NativeClient.ForHttp(new NativeClientOptions { Origin = registered.Target, Transport = registered.Transport });
        });
        services.AddSingleton(provider =>
        {
            var registered = provider.GetRequiredService<RegisteredOptions>().Value;
            return new ZeroshotClient(provider.GetRequiredService<NativeClient>(), registered.NativeBinding, ownsClient: false,
                registered.TargetCredentials, registered.Observation);
        });
        return services;
    }

    private sealed record RegisteredOptions(ZeroshotClientOptions Value);
}
