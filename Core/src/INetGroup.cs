namespace Markwardt.NetChannel;

/// <summary>
/// A group of tracked local net entities exposed to some audience of connections. Implemented by
/// <see cref="INetView"/>, whose audience is an explicit, caller-managed set of viewers, and by
/// <see cref="INetManager.All"/>, whose audience is implicitly every active connection. An entity relationship
/// only ever exists between a local entity's owner and its direct connections — there is no way to relay an
/// entity onward through an intermediate peer.
/// </summary>
public interface INetGroup
{
    /// <summary>
    /// The entities currently in the group.
    /// </summary>
    IReadOnlySet<INetEntity> Entities { get; }

    /// <summary>
    /// Adds local entities of a single model/controller type to the group, exposing them to its audience of
    /// connections. <typeparamref name="TModel"/>/<typeparamref name="TController"/> are given explicitly by
    /// the caller here, rather than found by inspecting each entity's runtime type.
    /// The first time an entity becomes visible to a given connection in the audience, that connection is
    /// assigned a sequential entity id for the entity, and, for each of its model and controller interface
    /// types it hasn't seen before, a sequential id for that type (all three sequences are independent per
    /// connection, scoped to this manager). An entity creation message is then sent to that connection
    /// containing the entity id, the model/controller type ids, each type's full name (only included the
    /// first time that type's id is used for the connection, so the connection can map it going forward —
    /// later entities of the same model/controller type on that connection omit whichever it already knows),
    /// and the entity's model property values that differ from their type's default.
    /// </summary>
    /// <typeparam name="TModel">The model interface of the entities to add.</typeparam>
    /// <typeparam name="TController">The controller interface of the entities to add.</typeparam>
    /// <param name="entities">The entities to add.</param>
    /// <exception cref="NetInvalidInterfaceException">
    /// <typeparamref name="TModel"/>/<typeparamref name="TController"/> is used here for the first time
    /// anywhere and does not have the shape required of a model/controller interface.
    /// </exception>
    void Add<TModel, TController>(params IEnumerable<INetEntity<TModel, TController>> entities)
        where TModel : class
        where TController : class;

    /// <summary>
    /// Removes entities from the group.
    /// </summary>
    /// <param name="entities">The entities to remove.</param>
    void Remove(params IEnumerable<INetEntity> entities);
}
