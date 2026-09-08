namespace Markwardt.NetChannel.Internal;

/// <summary>
/// Which handshake step this side of an outgoing connection attempt is currently waiting on a reply to.
/// </summary>
internal enum NetHandshakeStage
{
    /// <summary>
    /// Waiting for a <see cref="HandshakeChallenge"/>/<see cref="HandshakeReject"/> in reply to a <see cref="HandshakeInit"/>.
    /// </summary>
    AwaitingChallenge,

    /// <summary>
    /// Waiting for a <see cref="HandshakeAccept"/>/<see cref="HandshakeReject"/> in reply to a <see cref="HandshakeResponse"/>.
    /// </summary>
    AwaitingAccept,
}

/// <summary>
/// Fixed context for an outgoing property-set channel, cached alongside the channel itself so a resend doesn't
/// need to re-derive which model/property it belongs to.
/// </summary>
/// <param name="Channel">The channel's resend-tracking state.</param>
/// <param name="ModelId">The id this connection uses to refer to the property's model interface type.</param>
/// <param name="Member">The property.</param>
/// <param name="ReportFailureAsRemote">
/// Whether a decline should be raised through <see cref="INetManager.Failed"/> against a
/// <see cref="INetRemoteEntity"/> — <see langword="true"/> when this side is a viewer requesting the change,
/// <see langword="false"/> when this side is the entity's owner broadcasting it.
/// </param>
internal sealed record NetOutgoingPropertyContext(NetPropertyChannel Channel, uint ModelId, NetModelMember Member, bool ReportFailureAsRemote)
{
    /// <summary>
    /// The remote entity a decline is reported against, captured when the channel is opened rather than looked
    /// up when one arrives — by then the entity may already have stopped being visible, which is itself one of
    /// the reasons a decline is reported. Null when this side owns the entity and has nothing to report against.
    /// </summary>
    public INetRemoteEntity? RemoteEntity { get; init; }

    /// <summary>
    /// Serializes choosing the value to send with allocating the sequence to send it under, so that a packet
    /// carrying a newer value always carries a newer sequence. The receiver discards an out-of-order set by
    /// comparing sequences (Docs/Wire.md#property-delivery), which only works if the two orders agree.
    /// </summary>
    public Lock Gate { get; } = new();
}

/// <summary>
/// Fixed context for an outgoing method call channel, cached alongside the channel itself so a resend doesn't
/// need to re-derive which controller/method it belongs to.
/// </summary>
/// <param name="Channel">The channel's queuing/resend-tracking state.</param>
/// <param name="ControllerId">The id this connection uses to refer to the method's controller interface type.</param>
/// <param name="Member">The method.</param>
internal sealed record NetOutgoingCallContext(NetMethodCallerChannel Channel, uint ControllerId, NetControllerMember Member)
{
    /// <summary>
    /// The remote entity a failure is reported against, captured when the channel is opened, on the same terms
    /// as <see cref="NetOutgoingPropertyContext.RemoteEntity"/>.
    /// </summary>
    public INetRemoteEntity? RemoteEntity { get; init; }
}

/// <inheritdoc cref="INetConnection" />
internal sealed class NetConnection(NetManager manager, NetDirection direction, string host, int port, object? tag) : INetConnection
{
    private readonly Lock rolesGate = new();

    private readonly HashSet<string> roles = [];

    private readonly Lock stateGate = new();

    private ulong outgoingSequence;

    private NetStatus status = NetStatus.Connecting;

    private NetPosition? position;

    private TimeSpan latency;

    private ulong nextEntityId = 1;

    private uint nextModelId;

    private uint nextControllerId;

    /// <summary>
    /// This side's ephemeral key pair used during the handshake, retained only until the handshake completes
    /// or fails.
    /// </summary>
    public NetAgreementKey? AgreementKey { get; set; }

    /// <summary>
    /// The remote peer's source endpoint. Updated on any validly authenticated packet so the connection
    /// survives a NAT rebind or network switch.
    /// </summary>
    public IPEndPoint? RemoteEndPoint { get; set; }

