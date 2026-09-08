namespace Markwardt.NetChannel.Internal;

/// <inheritdoc cref="INetStateFactory" />
internal sealed class NetStateFactory(INetEntityTypeCache typeCache) : INetStateFactory
{
    /// <inheritdoc />
    public INetState<TModel> Create<TModel>()
        where TModel : class
        => new NetState<TModel>(typeCache.GetModel(typeof(TModel)));
}
