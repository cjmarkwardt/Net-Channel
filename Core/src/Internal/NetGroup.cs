namespace Markwardt.NetChannel.Internal;

/// <summary>
/// Shared base for <see cref="INetGroup"/>'s tracked-entity-set behavior, implemented by
/// <see cref="NetGroupAll"/> and <see cref="NetView"/>, which each supply their own notion of audience.
/// </summary>
internal abstract class NetGroupBase(NetManager manager) : INetGroup
{
    private readonly HashSet<INetEntity> entities = [];

    private readonly Lock entitiesGate = new();

    /// <summary>
    /// The manager this group belongs to.
    /// </summary>
    protected NetManager Manager { get; } = manager;

    /// <inheritdoc />
    public IReadOnlySet<INetEntity> Entities
    {
        get
        {
            lock (entitiesGate)
            {
                return entities.ToHashSet();
            }
        }
    }

    /// <inheritdoc />
    public void Add<TModel, TController>(params IEnumerable<INetEntity<TModel, TController>> entities)
        where TModel : class
        where TController : class
    {
        NetModelType modelType = Manager.TypeCache.GetModel(typeof(TModel));
        NetControllerType controllerType = Manager.TypeCache.GetController(typeof(TController));

        foreach (INetEntity<TModel, TController> entity in entities)
        {
            lock (entitiesGate)
            {
                this.entities.Add(entity);
            }

            Manager.RegisterEntityGroup(entity, modelType, controllerType, this);
        }
    }

    /// <inheritdoc />
    public void Remove(params IEnumerable<INetEntity> entities)
    {
        foreach (INetEntity entity in entities)
        {
            bool removed;

            lock (entitiesGate)
            {
                removed = this.entities.Remove(entity);
            }

            if (removed)
            {
                Manager.UnregisterEntityGroup(entity, this);
            }
        }
    }

    /// <summary>
    /// Checks whether a connection is currently part of this group's audience.
    /// </summary>
    /// <param name="connection">The connection to check.</param>
    /// <returns><see langword="true"/> if it is in the audience; otherwise, <see langword="false"/>.</returns>
    public abstract bool ContainsViewer(NetConnection connection);

    /// <summary>
    /// Gets a snapshot of every connection currently in this group's audience.
    /// </summary>
    /// <returns>The snapshot.</returns>
    public abstract IReadOnlyCollection<NetConnection> GetAudienceSnapshot();

    /// <summary>
    /// Gets the effective position/range this group uses for position-based visibility filtering for a connection.
    /// </summary>
    /// <param name="connection">The connection to get the effective position for.</param>
    /// <returns>The effective position, or <see langword="null"/> if none applies.</returns>
    public abstract NetPosition? GetEffectivePosition(NetConnection connection);
}

/// <summary>
/// The implicit group backing <see cref="INetManager.All"/>, whose audience is every currently active connection.
/// </summary>
internal sealed class NetGroupAll(NetManager manager) : NetGroupBase(manager)
{
    /// <inheritdoc />
    public override bool ContainsViewer(NetConnection connection) => connection.Status == NetStatus.Connected;

    /// <inheritdoc />
    public override IReadOnlyCollection<NetConnection> GetAudienceSnapshot() => Manager.GetActiveConnectionsSnapshot();

    /// <inheritdoc />
    public override NetPosition? GetEffectivePosition(NetConnection connection) => connection.Position;
}

/// <inheritdoc cref="INetView" />
internal sealed class NetView(NetManager manager, object? tag) : NetGroupBase(manager), INetView
{
    private readonly HashSet<NetConnection> viewers = [];

    private readonly Dictionary<NetConnection, NetPosition?> positionOverrides = [];

    private readonly Lock viewersGate = new();

    /// <inheritdoc />
    public object? Tag { get; } = tag;

    /// <inheritdoc />
    public IReadOnlySet<INetConnection> Viewers
    {
        get
        {
            lock (viewersGate)
            {
                return viewers.ToHashSet<INetConnection>();
            }
        }
    }

    /// <inheritdoc />
    public void Destroy() => Manager.DestroyView(this);

    /// <inheritdoc />
    public void AddViewers(params IEnumerable<INetConnection> viewers)
    {
        foreach (NetConnection connection in viewers.Select(RequireOwnConnection))
        {
            lock (viewersGate)
            {
                this.viewers.Add(connection);
            }

            Manager.ReevaluateConnection(connection);
        }
    }

    /// <inheritdoc />
    public void RemoveViewers(params IEnumerable<INetConnection> viewers)
    {
        foreach (NetConnection connection in viewers.Select(RequireOwnConnection))
        {
            lock (viewersGate)
            {
                this.viewers.Remove(connection);
            }

            Manager.ReevaluateConnection(connection);
        }
    }

    /// <inheritdoc />
    public NetPosition? GetViewerPosition(INetConnection connection)
    {
        NetConnection typed = RequireOwnConnection(connection);

        lock (viewersGate)
        {
            return positionOverrides.TryGetValue(typed, out NetPosition? position) ? position : null;
        }
    }

    /// <inheritdoc />
    public void SetViewerPosition(INetConnection connection, NetPosition? position)
    {
        NetConnection typed = RequireOwnConnection(connection);

        lock (viewersGate)
        {
            if (position is null)
            {
                positionOverrides.Remove(typed);
            }
            else
            {
                positionOverrides[typed] = position;
            }
        }

        Manager.ReevaluateConnection(typed);
    }

    /// <summary>
    /// Empties this view's audience, for use when the view itself is destroyed, so that anything added to it
    /// afterward is exposed to nobody rather than to whoever happened to still be listed as a viewer.
    /// </summary>
    public void ClearViewers()
    {
        lock (viewersGate)
        {
            viewers.Clear();
            positionOverrides.Clear();
        }
    }

    /// <summary>
    /// Removes a connection from this view without triggering re-evaluation, for use when the connection itself
    /// has disconnected.
    /// </summary>
    /// <param name="connection">The connection to remove.</param>
    public void RemoveViewerSilently(NetConnection connection)
    {
        lock (viewersGate)
        {
            viewers.Remove(connection);
            positionOverrides.Remove(connection);
        }
    }

    /// <inheritdoc />
    public override bool ContainsViewer(NetConnection connection)
    {
        lock (viewersGate)
        {
            return viewers.Contains(connection);
        }
    }

    /// <inheritdoc />
    public override IReadOnlyCollection<NetConnection> GetAudienceSnapshot()
    {
        lock (viewersGate)
        {
            return viewers.ToArray();
        }
    }

    /// <inheritdoc />
    public override NetPosition? GetEffectivePosition(NetConnection connection) => GetViewerPosition(connection) ?? connection.Position;

    private NetConnection RequireOwnConnection(INetConnection connection) =>
        connection as NetConnection ?? throw new ArgumentException($"'{connection.GetType()}' is not a connection created by this library.", nameof(connection));
}