    /// <summary>
    /// The server-assigned id used as <c>Packet.connection_id</c>, or <see langword="null"/> before the
    /// handshake completes.
    /// </summary>
    public ulong? ConnectionId { get; set; }

    /// <summary>
    /// This connection's authentication key, derived once the handshake completes.
    /// </summary>
    public ReadOnlyMemory<byte> SessionKey { get; set; }

    /// <summary>
    /// This connection's <c>[NetSecure]</c> payload encryption key, derived once the handshake completes.
    /// </summary>
    public ReadOnlyMemory<byte> PayloadKey { get; set; }

    /// <summary>
    /// The cookie the server issued in its <see cref="HandshakeChallenge"/>, echoed back in this client's <see cref="HandshakeResponse"/>.
    /// </summary>
    public byte[]? PendingCookie { get; set; }

    /// <summary>
    /// The server's ephemeral public key learned from its <see cref="HandshakeChallenge"/>.
    /// </summary>
    public ReadOnlyMemory<byte>? ServerPublicKey { get; set; }

    /// <summary>
    /// Which handshake step this client-side connection attempt is currently waiting on a reply to, or
    /// <see langword="null"/> once the handshake has completed.
    /// </summary>
    public NetHandshakeStage? HandshakeStage { get; set; }

    /// <summary>
    /// The client's expected identity key for Pinned mode, or <see langword="null"/> for Unauthenticated mode.
    /// </summary>
    public ReadOnlyMemory<byte>? ExpectedIdentityKey { get; set; }

    /// <summary>
    /// Why this connection ended, recorded before any status/failure event fires so a
    /// <see cref="Wait"/> resolving off <see cref="INetManager.StatusChanged"/> reports the same reason
    /// <see cref="INetManager.Rejected"/> would, or <see langword="null"/> if it ended locally and gracefully.
    /// </summary>
    public Exception? FailureReason { get; set; }

    /// <summary>
    /// The cookie from the <see cref="HandshakeResponse"/> this incoming connection was accepted from, used to
    /// recognize a retransmission of that same response as referring to this connection rather than a new one.
    /// </summary>
    public string? HandshakeCookie { get; set; }

    /// <summary>
    /// Tracks the highest sequence accepted from the remote peer so far, for replay rejection.
    /// </summary>
    public NetReplayWindow IncomingReplay { get; } = new();

    /// <summary>
    /// The last time any packet was sent on this connection, for liveness ping scheduling.
    /// </summary>
    public DateTime LastSentAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The last time any packet was received on this connection, for disconnect timeout tracking.
    /// </summary>
    public DateTime LastReceivedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Whether a <see cref="Ping"/> is currently awaiting its <see cref="Reply"/>, gating whether another is
    /// due to be sent.
    /// </summary>
    public bool HasOutstandingPing { get; set; }

    /// <summary>
    /// When the outstanding ping was sent, for round-trip latency measurement.
    /// </summary>
    public DateTime PendingPingSentAt { get; set; }

    /// <summary>
    /// Callbacks awaiting a <see cref="Reply"/> keyed by the sequence they were last sent with, alongside the
    /// entity id they belong to (for entity-scoped cancellation), or <see langword="null"/> for a connection-scoped one.
    /// </summary>
    public ConcurrentDictionary<ulong, (ulong? EntityId, Action<Reply> Callback)> PendingReplies { get; } = new();

    /// <summary>
    /// Local entities currently exposed to this connection, and the id assigned to each.
    /// </summary>
    public ConcurrentDictionary<INetEntity, ulong> OutgoingEntityIds { get; } = new();

    /// <summary>
    /// The reverse of <see cref="OutgoingEntityIds"/>, for resolving an incoming message's <c>entity_id</c>
    /// back to the local entity this connection is a viewer of.
    /// </summary>
    public ConcurrentDictionary<ulong, INetEntity> OutgoingEntitiesById { get; } = new();

    /// <summary>
    /// The lifecycle channel for each local entity currently exposed to this connection.
    /// </summary>
    public ConcurrentDictionary<ulong, NetEntityLifecycle> OutgoingLifecycles { get; } = new();

