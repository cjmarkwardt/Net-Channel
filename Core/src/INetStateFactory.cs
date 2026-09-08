namespace Markwardt.NetChannel;

/// <summary>
/// Creates the <see cref="INetState{TModel}"/> backing a local <see cref="NetEntity{TModel, TController}"/>.
/// </summary>
public interface INetStateFactory
{
    /// <summary>
    /// Creates a new, empty state for a local entity's model, validating and caching
    /// <typeparamref name="TModel"/>'s shape the first time it's used anywhere.
    /// </summary>
    /// <typeparam name="TModel">The entity's model interface.</typeparam>
    /// <returns>The created state.</returns>
    /// <exception cref="NetInvalidInterfaceException"><typeparamref name="TModel"/> does not have the shape required of a model interface.</exception>
    INetState<TModel> Create<TModel>()
        where TModel : class;
}
