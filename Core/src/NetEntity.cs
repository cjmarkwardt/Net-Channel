namespace Markwardt.NetChannel;

/// <summary>
/// Marker for a local net entity of any model/controller type, implemented by <see cref="INetEntity{TModel, TController}"/>.
/// Lets <see cref="INetGroup.Add"/>/<see cref="INetGroup.Remove"/> accept entities of any model/controller type uniformly.
/// </summary>
public interface INetEntity
{
    /// <summary>
    /// The entity's model state, in its untyped form. See
    /// <see cref="INetEntity{TModel, TController}.State"/> for the strongly-typed view.
    /// </summary>
    INetState State { get; }

    /// <summary>
    /// Whether <see cref="Destroy"/> has been called on this entity, after which it should not be used.
    /// </summary>
    bool IsDestroyed { get; }

    /// <summary>
    /// Triggered the first time the entity is destroyed via <see cref="Destroy"/>, after which it should not
    /// be used.
    /// </summary>
    IObservable<INetEntity> Destroyed { get; }

    /// <summary>
    /// Destroys the entity and triggers <see cref="Destroyed"/>, also removing it from every
    /// <see cref="INetGroup"/> it belongs to so that it stops being exposed to any connection. The entity
    /// should not be used afterward. Only the first call has any effect; every call after that is a no-op.
    /// </summary>
    void Destroy();
}

/// <summary>
/// A local net entity, defined by the combination of a model interface, a controller interface, and an
/// implementation. Implemented by <see cref="NetEntity{TModel, TController}"/>.
/// </summary>
/// <typeparam name="TModel">
/// The entity's model interface: a set of properties, each with a public getter and setter, representing the
/// entity's network-visible state.
/// </typeparam>
/// <typeparam name="TController">
/// The entity's controller interface: a set of methods, each returning <see langword="void"/> or a result
/// wrapped in a <see cref="Task"/>, representing the controls other peers can invoke on the entity.
/// </typeparam>
public interface INetEntity<TModel, TController> : INetEntity
    where TModel : class
    where TController : class
{
    /// <summary>
    /// The entity's model state: storage for its <typeparamref name="TModel"/> property values, and a source
    /// of change notifications for them.
    /// </summary>
    new INetState<TModel> State { get; }
}

/// <summary>
/// Base class for a local net entity's implementation. A deriving class must itself implement
/// <typeparamref name="TController"/>, supplying the real behavior invoked when a remote peer calls a
/// controller method; that implementation, together with <see cref="State"/>, gives the entity complete
/// control over what happens when it is called, and when its model properties are got, set, or observed.
/// Because the deriving class implements <see cref="INetEntity{TModel, TController}"/>, its
/// <typeparamref name="TModel"/>/<typeparamref name="TController"/> are given explicitly at the
/// <see cref="INetGroup.Add"/> call site rather than found by inspecting its runtime type.
/// </summary>
/// <typeparam name="TModel">
/// The entity's model interface: a set of properties, each with a public getter and setter, representing the
/// entity's network-visible state.
/// </typeparam>
/// <typeparam name="TController">
/// The entity's controller interface: a set of methods, each returning <see langword="void"/> or a result
/// wrapped in a <see cref="Task"/>, representing the controls other peers can invoke on the entity. The
/// deriving class must implement this interface.
/// </typeparam>
/// <param name="state">The entity's model state.</param>
public abstract class NetEntity<TModel, TController>(INetState<TModel> state) : INetEntity<TModel, TController>
    where TModel : class
    where TController : class
{
    private readonly ReplaySubject<INetEntity> destroyed = new(1);

    private int isDestroyed;

    /// <inheritdoc cref="INetEntity{TModel, TController}.State" />
    public INetState<TModel> State { get; } = state;

    INetState INetEntity.State => State;

    /// <inheritdoc />
    public bool IsDestroyed => Volatile.Read(ref isDestroyed) != 0;

    /// <inheritdoc />
    public IObservable<INetEntity> Destroyed => destroyed;

    /// <inheritdoc />
    public void Destroy()
    {
        if (Interlocked.CompareExchange(ref isDestroyed, 1, 0) != 0)
        {
            return;
        }

        destroyed.OnNext(this);
        destroyed.OnCompleted();
    }
}
