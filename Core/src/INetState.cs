namespace Markwardt.NetChannel;

/// <summary>
/// The untyped storage backing an <see cref="INetState{TModel}"/>: every property is accessible by name,
/// without needing to know the model interface's exact type at compile time.
/// </summary>
public interface INetState
{
    /// <summary>
    /// The names of every property declared on the model interface this state backs.
    /// </summary>
    IReadOnlySet<string> Properties { get; }

    /// <summary>
    /// Gets the current value of a property by name.
    /// </summary>
    /// <param name="property">The property's name, as it appears in <see cref="Properties"/>.</param>
    /// <returns>The property's current value.</returns>
    object? Get(string property);

    /// <summary>
    /// Sets the value of a property by name.
    /// </summary>
    /// <param name="property">The property's name, as it appears in <see cref="Properties"/>.</param>
    /// <param name="value">The value to store.</param>
    void Set(string property, object? value);

    /// <summary>
    /// Observes changes to a single property by name.
    /// </summary>
    /// <param name="property">The property's name, as it appears in <see cref="Properties"/>.</param>
    /// <returns>An observable that pushes the property's new value, untyped, whenever it changes.</returns>
    IObservable<object?> Observe(string property);
}

/// <summary>
/// Stores the property values of a local or remote entity's model interface <typeparamref name="TModel"/>,
/// and makes each property's changes observable. Implemented via reflection over <typeparamref name="TModel"/>
/// and a dynamic object that implements it by dispatching every get/set to this state's
/// <see cref="INetState.Get"/>/<see cref="INetState.Set"/> storage.
/// </summary>
/// <typeparam name="TModel">
/// The entity's model interface: a set of properties, each with a public getter and setter, representing the
/// entity's network-visible state.
/// </typeparam>
public interface INetState<TModel> : INetState
    where TModel : class
{
    /// <summary>
    /// A dynamically implemented instance of <typeparamref name="TModel"/> backed by this state: getting a
    /// property returns its currently stored value, and setting one stores the new value and pushes it to
    /// any <see cref="Observe{T}"/>/<see cref="INetState.Observe"/> subscribers. This is the unrestricted
    /// local view of the state; a
    /// <see cref="NetAccessAttribute"/>/<see cref="NetRoleAttribute"/> on a property only restricts which
    /// remote peers may set it, not access through this property, and never restricts getting it.
    /// </summary>
    TModel Model { get; }

    /// <summary>
    /// Observes changes to a single property of <see cref="Model"/>. The strongly-typed counterpart of
    /// <see cref="INetState.Observe"/>.
    /// </summary>
    /// <typeparam name="T">The property's value type.</typeparam>
    /// <param name="selector">An expression selecting the property to observe, e.g. <c>x => x.Position</c>.</param>
    /// <returns>An observable that pushes the property's new value whenever it changes.</returns>
    IObservable<T> Observe<T>(Expression<Func<TModel, T>> selector);
}
