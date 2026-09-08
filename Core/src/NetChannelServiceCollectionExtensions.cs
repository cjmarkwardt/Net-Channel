namespace Markwardt.NetChannel;

/// <summary>
/// Registers Net-Channel's services into an <see cref="IServiceCollection"/>.
/// </summary>
public static class NetChannelServiceCollectionExtensions
{
    /// <summary>
    /// Adds a singleton <see cref="INetManager"/>, and its supporting internal services, to the service collection.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    public static IServiceCollection AddNetChannel(this IServiceCollection services)
    {
        services.TryAddSingleton<INetCrypto, NetCrypto>();
        services.TryAddSingleton<INetValueCodec, NetValueCodec>();
        services.TryAddSingleton<INetPayloadCodec, NetPayloadCodec>();
        services.TryAddSingleton<INetEntityTypeCache, NetEntityTypeCache>();
        services.TryAddSingleton<INetDatagramCodec, NetDatagramCodec>();
        services.TryAddSingleton<INetStateFactory, NetStateFactory>();
        services.AddSingleton<INetManager, NetManager>();
        return services;
    }
}