    /// <summary>
    /// The model interface type id assigned to each model interface type used by an entity exposed to this connection.
    /// </summary>
    public ConcurrentDictionary<Type, uint> OutgoingModelIds { get; } = new();

    /// <summary>
    /// The controller interface type id assigned to each controller interface type used by an entity exposed to this connection.
    /// </summary>
    public ConcurrentDictionary<Type, uint> OutgoingControllerIds { get; } = new();

    /// <summary>
    /// Which (model id, property id) pairs this connection has acknowledged receiving the name of, for entities
    /// this side owns. The name keeps being included until then, rather than only on the first attempt, since
    /// the packet introducing it can be lost like any other and the receiver would otherwise never learn the
    /// mapping.
    /// </summary>
    public ConcurrentDictionary<(uint ModelId, uint PropertyId), bool> SentOwnedPropertyNames { get; } = new();

    /// <summary>
    /// The same as <see cref="SentOwnedPropertyNames"/>, for entities this side only views. Kept apart because
    /// each side numbers the interfaces of the entities it owns from zero independently, so one model id means
    /// a different interface in each direction.
    /// </summary>
    public ConcurrentDictionary<(uint ModelId, uint PropertyId), bool> SentViewedPropertyNames { get; } = new();

    /// <summary>
    /// Which (controller id, method id) pairs this connection has acknowledged receiving the name of, on the
    /// same terms as <see cref="SentOwnedPropertyNames"/>.
    /// </summary>
    public ConcurrentDictionary<(uint ControllerId, uint MethodId), bool> SentMethodNames { get; } = new();

    /// <summary>
    /// Outgoing property-set channels for entities this side owns and broadcasts changes to, keyed by the
    /// entity id this side assigned and the property's fixed index.
    /// </summary>
    public ConcurrentDictionary<(ulong EntityId, uint PropertyId), NetOutgoingPropertyContext> OwnedPropertyChannels { get; } = new();

    /// <summary>
    /// Outgoing property-set channels for entities this side only views and requests changes to, keyed by the
    /// entity id the owner assigned. Kept apart from <see cref="OwnedPropertyChannels"/> because each side
    /// numbers the entities it owns from zero independently, so one entity id refers to a different entity in
    /// each direction.
    /// </summary>
    public ConcurrentDictionary<(ulong EntityId, uint PropertyId), NetOutgoingPropertyContext> ViewedPropertyChannels { get; } = new();

    /// <summary>
    /// Outgoing method call channels, one per (entity id, method id) this connection is calling as a viewer.
    /// </summary>
    public ConcurrentDictionary<(ulong EntityId, uint MethodId), NetOutgoingCallContext> MethodCallerChannels { get; } = new();

    /// <summary>
    /// Incoming method call dedup state, one per (entity id, method id) this connection calls as a viewer of a
    /// locally owned entity.
    /// </summary>
    public ConcurrentDictionary<(ulong EntityId, uint MethodId), NetMethodReceiverChannel> MethodReceiverChannels { get; } = new();

    /// <summary>
    /// Remote entities this connection has made visible, keyed by the id it assigned each one, or
    /// <see langword="null"/> if the id is known but no <see cref="INetManager.Listen{TModel, TController}"/>
    /// registration matched it.
    /// </summary>
    public ConcurrentDictionary<ulong, NetRemoteEntityBinding?> RemoteEntities { get; } = new();

    /// <summary>
    /// The model interface type name introduced for each model id this connection has told us about.
    /// </summary>
    public ConcurrentDictionary<uint, string> IncomingModelNames { get; } = new();

    /// <summary>
    /// The controller interface type name introduced for each controller id this connection has told us about.
    /// </summary>
    public ConcurrentDictionary<uint, string> IncomingControllerNames { get; } = new();

    /// <summary>
    /// The property name introduced for each (model id, property id) this connection has told us about for an
    /// entity it owns (Docs/Wire.md#entities). The name, not the id's numeric value, is what resolves the
    /// property against this side's own model interface, so the two peers agree even if their reflected member
    /// order doesn't.
    /// </summary>
    public ConcurrentDictionary<(uint ModelId, uint PropertyId), string> IncomingPropertyNames { get; } = new();

