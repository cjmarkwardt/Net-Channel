namespace Markwardt.NetChannel.Internal;

/// <summary>
/// A remote entity a connection has made visible, bound to the untyped operations <see cref="NetManager"/>
/// needs to apply incoming messages to it, regardless of its actual <c>TModel</c>/<c>TController</c>.
/// </summary>
internal sealed class NetRemoteEntityBinding
{
    /// <summary>
    /// The id this connection uses to refer to the entity.
    /// </summary>
    public required ulong EntityId { get; init; }

    /// <summary>
    /// The id this connection uses to refer to the entity's model interface type.
    /// </summary>
    public required uint ModelId { get; init; }

    /// <summary>
    /// The id this connection uses to refer to the entity's controller interface type.
    /// </summary>
    public required uint ControllerId { get; init; }

    /// <summary>
    /// The validated shape of the entity's model interface, for resolving an incoming property's id.
    /// </summary>
    public required NetModelType ModelType { get; init; }

    /// <summary>
    /// The validated shape of the entity's controller interface, for resolving an outgoing call's method id.
    /// </summary>
    public required NetControllerType ControllerType { get; init; }

    /// <summary>
    /// The untyped remote entity proxy.
    /// </summary>
    public required INetRemoteEntity RemoteEntity { get; init; }

    /// <summary>
    /// Applies an incoming property value to the entity's local cache, by the property's fixed index on
    /// <see cref="ModelType"/> — this side's own index for it, already resolved from the id the sender used.
    /// </summary>
    public required Action<uint, ReadOnlyMemory<byte>> ApplyProperty { get; init; }

    /// <summary>
    /// Marks the entity as no longer visible, triggering <see cref="INetRemoteEntity.Destroyed"/>.
    /// </summary>
    public required Action NotifyDestroyed { get; init; }
}

/// <summary>
/// Type-erased access to a registration made via <see cref="INetManager.Listen{TModel, TController}"/>,
/// implemented by <see cref="NetListenerBinding{TModel, TController}"/>.
/// </summary>
internal interface INetListenerBinding
{
    /// <summary>
    /// The validated shape of the listened-for model interface.
    /// </summary>
    NetModelType ModelType { get; }

    /// <summary>
    /// The validated shape of the listened-for controller interface.
    /// </summary>
    NetControllerType ControllerType { get; }

    /// <summary>
    /// Creates a remote entity proxy for a newly visible entity matching this registration, notifying every
    /// currently registered listener with it.
    /// </summary>
    /// <param name="manager">The manager the entity is visible through.</param>
    /// <param name="connection">The connection the entity became visible on.</param>
    /// <param name="entityId">The id this connection uses to refer to the entity.</param>
    /// <param name="modelId">The id this connection uses to refer to the entity's model interface type.</param>
    /// <param name="controllerId">The id this connection uses to refer to the entity's controller interface type.</param>
    /// <returns>The binding created for the new remote entity.</returns>
    NetRemoteEntityBinding CreateEntity(NetManager manager, NetConnection connection, ulong entityId, uint modelId, uint controllerId);
}

/// <inheritdoc cref="INetListenerBinding" />
internal sealed class NetListenerBinding<TModel, TController>(NetModelType modelType, NetControllerType controllerType) : INetListenerBinding
    where TModel : class
    where TController : class
{
    private readonly List<Action<INetRemoteEntity<TModel, TController>>> listeners = [];

    private readonly Lock gate = new();

    /// <inheritdoc />
    public NetModelType ModelType { get; } = modelType;

    /// <inheritdoc />
    public NetControllerType ControllerType { get; } = controllerType;

    /// <summary>
    /// Adds a listener to be notified of every entity created from now on. Entities already created for this
    /// registration are not replayed to it, matching <see cref="INetManager.Listen{TModel, TController}"/>.
    /// </summary>
    /// <param name="listener">The listener to add.</param>
    /// <returns>A handle that removes the listener when disposed.</returns>
    public IDisposable AddListener(Action<INetRemoteEntity<TModel, TController>> listener)
    {
        lock (gate)
        {
            listeners.Add(listener);
        }

        return new NetListenerHandle(() =>
        {
            lock (gate)
            {
                listeners.Remove(listener);
            }
        });
    }

    /// <inheritdoc />
    public NetRemoteEntityBinding CreateEntity(NetManager manager, NetConnection connection, ulong entityId, uint modelId, uint controllerId)
    {
        NetRemoteState<TModel> state = new(ModelType, manager, connection, entityId);
        NetRemoteEntity<TModel, TController> entity = new(manager, connection, entityId, controllerId, ControllerType, state);

        lock (gate)
        {
            foreach (Action<INetRemoteEntity<TModel, TController>> listener in listeners)
            {
                listener(entity);
            }
        }

        return new NetRemoteEntityBinding
        {
            EntityId = entityId,
            ModelId = modelId,
            ControllerId = controllerId,
            ModelType = ModelType,
            ControllerType = ControllerType,
            RemoteEntity = entity,
            ApplyProperty = (propertyId, value) =>
            {
                // A property id this side's own model doesn't declare can only come from a peer built against
                // a different version of the interface (or a hostile one); ignoring it keeps the message
                // acknowledged like any other, rather than throwing back into the receive loop and leaving the
                // sender retransmitting something that can never be applied.
                if (propertyId >= ModelType.Properties.Count)
                {
                    return;
                }

                NetModelMember member = ModelType.Properties[(int)propertyId];
                state.ApplyRemote(member.Name, manager.ValueCodec.Decode(member.Property.PropertyType, value.Span));
            },
            NotifyDestroyed = entity.NotifyDestroyed,
        };
    }

    private sealed class NetListenerHandle(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
