namespace Markwardt.NetChannel.Internal;

/// <summary>
/// A runtime-generated <typeparamref name="TModel"/> instance that dispatches every property get/set to an
/// <see cref="INetState"/>. Not sealed: <see cref="DispatchProxy"/> generates a subclass of this type at
/// runtime for each closed <typeparamref name="TModel"/>.
/// </summary>
/// <typeparam name="TModel">The model interface to implement.</typeparam>
internal class NetModelProxy<TModel> : DispatchProxy
    where TModel : class
{
    private INetState state = null!;

    /// <summary>
    /// Creates a <typeparamref name="TModel"/> instance backed by the given state.
    /// </summary>
    /// <param name="state">The state to dispatch every get/set to.</param>
    /// <returns>The created instance.</returns>
    public static TModel Create(INetState state)
    {
        TModel proxy = Create<TModel, NetModelProxy<TModel>>();
        ((NetModelProxy<TModel>)(object)proxy).state = state;
        return proxy;
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null)
        {
            return null;
        }

        if (targetMethod.Name.StartsWith("get_", StringComparison.Ordinal))
        {
            return state.Get(targetMethod.Name["get_".Length..]);
        }

        state.Set(targetMethod.Name["set_".Length..], args![0]);
        return null;
    }
}