    /// <summary>
    /// The same as <see cref="IncomingPropertyNames"/>, but for names introduced while requesting a set on an
    /// entity this side owns. Kept apart because each side numbers the entities it owns from zero
    /// independently, so one model id can mean a different interface in each direction.
    /// </summary>
    public ConcurrentDictionary<(uint ModelId, uint PropertyId), string> RequestedPropertyNames { get; } = new();

    /// <summary>
    /// The method name introduced for each (controller id, method id) this connection has told us about, on
    /// the same terms as <see cref="IncomingPropertyNames"/>.
    /// </summary>
    public ConcurrentDictionary<(uint ControllerId, uint MethodId), string> IncomingMethodNames { get; } = new();

    /// <summary>
    /// The highest <c>Packet.sequence</c> an incoming <c>EntitySet</c> has been accepted with for a given
    /// (entity id, property id) on an entity this side only views, so a re-ordered older packet for the same
    /// property never overwrites a newer value already applied — property delivery has no per-property
    /// ordering guarantee of its own (Docs/Wire.md#property-delivery), so this is enforced on receipt instead.
    /// </summary>
    public ConcurrentDictionary<(ulong EntityId, uint PropertyId), ulong> ViewedPropertySequences { get; } = new();

    /// <summary>
    /// The same as <see cref="ViewedPropertySequences"/>, for incoming change requests on entities this side
    /// owns, keyed by the entity id this side assigned. Kept apart for the same reason
    /// <see cref="ViewedPropertyChannels"/> is.
    /// </summary>
    public ConcurrentDictionary<(ulong EntityId, uint PropertyId), ulong> OwnedPropertySequences { get; } = new();

    /// <inheritdoc />
    public NetStatus Status
    {
        get
        {
            lock (stateGate)
            {
                return status;
            }
        }
    }

    /// <inheritdoc />
    public NetDirection Direction { get; } = direction;

    /// <inheritdoc />
    public string Host { get; } = host;

    /// <inheritdoc />
    public int Port { get; } = port;

    /// <inheritdoc />
    public object? Tag { get; } = tag;

    /// <inheritdoc />
    public IReadOnlySet<string> Roles
    {
        get
        {
            lock (rolesGate)
            {
                return roles.ToHashSet();
            }
        }
    }

    /// <inheritdoc />
    public TimeSpan Latency
    {
        get
        {
            lock (stateGate)
            {
                return latency;
            }
        }
    }

    /// <inheritdoc />
    public NetPosition? Position
    {
        get
        {
            lock (stateGate)
            {
                return position;
            }
        }

        set
        {
            lock (stateGate)
            {
                position = value;
            }

            manager.OnConnectionPositionChanged(this);
        }
    }

    /// <inheritdoc />
    public void Drop() => manager.DropConnection(this, null);

