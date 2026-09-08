namespace Markwardt.NetChannel.Internal;

/// <summary>
/// A runtime-generated <typeparamref name="TController"/> instance that dispatches every method call to a
/// delegate. Not sealed: <see cref="DispatchProxy"/> generates a subclass of this type at runtime for each
/// closed <typeparamref name="TController"/>.
/// </summary>
/// <typeparam name="TController">The controller interface to implement.</typeparam>
internal class NetControllerProxy<TController> : DispatchProxy
    where TController : class
{
    private Func<MethodInfo, object?[], object?> invoke = null!;

    /// <summary>
    /// Creates a <typeparamref name="TController"/> instance that dispatches every call to the given delegate.
    /// </summary>
    /// <param name="invoke">The delegate to dispatch every call to.</param>
    /// <returns>The created instance.</returns>
    public static TController Create(Func<MethodInfo, object?[], object?> invoke)
    {
        TController proxy = Create<TController, NetControllerProxy<TController>>();
        ((NetControllerProxy<TController>)(object)proxy).invoke = invoke;
        return proxy;
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => invoke(targetMethod!, args ?? []);
}