    /// <inheritdoc />
    public Task Wait()
    {
        if (Status == NetStatus.Connected)
        {
            return Task.CompletedTask;
        }

        if (Status == NetStatus.Disconnected)
        {
            return Task.FromException(BuildFailure());
        }

        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        IDisposable statusSubscription = manager.StatusChanged.Subscribe(change =>
        {
            if (change.Connection != this)
            {
                return;
            }

            if (change.Status == NetStatus.Connected)
            {
                completion.TrySetResult();
            }
            else if (change.Status == NetStatus.Disconnected)
            {
                // Resolving off StatusChanged rather than Rejected covers an attempt ended locally
                // (Drop/Disconnect) or by a disposing manager too, none of which raise Rejected at all;
                // FailureReason carries whatever reason Rejected would have reported.
                completion.TrySetException(BuildFailure());
            }
        });

        completion.Task.ContinueWith(
            _ => statusSubscription.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        if (Status == NetStatus.Connected)
        {
            completion.TrySetResult();
        }
        else if (Status == NetStatus.Disconnected)
        {
            completion.TrySetException(BuildFailure());
        }

        return completion.Task;
    }

    /// <inheritdoc />
    public Task Disconnect() => manager.DisconnectConnection(this);

    /// <inheritdoc />
    public void Promote(string role)
    {
        lock (rolesGate)
        {
            roles.Add(role);
        }
    }

    /// <inheritdoc />
    public void Demote(string role)
    {
        lock (rolesGate)
        {
            roles.Remove(role);
        }
    }

    /// <summary>
    /// Sets the connection's current status.
    /// </summary>
    /// <param name="value">The new status.</param>
    public void SetStatus(NetStatus value)
    {
        lock (stateGate)
        {
            status = value;
        }
    }

    /// <summary>
    /// Atomically moves the connection to <see cref="NetStatus.Disconnected"/>, reporting what it was before,
    /// so that two concurrent teardowns can't both decide they were the one that ended it and fire a duplicate
    /// round of events.
    /// </summary>
    /// <param name="previous">The status the connection was in before this call.</param>
    /// <returns><see langword="true"/> if this call was the one that ended it; otherwise, <see langword="false"/>.</returns>
    public bool TryBeginDisconnect(out NetStatus previous)
    {
        lock (stateGate)
        {
            previous = status;

            if (status == NetStatus.Disconnected)
            {
                return false;
            }

            status = NetStatus.Disconnected;
            return true;
        }
    }

    /// <summary>
    /// Builds the exception a <see cref="Wait"/> on an ended connection faults with.
    /// </summary>
    /// <returns><see cref="FailureReason"/>, or a generic failure if it ended without one.</returns>
    public Exception BuildFailure() => FailureReason ?? new NetFailedException("Connection failed.");

    /// <summary>
    /// Sets the connection's currently measured round-trip latency.
    /// </summary>
    /// <param name="value">The new latency.</param>
    public void SetLatency(TimeSpan value)
    {
        lock (stateGate)
        {
            latency = value;
        }
    }

    /// <summary>
    /// Allocates the next sequence for a packet about to be sent on this connection.
    /// </summary>
    /// <returns>The allocated sequence.</returns>
    public ulong AllocateSequence() => Interlocked.Increment(ref outgoingSequence);

    /// <summary>
    /// Assigns (or retrieves the already-assigned) id this connection uses for the given local entity, along
    /// with whether it was just newly assigned.
    /// </summary>
    /// <param name="entity">The local entity.</param>
    /// <returns>The entity's id, and whether it was newly assigned.</returns>
    public (ulong Id, bool IsNew) AssignEntityId(INetEntity entity)
    {
        bool isNew = false;

        ulong id = OutgoingEntityIds.GetOrAdd(entity, _ =>
        {
            isNew = true;
            ulong assigned = Interlocked.Increment(ref nextEntityId) - 1;
            OutgoingEntitiesById[assigned] = entity;
            return assigned;
        });

        return (id, isNew);
    }

    /// <summary>
    /// Assigns (or retrieves the already-assigned) id this connection uses for the given model interface type,
    /// along with whether it was just newly assigned.
    /// </summary>
    /// <param name="type">The model interface type.</param>
    /// <returns>The type's id, and whether it was newly assigned.</returns>
    public (uint Id, bool IsNew) AssignModelId(Type type)
    {
        bool isNew = false;
        uint id = OutgoingModelIds.GetOrAdd(type, _ => { isNew = true; return Interlocked.Increment(ref nextModelId) - 1; });
        return (id, isNew);
    }

    /// <summary>
    /// Assigns (or retrieves the already-assigned) id this connection uses for the given controller interface
    /// type, along with whether it was just newly assigned.
    /// </summary>
    /// <param name="type">The controller interface type.</param>
    /// <returns>The type's id, and whether it was newly assigned.</returns>
    public (uint Id, bool IsNew) AssignControllerId(Type type)
    {
        bool isNew = false;
        uint id = OutgoingControllerIds.GetOrAdd(type, _ => { isNew = true; return Interlocked.Increment(ref nextControllerId) - 1; });
        return (id, isNew);
    }
}
