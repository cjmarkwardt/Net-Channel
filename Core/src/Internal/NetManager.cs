namespace Markwardt.NetChannel.Internal;

/// <inheritdoc cref="INetManager" />
internal sealed class NetManager : INetManager
{
    private static readonly byte[] EphemeralContextPrefix = "netchannel ephemeral v1"u8.ToArray();

    private static readonly byte[] SessionKeyInfo = "netchannel packet-auth v1"u8.ToArray();

    private static readonly byte[] PayloadKeyInfo = "netchannel payload-encryption v1"u8.ToArray();

    private static readonly TimeSpan HandshakeRetryInterval = TimeSpan.FromMilliseconds(500);

    private static readonly int IdentityKeyLength = 32;

    private static readonly TimeSpan SweepInterval = TimeSpan.FromMilliseconds(100);

    private readonly INetCrypto crypto;

    private readonly INetDatagramCodec datagramCodec;

    private readonly byte[] serverSecret;

    private readonly Lock socketGate = new();

    private readonly CancellationTokenSource lifetimeCancellation = new();

    private readonly Task sweepLoopTask;

    private readonly Subject<NetStatusChange> statusChangedSubject = new();

    private readonly Subject<NetConnectionFailure> rejectedSubject = new();

    private readonly Subject<NetConnectionFailure> droppedSubject = new();

    private readonly Subject<NetRemoteEntityFailure> failedSubject = new();

    private readonly ConcurrentDictionary<NetConnection, byte> allConnections = new();

    private readonly ConcurrentDictionary<ulong, NetConnection> connectionsById = new();

    private readonly ConcurrentDictionary<object, NetConnection> connectionsByTag = new();

    private readonly ConcurrentDictionary<IPEndPoint, NetConnection> pendingOutgoingByEndpoint = new();

    private readonly ConcurrentDictionary<string, NetConnection> incomingConnectionsByCookie = new();

    private readonly ConcurrentDictionary<NetView, byte> views = new();

    private readonly ConcurrentDictionary<object, NetView> viewsByTag = new();

    private readonly ConcurrentDictionary<(string Model, string Controller), INetListenerBinding> listenerBindings = new();

    private readonly ConcurrentDictionary<INetEntity, List<NetGroupBase>> entityGroups = new();

    private readonly ConcurrentDictionary<INetEntity, (NetModelType ModelType, NetControllerType ControllerType)> entityTypes = new();

    private readonly ConcurrentDictionary<INetEntity, List<IDisposable>> entitySubscriptions = new();

    private readonly Lock groupMembershipGate = new();

    private ulong nextConnectionId;

    private Socket? socket;

    private Task? receiveLoopTask;

    private int? hostPort;

    private ReadOnlyMemory<byte>? identityKey;

    private int isDisposed;

    /// <summary>
    /// Initializes a new instance, starting its sweep loop. No socket is bound until <see cref="Host"/> is
    /// called, or until the first outgoing <see cref="Connect"/>, whichever happens first.
    /// </summary>
    /// <param name="crypto">The cryptographic primitives the wire protocol is built from.</param>
    /// <param name="valueCodec">The codec for individual property/argument/result values.</param>
    /// <param name="payloadCodec">The codec for <c>[NetSecure]</c> method payload encryption.</param>
    /// <param name="typeCache">The validated model/controller interface shape cache.</param>
    /// <param name="datagramCodec">The datagram framing codec.</param>
    public NetManager(INetCrypto crypto, INetValueCodec valueCodec, INetPayloadCodec payloadCodec, INetEntityTypeCache typeCache, INetDatagramCodec datagramCodec)
    {
        this.crypto = crypto;
        ValueCodec = valueCodec;
        PayloadCodec = payloadCodec;
        TypeCache = typeCache;
        this.datagramCodec = datagramCodec;

        serverSecret = crypto.GenerateRandom(32);
        All = new NetGroupAll(this);

        sweepLoopTask = Task.Run(() => SweepLoop(lifetimeCancellation.Token));
    }

    /// <inheritdoc />
    public IObservable<NetStatusChange> StatusChanged => statusChangedSubject;

    /// <inheritdoc />
    public IObservable<NetConnectionFailure> Rejected => rejectedSubject;

    /// <inheritdoc />
    public IObservable<NetConnectionFailure> Dropped => droppedSubject;

    /// <inheritdoc />
    public IObservable<NetRemoteEntityFailure> Failed => failedSubject;

    /// <inheritdoc />
    public IReadOnlySet<INetConnection> PendingConnections => allConnections.Keys.Where(connection => connection.Status == NetStatus.Connecting).ToHashSet<INetConnection>();

    /// <inheritdoc />
    public IReadOnlySet<INetConnection> ActiveConnections => allConnections.Keys.Where(connection => connection.Status == NetStatus.Connected).ToHashSet<INetConnection>();

    /// <inheritdoc />
    public IReadOnlySet<INetView> Views => views.Keys.ToHashSet<INetView>();

    /// <inheritdoc />
    public INetGroup All { get; }

    /// <inheritdoc />
    public int? HostPort
    {
        get
        {
            lock (socketGate)
            {
                return hostPort;
            }
        }
    }

    /// <inheritdoc />
    public TimeSpan PingInterval { get; set; } = TimeSpan.FromSeconds(1.5);

    /// <inheritdoc />
    public TimeSpan DisconnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The codec for individual property/argument/result values.
    /// </summary>
    internal INetValueCodec ValueCodec { get; }

    /// <summary>
    /// The codec for <c>[NetSecure]</c> method payload encryption.
    /// </summary>
    internal INetPayloadCodec PayloadCodec { get; }

    /// <summary>
    /// The validated model/controller interface shape cache.
    /// </summary>
    internal INetEntityTypeCache TypeCache { get; }

    /// <summary>
    /// Every local entity currently tracked as belonging to at least one group. An entity is dropped from
    /// this set, along with the rest of its bookkeeping, once it leaves the last group holding it.
    /// </summary>
    internal IReadOnlyCollection<INetEntity> TrackedEntities => entityGroups.Keys.ToArray();

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref isDisposed, 1, 0) != 0)
        {
            return;
        }

        EndAllConnections(gracefully: false);
        lifetimeCancellation.Cancel();
        (Socket? currentSocket, Task? currentReceiveLoop) = CloseSocket();
        List<Task> tasks = [sweepLoopTask];

        if (currentReceiveLoop is not null)
        {
            tasks.Add(currentReceiveLoop);
        }

        try
        {
            Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        lifetimeCancellation.Dispose();
        currentSocket?.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref isDisposed, 1, 0) != 0)
        {
            return;
        }

        EndAllConnections(gracefully: true);
        await lifetimeCancellation.CancelAsync().ConfigureAwait(false);
        (Socket? currentSocket, Task? currentReceiveLoop) = CloseSocket();
        List<Task> tasks = [sweepLoopTask];

        if (currentReceiveLoop is not null)
        {
            tasks.Add(currentReceiveLoop);
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        lifetimeCancellation.Dispose();
        currentSocket?.Dispose();
    }

    private void EndAllConnections(bool gracefully)
    {
        foreach (NetConnection connection in allConnections.Keys)
        {
            try
            {
                // Runs before the socket closes so a graceful teardown's Disconnect can still go out; a
                // connection failing to end must not stop the rest from ending, or the disposal from finishing.
                if (gracefully)
                {
                    DisconnectConnection(connection);
                }
                else
                {
                    DropConnection(connection, null);
                }
            }
            catch (Exception)
            {
            }
        }
    }

    private (Socket? Socket, Task? ReceiveLoop) CloseSocket()
    {
        Socket? currentSocket;
        Task? currentReceiveLoop;

        lock (socketGate)
        {
            currentSocket = socket;
            currentReceiveLoop = receiveLoopTask;
        }

        currentSocket?.Close();
        return (currentSocket, currentReceiveLoop);
    }

    private Socket GetSocket()
    {
        lock (socketGate)
        {
            if (socket is null)
            {
                Socket created = CreateAndBindSocket(IPAddress.Any, 0);
                socket = created;
                receiveLoopTask = Task.Run(() => ReceiveLoop(created, lifetimeCancellation.Token));
            }

            return socket;
        }
    }

    private ReadOnlyMemory<byte>? GetIdentityKey()
    {
        lock (socketGate)
        {
            return identityKey;
        }
    }

    private static Socket CreateAndBindSocket(IPAddress ip, int port)
    {
        Socket created = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        created.Bind(new IPEndPoint(ip, port));
        return created;
    }

    /// <inheritdoc />
    public void Host(int port = 0, ReadOnlyMemory<byte>? identity = null, string host = "0.0.0.0")
    {
        // Checked here rather than where it is first used, which is inside the receive loop while answering a
        // client: throwing there would be swallowed, no challenge would ever be sent, and every client would
        // sit in Connecting forever with nothing reported on either side.
        if (identity is { } key && key.Length != IdentityKeyLength)
        {
            throw new ArgumentException($"An identity key must be {IdentityKeyLength} bytes.", nameof(identity));
        }

        if (!IPAddress.TryParse(host, out IPAddress? address) || address.AddressFamily != AddressFamily.InterNetwork)
        {
            // Sockets here are IPv4 (Docs/Api.md); binding anything else would otherwise surface as an opaque
            // "address family not supported" from the socket layer, or a FormatException for a non-address.
            throw new ArgumentException($"'{host}' is not an IPv4 address.", nameof(host));
        }

        Socket created = CreateAndBindSocket(address, port);
        Socket? previous;

        lock (socketGate)
        {
            previous = socket;
            socket = created;
            hostPort = ((IPEndPoint)created.LocalEndPoint!).Port;
            identityKey = identity;
            receiveLoopTask = Task.Run(() => ReceiveLoop(created, lifetimeCancellation.Token));
        }

        previous?.Close();
        previous?.Dispose();
    }

    /// <inheritdoc />
    public INetConnection Connect(string host, int port, object? tag = null, ReadOnlyMemory<byte>? expectedIdentity = null)
    {
        if (expectedIdentity is { } key && key.Length != IdentityKeyLength)
        {
            throw new ArgumentException($"An identity key must be {IdentityKeyLength} bytes.", nameof(expectedIdentity));
        }

        GetSocket();

        NetConnection connection = new(this, NetDirection.Outgoing, host, port, tag)
        {
            ExpectedIdentityKey = expectedIdentity,
            AgreementKey = crypto.CreateAgreementKey(),
            RemoteEndPoint = ResolveEndPoint(host, port),
        };

        allConnections[connection] = 0;

        if (tag is not null)
        {
            connectionsByTag[tag] = connection;
        }

        pendingOutgoingByEndpoint[connection.RemoteEndPoint!] = connection;
        SendHandshakeInit(connection);
        return connection;
    }

    /// <inheritdoc />
    public INetConnection? GetConnection(object tag) => connectionsByTag.TryGetValue(tag, out NetConnection? connection) ? connection : null;

    /// <inheritdoc />
    public INetView CreateView(object? tag = null)
    {
        NetView view = new(this, tag);
        views[view] = 0;

        if (tag is not null)
        {
            viewsByTag[tag] = view;
        }

        return view;
    }

    /// <inheritdoc />
    public INetView? GetView(object tag) => viewsByTag.TryGetValue(tag, out NetView? view) ? view : null;

    /// <inheritdoc />
    public IDisposable Listen<TModel, TController>(Action<INetRemoteEntity<TModel, TController>> listener)
        where TModel : class
        where TController : class
    {
        NetModelType modelType = TypeCache.GetModel(typeof(TModel));
        NetControllerType controllerType = TypeCache.GetController(typeof(TController));

        NetListenerBinding<TModel, TController> binding = (NetListenerBinding<TModel, TController>)listenerBindings.GetOrAdd(
            (typeof(TModel).FullName!, typeof(TController).FullName!),
            _ => new NetListenerBinding<TModel, TController>(modelType, controllerType));

        return binding.AddListener(listener);
    }

    /// <inheritdoc />
    public void SetSerializer<T>(INetSerializer serializer) => ValueCodec.SetSerializer<T>(serializer);

    /// <summary>
    /// Gets a snapshot of every currently connected connection.
    /// </summary>
    /// <returns>The snapshot.</returns>
    internal IReadOnlyCollection<NetConnection> GetActiveConnectionsSnapshot() =>
        allConnections.Keys.Where(connection => connection.Status == NetStatus.Connected).ToArray();

    /// <summary>
    /// Ends a connection, without waiting for anything further.
    /// </summary>
    /// <param name="connection">The connection to end.</param>
    /// <param name="reason">The failure that caused this, or <see langword="null"/> for a local, graceful end.</param>
    internal void DropConnection(NetConnection connection, Exception? reason)
    {
        connection.FailureReason = reason;

        if (!connection.TryBeginDisconnect(out NetStatus previous))
        {
            return;
        }

        allConnections.TryRemove(connection, out _);

        if (connection.HandshakeCookie is { } cookie)
        {
            incomingConnectionsByCookie.TryRemove(cookie, out _);
        }

        if (connection.ConnectionId is { } id)
        {
            connectionsById.TryRemove(id, out _);
        }

        if (connection.Tag is not null)
        {
            connectionsByTag.TryRemove(connection.Tag, out _);
        }

        if (connection.RemoteEndPoint is not null)
        {
            pendingOutgoingByEndpoint.TryRemove(connection.RemoteEndPoint, out _);
        }

        connection.AgreementKey?.Dispose();

        foreach (NetView view in views.Keys)
        {
            view.RemoveViewerSilently(connection);
        }

        foreach ((ulong? EntityId, Action<Reply> Callback) pending in connection.PendingReplies.Values)
        {
            pending.Callback(new Reply { Failure = "Connection lost." });
        }

        connection.PendingReplies.Clear();

        foreach (ulong entityId in connection.RemoteEntities.Keys)
        {
            connection.RemoteEntities.TryRemove(entityId, out NetRemoteEntityBinding? binding);
            binding?.NotifyDestroyed();
        }

        statusChangedSubject.OnNext(new NetStatusChange { Connection = connection, Status = NetStatus.Disconnected });

        if (reason is not null && previous == NetStatus.Connecting)
        {
            rejectedSubject.OnNext(new NetConnectionFailure { Connection = connection, Exception = reason });
        }
        else if (reason is not null && previous == NetStatus.Connected)
        {
            droppedSubject.OnNext(new NetConnectionFailure { Connection = connection, Exception = reason });
        }
    }

    /// <summary>
    /// Gracefully ends a connection, best-effort notifying the remote peer first.
    /// </summary>
    /// <param name="connection">The connection to end.</param>
    /// <returns>A completed task, once the local side of the teardown is done.</returns>
    internal Task DisconnectConnection(NetConnection connection)
    {
        if (connection.Status == NetStatus.Connected)
        {
            Send(connection, new Packet { Disconnect = new Disconnect() }, trackReply: false, null, null, out _);
        }

        DropConnection(connection, null);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Destroys a view, removing every entity from it first.
    /// </summary>
    /// <param name="view">The view to destroy.</param>
    internal void DestroyView(NetView view)
    {
        view.Remove(view.Entities.ToArray());
        view.ClearViewers();
        views.TryRemove(view, out _);

        if (view.Tag is not null)
        {
            viewsByTag.TryRemove(view.Tag, out _);
        }
    }

    /// <summary>
    /// Registers a local entity as belonging to a group, re-evaluating its visibility to every connection.
    /// </summary>
    /// <param name="entity">The entity.</param>
    /// <param name="modelType">The validated shape of the entity's model interface.</param>
    /// <param name="controllerType">The validated shape of the entity's controller interface.</param>
    /// <param name="group">The group the entity now belongs to.</param>
    internal void RegisterEntityGroup(INetEntity entity, NetModelType modelType, NetControllerType controllerType, NetGroupBase group)
    {
        bool isNewEntity;

        lock (groupMembershipGate)
        {
            isNewEntity = !entityTypes.ContainsKey(entity);
            entityTypes[entity] = (modelType, controllerType);

            if (!entityGroups.TryGetValue(entity, out List<NetGroupBase>? groups))
            {
                groups = [];
                entityGroups[entity] = groups;
            }

            // Recorded at most once per group. A second listing would survive the matching single removal,
            // leaving the entity gone from the group's own set yet still counted as exposed through it.
            if (!groups.Contains(group))
            {
                groups.Add(group);
            }
        }

        if (isNewEntity)
        {
            List<IDisposable> subscriptions = modelType.Properties
                .Select(member => entity.State.Observe(member.Name).Subscribe(_ => OnLocalPropertyChanged(entity, modelType, member)))
                .ToList();

            entitySubscriptions[entity] = subscriptions;
            subscriptions.Add(entity.Destroyed.Subscribe(_ => RetractDestroyedEntity(entity)));
        }

        ReevaluateEntity(entity);
    }

    /// <summary>
    /// Removes a destroyed entity from every group still holding it, so it stops being exposed to anyone.
    /// </summary>
    /// <param name="entity">The destroyed entity.</param>
    internal void RetractDestroyedEntity(INetEntity entity)
    {
        NetGroupBase[] groups;

        lock (groupMembershipGate)
        {
            groups = entityGroups.TryGetValue(entity, out List<NetGroupBase>? current) ? current.ToArray() : [];
        }

        foreach (NetGroupBase group in groups)
        {
            group.Remove(entity);
        }
    }

    /// <summary>
    /// Removes a local entity from a group, re-evaluating its visibility to every connection.
    /// </summary>
    /// <param name="entity">The entity.</param>
    /// <param name="group">The group the entity no longer belongs to.</param>
    internal void UnregisterEntityGroup(INetEntity entity, NetGroupBase group)
    {
        bool isLastGroup;

        lock (groupMembershipGate)
        {
            if (!entityGroups.TryGetValue(entity, out List<NetGroupBase>? groups))
            {
                return;
            }

            groups.Remove(group);
            isLastGroup = groups.Count == 0;
        }

        // Must run while the entity is still tracked, so a pairing that just lost its last granting group is
        // still seen as newly invisible and gets its EntityDestroy sent.
        ReevaluateEntity(entity);

        if (!isLastGroup)
        {
            return;
        }

        lock (groupMembershipGate)
        {
            if (entityGroups.TryGetValue(entity, out List<NetGroupBase>? groups) && groups.Count > 0)
            {
                return;
            }

            entityGroups.TryRemove(entity, out _);
            entityTypes.TryRemove(entity, out _);
        }

        if (entitySubscriptions.TryRemove(entity, out List<IDisposable>? subscriptions))
        {
            foreach (IDisposable subscription in subscriptions)
            {
                subscription.Dispose();
            }
        }
    }

    /// <summary>
    /// Re-evaluates a local entity's visibility against every currently connected connection.
    /// </summary>
    /// <param name="entity">The entity to re-evaluate.</param>
    internal void ReevaluateEntity(INetEntity entity)
    {
        foreach (NetConnection connection in GetActiveConnectionsSnapshot())
        {
            ReevaluatePair(entity, connection);
        }
    }

    /// <summary>
    /// Re-evaluates every currently tracked local entity's visibility against a connection.
    /// </summary>
    /// <param name="connection">The connection to re-evaluate.</param>
    internal void ReevaluateConnection(NetConnection connection)
    {
        foreach (INetEntity entity in entityGroups.Keys)
        {
            ReevaluatePair(entity, connection);
        }
    }

    /// <summary>
    /// Handles a connection's <see cref="INetConnection.Position"/> having changed.
    /// </summary>
    /// <param name="connection">The connection whose position changed.</param>
    internal void OnConnectionPositionChanged(NetConnection connection) => ReevaluateConnection(connection);

    /// <summary>
    /// Sends a viewer's requested property change to a remote entity's owner.
    /// </summary>
    /// <param name="connection">The connection to the entity's owner.</param>
    /// <param name="entityId">The id this connection uses to refer to the entity.</param>
    /// <param name="member">The property to change.</param>
    /// <param name="value">The new value.</param>
    internal void RequestPropertySet(NetConnection connection, ulong entityId, NetModelMember member, object? value)
    {
        if (!connection.RemoteEntities.TryGetValue(entityId, out NetRemoteEntityBinding? binding) || binding is null)
        {
            return;
        }

        ReadOnlyMemory<byte> encoded = ValueCodec.Encode(member.Property.PropertyType, value);
        SendPropertySet(connection, entityId, binding.ModelId, member, () => encoded, reportFailureAsRemote: true, binding.RemoteEntity);
    }

    /// <summary>
    /// Allocates the next operation id for a new call on a method channel, creating the channel's context if
    /// this is the first call ever made on it.
    /// </summary>
    /// <param name="connection">The connection to the entity's owner.</param>
    /// <param name="entityId">The id this connection uses to refer to the entity.</param>
    /// <param name="controllerId">The id this connection uses to refer to the entity's controller interface type.</param>
    /// <param name="member">The method being called.</param>
    /// <param name="remoteEntity">The remote entity proxy a failure on this channel is reported against.</param>
    /// <returns>The allocated operation id.</returns>
    internal ulong AllocateCallOperation(NetConnection connection, ulong entityId, uint controllerId, NetControllerMember member, INetRemoteEntity remoteEntity)
    {
        NetOutgoingCallContext context = connection.MethodCallerChannels.GetOrAdd(
            (entityId, member.Id),
            _ => new NetOutgoingCallContext(new NetMethodCallerChannel(), controllerId, member) { RemoteEntity = remoteEntity });

        return context.Channel.AllocateOperation();
    }

    /// <summary>
    /// Sends (or queues behind an outstanding call) a method call.
    /// </summary>
    /// <param name="connection">The connection to the entity's owner.</param>
    /// <param name="entityId">The id this connection uses to refer to the entity.</param>
    /// <param name="member">The method being called.</param>
    /// <param name="operation">The call's operation id, from <see cref="AllocateCallOperation"/>.</param>
    /// <param name="arguments">The call's encoded (and, if <see cref="NetControllerMember.IsSecure"/>, encrypted) arguments.</param>
    /// <param name="completion">The completion to fulfil with the call's <see cref="Reply"/>, or <see langword="null"/> for a fire-and-forget void call.</param>
    internal void SendCall(NetConnection connection, ulong entityId, NetControllerMember member, ulong operation, IReadOnlyList<ReadOnlyMemory<byte>> arguments, TaskCompletionSource<Reply>? completion)
    {
        NetOutgoingCallContext context = connection.MethodCallerChannels[(entityId, member.Id)];
        NetPendingCall call = new(operation, entityId, member.Id, null, arguments, completion);
        NetPendingCall? toSend = context.Channel.Enqueue(call);

        if (toSend is not null)
        {
            TransmitCall(connection, entityId, context, toSend);
        }
    }

    private static IPEndPoint ResolveEndPoint(string host, int port)
    {
        if (IPAddress.TryParse(host, out IPAddress? parsed))
        {
            return parsed.AddressFamily == AddressFamily.InterNetwork
                ? new IPEndPoint(parsed, port)
                : throw new ArgumentException($"'{host}' is not an IPv4 address.", nameof(host));
        }

        IPAddress? resolved = Array.Find(Dns.GetHostAddresses(host), candidate => candidate.AddressFamily == AddressFamily.InterNetwork);
        return resolved is not null
            ? new IPEndPoint(resolved, port)
            : throw new ArgumentException($"'{host}' resolves to no IPv4 address.", nameof(host));
    }

    private static byte[] Concat(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        byte[] result = new byte[a.Length + b.Length];
        a.CopyTo(result);
        b.CopyTo(result.AsSpan(a.Length));
        return result;
    }

    private static bool IsDefaultValue(Type type, object? value)
    {
        if (value is null)
        {
            return true;
        }

        if (!type.IsValueType)
        {
            return false;
        }

        Type underlying = Nullable.GetUnderlyingType(type) ?? type;
        return value.Equals(Activator.CreateInstance(underlying));
    }

    private async Task ReceiveLoop(Socket targetSocket, CancellationToken cancellation)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65527);
        EndPoint anyEndPoint = new IPEndPoint(IPAddress.Any, 0);

        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                SocketReceiveFromResult result;

                try
                {
                    result = await targetSocket.ReceiveFromAsync(buffer, SocketFlags.None, anyEndPoint, cancellation).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException exception) when (exception.SocketErrorCode is SocketError.OperationAborted or SocketError.Interrupted or SocketError.NotSocket)
                {
                    break;
                }
                catch (SocketException)
                {
                    continue;
                }

                try
                {
                    Packet? packet = datagramCodec.Decode(buffer.AsSpan(0, result.ReceivedBytes));

                    if (packet is not null)
                    {
                        HandlePacket(packet, (IPEndPoint)result.RemoteEndPoint);
                    }
                }
                catch (Exception)
                {
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task SweepLoop(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(SweepInterval, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            foreach (NetConnection connection in allConnections.Keys)
            {
                try
                {
                    // One connection's exception here must not stop the sweep from ever reaching the others,
                    // since this loop is the only source of pings, disconnect timeouts, and resends.
                    SweepConnection(connection);
                }
                catch (Exception)
                {
                }
            }
        }
    }

    /// <summary>
    /// Runs one periodic sweep tick for a connection: handshake retry while connecting, or, once connected,
    /// the disconnect timeout check, liveness ping, and every due property/entity-lifecycle/method-call resend.
    /// </summary>
    /// <param name="connection">The connection to sweep.</param>
    internal void SweepConnection(NetConnection connection)
    {
        if (connection.Status == NetStatus.Connecting)
        {
            SweepHandshakeRetry(connection);
            return;
        }

        if (connection.Status != NetStatus.Connected)
        {
            return;
        }

        if (DateTime.UtcNow - connection.LastReceivedAt >= DisconnectTimeout)
        {
            DropConnection(connection, new NetFailedException("Connection timed out."));
            return;
        }

        if (!connection.HasOutstandingPing && DateTime.UtcNow - connection.LastSentAt >= PingInterval)
        {
            SendPing(connection);
        }

        foreach (KeyValuePair<(ulong EntityId, uint PropertyId), NetOutgoingPropertyContext> entry in connection.OwnedPropertyChannels.Concat(connection.ViewedPropertyChannels))
        {
            lock (entry.Value.Gate)
            {
                ReadOnlyMemory<byte>? due = entry.Value.Channel.GetValueDueForResend(PingInterval);

                if (due is { } value)
                {
                    TransmitPropertySet(connection, entry.Key.EntityId, entry.Value, value);
                }
            }
        }

        foreach (KeyValuePair<ulong, NetEntityLifecycle> entry in connection.OutgoingLifecycles)
        {
            if (!entry.Value.IsDueForResend(PingInterval))
            {
                continue;
            }

            if (entry.Value.State == NetLifecycleState.CreatePending &&
                connection.OutgoingEntitiesById.TryGetValue(entry.Key, out INetEntity? entity) &&
                entityTypes.TryGetValue(entity, out (NetModelType ModelType, NetControllerType ControllerType) types))
            {
                TransmitEntityCreate(connection, entity, entry.Key, types.ModelType, types.ControllerType, entry.Value);
            }
            else if (entry.Value.State == NetLifecycleState.DestroyPending)
            {
                TransmitEntityDestroy(connection, entry.Key, entry.Value);
            }
        }

        foreach (KeyValuePair<(ulong EntityId, uint MethodId), NetOutgoingCallContext> entry in connection.MethodCallerChannels)
        {
            NetPendingCall? due = entry.Value.Channel.GetDueForResend(PingInterval);

            if (due is not null)
            {
                TransmitCall(connection, entry.Key.EntityId, entry.Value, due);
            }
        }
    }

    private void SweepHandshakeRetry(NetConnection connection)
    {
        if (connection.HandshakeStage is null || DateTime.UtcNow - connection.LastSentAt < HandshakeRetryInterval)
        {
            return;
        }

        switch (connection.HandshakeStage)
        {
            case NetHandshakeStage.AwaitingChallenge:
                SendHandshakeInit(connection);
                break;
            case NetHandshakeStage.AwaitingAccept:
                SendHandshakeResponse(connection);
                break;
        }
    }

    private void Send(NetConnection connection, Packet packet, bool trackReply, ulong? entityIdForReply, Action<Reply>? onReply, out ulong sequence, ulong? supersedes = null)
    {
        // A single-outstanding channel's resend replaces its previous attempt, whose reply will now never come
        // if that attempt was the one lost. Dropping the wait for it keeps the pending table from growing by
        // one entry per lost packet for the life of the connection.
        if (supersedes is { } superseded)
        {
            connection.PendingReplies.TryRemove(superseded, out _);
        }

        packet.ConnectionId = connection.ConnectionId ?? 0;
        sequence = connection.AllocateSequence();
        packet.Sequence = sequence;

        byte[] buffer = datagramCodec.Encode(packet, connection.SessionKey.IsEmpty ? null : connection.SessionKey, out int length);

        // Registered before the datagram goes out, never after: over a fast link the answer can already be
        // back and handled by the time a later registration would have run, and a reply nobody is waiting on
        // is simply dropped — losing the acknowledgment for good.
        if (trackReply && onReply is not null)
        {
            connection.PendingReplies[sequence] = (entityIdForReply, onReply);
        }

        try
        {
            Transmit(buffer, length, connection.RemoteEndPoint!);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        connection.LastSentAt = DateTime.UtcNow;
    }

    private void SendHandshake(Packet packet, IPEndPoint remoteEndPoint)
    {
        byte[] buffer = datagramCodec.Encode(packet, null, out int length);

        try
        {
            Transmit(buffer, length, remoteEndPoint);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void Transmit(byte[] buffer, int length, IPEndPoint remoteEndPoint)
    {
        try
        {
            GetSocket().SendTo(buffer, 0, length, SocketFlags.None, remoteEndPoint);
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
            // A datagram that can't go out is simply a lost one, which every channel above this already
            // tolerates and retries. Letting it throw instead would surface a transient send error — or a
            // socket closed mid-teardown — from wherever the send happened to originate, which for a property
            // broadcast is the caller's own property setter, and would abandon the rest of the audience.
        }
    }

    private void SendHandshakeReject(IPEndPoint remoteEndPoint, string reason) =>
        SendHandshake(new Packet { HandshakeReject = new HandshakeReject { Reason = reason } }, remoteEndPoint);

    private void SendHandshakeInit(NetConnection connection)
    {
        Packet packet = new()
        {
            HandshakeInit = new HandshakeInit
            {
                ProtocolVersion = INetManager.ProtocolVersion,
                ClientPublicKey = ByteString.CopyFrom(connection.AgreementKey!.PublicKey.Span),
            },
        };

        connection.HandshakeStage = NetHandshakeStage.AwaitingChallenge;
        connection.LastSentAt = DateTime.UtcNow;
        SendHandshake(packet, connection.RemoteEndPoint!);
    }

    private void SendHandshakeResponse(NetConnection connection)
    {
        Packet packet = new()
        {
            HandshakeResponse = new HandshakeResponse
            {
                ClientPublicKey = ByteString.CopyFrom(connection.AgreementKey!.PublicKey.Span),
                ServerPublicKey = ByteString.CopyFrom(connection.ServerPublicKey!.Value.Span),
                Cookie = ByteString.CopyFrom(connection.PendingCookie!),
            },
        };

        connection.LastSentAt = DateTime.UtcNow;
        SendHandshake(packet, connection.RemoteEndPoint!);
    }

    private void SendPing(NetConnection connection)
    {
        // Both marked before the send, since the reply can be handled before Send even returns — recording
        // the ping as outstanding afterward would overwrite the clearing its own reply just did, and no
        // further ping would ever be sent on this connection.
        connection.PendingPingSentAt = DateTime.UtcNow;
        connection.HasOutstandingPing = true;

        Send(connection, new Packet { Ping = new Ping() }, trackReply: true, null, _ =>
        {
            connection.SetLatency(DateTime.UtcNow - connection.PendingPingSentAt);
            connection.HasOutstandingPing = false;
        }, out _);
    }

    private NetAgreementKey DeriveServerAgreementKey(ReadOnlySpan<byte> clientPublicKey, uint issuedAt)
    {
        byte[] context = BuildEphemeralContext(clientPublicKey, issuedAt);
        byte[] seed = crypto.ComputeHmacSha256(serverSecret, context);
        return crypto.CreateAgreementKey(seed);
    }

    private static byte[] BuildEphemeralContext(ReadOnlySpan<byte> clientPublicKey, uint issuedAt)
    {
        byte[] buffer = new byte[EphemeralContextPrefix.Length + clientPublicKey.Length + 4];
        EphemeralContextPrefix.CopyTo(buffer, 0);
        clientPublicKey.CopyTo(buffer.AsSpan(EphemeralContextPrefix.Length));
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(buffer.Length - 4), issuedAt);
        return buffer;
    }

    private byte[] ComputeCookie(IPEndPoint remoteEndPoint, ReadOnlySpan<byte> clientPublicKey, ReadOnlySpan<byte> serverPublicKey, uint issuedAt)
    {
        byte[] addressBytes = remoteEndPoint.Address.GetAddressBytes();
        byte[] input = new byte[addressBytes.Length + 2 + clientPublicKey.Length + serverPublicKey.Length + 4];
        int offset = 0;
        addressBytes.CopyTo(input, offset);
        offset += addressBytes.Length;
        BinaryPrimitives.WriteUInt16BigEndian(input.AsSpan(offset), (ushort)remoteEndPoint.Port);
        offset += 2;
        clientPublicKey.CopyTo(input.AsSpan(offset));
        offset += clientPublicKey.Length;
        serverPublicKey.CopyTo(input.AsSpan(offset));
        offset += serverPublicKey.Length;
        BinaryPrimitives.WriteUInt32BigEndian(input.AsSpan(offset), issuedAt);

        byte[] mac = crypto.ComputeHmacSha256(serverSecret, input);
        byte[] cookie = new byte[20];
        mac.AsSpan(0, 16).CopyTo(cookie);
        BinaryPrimitives.WriteUInt32BigEndian(cookie.AsSpan(16), issuedAt);
        return cookie;
    }

    private void HandlePacket(Packet packet, IPEndPoint remoteEndPoint)
    {
        switch (packet.PayloadCase)
        {
            case Packet.PayloadOneofCase.HandshakeInit:
                HandleHandshakeInit(packet.HandshakeInit, remoteEndPoint);
                return;
            case Packet.PayloadOneofCase.HandshakeChallenge:
                HandleHandshakeChallenge(packet.HandshakeChallenge, remoteEndPoint);
                return;
            case Packet.PayloadOneofCase.HandshakeResponse:
                HandleHandshakeResponse(packet.HandshakeResponse, remoteEndPoint);
                return;
            case Packet.PayloadOneofCase.HandshakeReject:
                HandleHandshakeReject(packet, remoteEndPoint);
                return;
            case Packet.PayloadOneofCase.HandshakeAccept:
                HandleHandshakeAccept(packet, remoteEndPoint);
                return;
        }

        if (!connectionsById.TryGetValue(packet.ConnectionId, out NetConnection? connection))
        {
            return;
        }

        if (!datagramCodec.VerifyTag(packet, connection.SessionKey.Span) || !connection.IncomingReplay.ShouldAccept(packet.Sequence))
        {
            return;
        }

        connection.RemoteEndPoint = remoteEndPoint;
        connection.LastReceivedAt = DateTime.UtcNow;

        switch (packet.PayloadCase)
        {
            case Packet.PayloadOneofCase.Disconnect:
                DropConnection(connection, null);
                break;
            case Packet.PayloadOneofCase.Ping:
                Send(connection, new Packet { Reply = new Reply { InResponseTo = packet.Sequence } }, trackReply: false, null, null, out _);
                break;
            case Packet.PayloadOneofCase.EntityCreate:
                HandleEntityCreate(connection, packet);
                break;
            case Packet.PayloadOneofCase.EntityDestroy:
                HandleEntityDestroy(connection, packet);
                break;
            case Packet.PayloadOneofCase.EntitySet:
                HandleEntitySet(connection, packet);
                break;
            case Packet.PayloadOneofCase.EntityCall:
                HandleEntityCall(connection, packet);
                break;
            case Packet.PayloadOneofCase.Reply:
                HandleReply(connection, packet);
                break;
        }
    }

    private void HandleHandshakeInit(HandshakeInit init, IPEndPoint remoteEndPoint)
    {
        if (HostPort is null)
        {
            return;
        }

        if (init.ProtocolVersion != INetManager.ProtocolVersion)
        {
            SendHandshakeReject(remoteEndPoint, "Unsupported protocol version.");
            return;
        }

        uint issuedAt = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using NetAgreementKey serverKey = DeriveServerAgreementKey(init.ClientPublicKey.Span, issuedAt);
        byte[] cookie = ComputeCookie(remoteEndPoint, init.ClientPublicKey.Span, serverKey.PublicKey.Span, issuedAt);

        HandshakeChallenge challenge = new()
        {
            ClientPublicKey = init.ClientPublicKey,
            ServerPublicKey = ByteString.CopyFrom(serverKey.PublicKey.Span),
            Cookie = ByteString.CopyFrom(cookie),
        };

        if (GetIdentityKey() is { } identityKey)
        {
            challenge.ServerSignature = ByteString.CopyFrom(crypto.Sign(identityKey, serverKey.PublicKey.Span));
        }

        SendHandshake(new Packet { HandshakeChallenge = challenge }, remoteEndPoint);
    }

    private void HandleHandshakeChallenge(HandshakeChallenge challenge, IPEndPoint remoteEndPoint)
    {
        if (!pendingOutgoingByEndpoint.TryGetValue(remoteEndPoint, out NetConnection? connection) || connection.HandshakeStage != NetHandshakeStage.AwaitingChallenge)
        {
            return;
        }

        if (connection.ExpectedIdentityKey is { } expectedKey &&
            (challenge.ServerSignature.IsEmpty || !crypto.Verify(expectedKey.Span, challenge.ServerPublicKey.Span, challenge.ServerSignature.Span)))
        {
            DropConnection(connection, new NetFailedException("Server identity verification failed."));
            return;
        }

        byte[] salt = Concat(connection.AgreementKey!.PublicKey.Span, challenge.ServerPublicKey.Span);

        try
        {
            connection.SessionKey = connection.AgreementKey.DeriveKey(challenge.ServerPublicKey.Memory, salt, SessionKeyInfo, 32);
            connection.PayloadKey = connection.AgreementKey.DeriveKey(challenge.ServerPublicKey.Memory, salt, PayloadKeyInfo, 32);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException or ArgumentException)
        {
            // Anything unusable as a public key here can only have come from a peer that isn't speaking this
            // protocol properly. Left to throw, it would be swallowed by the receive loop and the attempt would
            // retry forever instead of ever resolving.
            DropConnection(connection, new NetFailedException("Server key exchange failed."));
            return;
        }

        connection.ServerPublicKey = challenge.ServerPublicKey.Memory;
        connection.PendingCookie = challenge.Cookie.ToByteArray();
        connection.HandshakeStage = NetHandshakeStage.AwaitingAccept;
        SendHandshakeResponse(connection);
    }

    private void HandleHandshakeResponse(HandshakeResponse response, IPEndPoint remoteEndPoint)
    {
        if (HostPort is null || response.Cookie.Length != 20)
        {
            return;
        }

        uint issuedAt = BinaryPrimitives.ReadUInt32BigEndian(response.Cookie.Span[16..]);
        byte[] expected = ComputeCookie(remoteEndPoint, response.ClientPublicKey.Span, response.ServerPublicKey.Span, issuedAt);

        if (!CryptographicOperations.FixedTimeEquals(expected, response.Cookie.Span) ||
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() - issuedAt > 30)
        {
            SendHandshakeReject(remoteEndPoint, "Invalid or expired cookie.");
            return;
        }

        // The server keeps no per-attempt handshake state, so a retransmitted HandshakeResponse (sent whenever
        // its HandshakeAccept was lost) is indistinguishable from a fresh one except by its cookie, which is
        // unique to this attempt and echoed unchanged across retransmissions. Without this it would accept the
        // same client twice, leaving a duplicate connection nothing will ever talk on.
        string cookieKey = Convert.ToHexString(response.Cookie.Span);

        if (incomingConnectionsByCookie.TryGetValue(cookieKey, out NetConnection? established))
        {
            established.RemoteEndPoint = remoteEndPoint;
            Send(established, new Packet { HandshakeAccept = new HandshakeAccept { ConnectionId = established.ConnectionId!.Value } }, trackReply: false, null, null, out _);
            return;
        }

        using NetAgreementKey serverKey = DeriveServerAgreementKey(response.ClientPublicKey.Span, issuedAt);
        byte[] salt = Concat(response.ClientPublicKey.Span, response.ServerPublicKey.Span);
        ReadOnlyMemory<byte> sessionKey = serverKey.DeriveKey(response.ClientPublicKey.Memory, salt, SessionKeyInfo, 32);
        ReadOnlyMemory<byte> payloadKey = serverKey.DeriveKey(response.ClientPublicKey.Memory, salt, PayloadKeyInfo, 32);

        ulong connectionId = Interlocked.Increment(ref nextConnectionId) - 1;

        NetConnection connection = new(this, NetDirection.Incoming, remoteEndPoint.Address.ToString(), remoteEndPoint.Port, null)
        {
            RemoteEndPoint = remoteEndPoint,
            ConnectionId = connectionId,
            SessionKey = sessionKey,
            PayloadKey = payloadKey,
            HandshakeCookie = cookieKey,
        };

        if (incomingConnectionsByCookie.TryAdd(cookieKey, connection) is false)
        {
            return;
        }

        connection.SetStatus(NetStatus.Connected);
        allConnections[connection] = 0;
        connectionsById[connectionId] = connection;

        Send(connection, new Packet { HandshakeAccept = new HandshakeAccept { ConnectionId = connectionId } }, trackReply: false, null, null, out _);
        statusChangedSubject.OnNext(new NetStatusChange { Connection = connection, Status = NetStatus.Connected });
    }

    private void HandleHandshakeAccept(Packet packet, IPEndPoint remoteEndPoint)
    {
        if (!pendingOutgoingByEndpoint.TryGetValue(remoteEndPoint, out NetConnection? connection) || connection.HandshakeStage != NetHandshakeStage.AwaitingAccept)
        {
            return;
        }

        if (!datagramCodec.VerifyTag(packet, connection.SessionKey.Span) || !connection.IncomingReplay.ShouldAccept(packet.Sequence))
        {
            return;
        }

        connection.ConnectionId = packet.HandshakeAccept.ConnectionId;
        connection.HandshakeStage = null;
        connection.LastReceivedAt = DateTime.UtcNow;
        connection.SetStatus(NetStatus.Connected);

        pendingOutgoingByEndpoint.TryRemove(remoteEndPoint, out _);
        connectionsById[connection.ConnectionId.Value] = connection;
        connection.AgreementKey?.Dispose();
        connection.AgreementKey = null;

        statusChangedSubject.OnNext(new NetStatusChange { Connection = connection, Status = NetStatus.Connected });
    }

    private void HandleHandshakeReject(Packet packet, IPEndPoint remoteEndPoint)
    {
        if (!pendingOutgoingByEndpoint.TryGetValue(remoteEndPoint, out NetConnection? connection))
        {
            return;
        }

        DropConnection(connection, new NetFailedException(packet.HandshakeReject.Reason));
    }

    private void HandleReply(NetConnection connection, Packet packet)
    {
        if (connection.PendingReplies.TryRemove(packet.Reply.InResponseTo, out (ulong? EntityId, Action<Reply> Callback) entry))
        {
            entry.Callback(packet.Reply);
        }
    }

    private void HandleEntityCreate(NetConnection connection, Packet packet)
    {
        EntityCreate create = packet.EntityCreate;

        if (connection.RemoteEntities.ContainsKey(create.EntityId))
        {
            Send(connection, new Packet { Reply = new Reply { InResponseTo = packet.Sequence } }, trackReply: false, null, null, out _);
            return;
        }

        if (!string.IsNullOrEmpty(create.NewModel))
        {
            connection.IncomingModelNames[create.ModelId] = create.NewModel;
        }

        if (!string.IsNullOrEmpty(create.NewController))
        {
            connection.IncomingControllerNames[create.ControllerId] = create.NewController;
        }

        NetRemoteEntityBinding? binding = null;

        if (connection.IncomingModelNames.TryGetValue(create.ModelId, out string? modelName) &&
            connection.IncomingControllerNames.TryGetValue(create.ControllerId, out string? controllerName) &&
            listenerBindings.TryGetValue((modelName, controllerName), out INetListenerBinding? listener))
        {
            binding = listener.CreateEntity(this, connection, create.EntityId, create.ModelId, create.ControllerId);

            foreach (EntityProperty property in create.Properties)
            {
                LearnPropertyName(connection.IncomingPropertyNames, create.ModelId, property.PropertyId, property.NewProperty);
                binding.ApplyProperty(ResolvePropertyId(connection.IncomingPropertyNames, create.ModelId, property.PropertyId, binding.ModelType), property.Value.Memory);
            }
        }

        connection.RemoteEntities[create.EntityId] = binding;
        Send(connection, new Packet { Reply = new Reply { InResponseTo = packet.Sequence } }, trackReply: false, null, null, out _);
    }

    private void HandleEntityDestroy(NetConnection connection, Packet packet)
    {
        ulong entityId = packet.EntityDestroy.EntityId;

        if (!connection.RemoteEntities.TryRemove(entityId, out NetRemoteEntityBinding? binding))
        {
            return;
        }

        binding?.NotifyDestroyed();

        foreach ((ulong EntityId, uint PropertyId) key in connection.ViewedPropertySequences.Keys.Where(key => key.EntityId == entityId))
        {
            connection.ViewedPropertySequences.TryRemove(key, out _);
            connection.ViewedPropertyChannels.TryRemove(key, out _);
        }

        foreach ((ulong EntityId, uint PropertyId) key in connection.ViewedPropertyChannels.Keys.Where(key => key.EntityId == entityId))
        {
            connection.ViewedPropertyChannels.TryRemove(key, out _);
        }

        foreach ((ulong EntityId, uint MethodId) key in connection.MethodCallerChannels.Keys.Where(key => key.EntityId == entityId))
        {
            connection.MethodCallerChannels.TryRemove(key, out _);
        }

        foreach (KeyValuePair<ulong, (ulong? EntityId, Action<Reply> Callback)> pending in connection.PendingReplies)
        {
            if (pending.Value.EntityId == entityId && connection.PendingReplies.TryRemove(pending.Key, out (ulong? EntityId, Action<Reply> Callback) removed))
            {
                removed.Callback(new Reply { Failure = "Entity is no longer visible." });
            }
        }

        Send(connection, new Packet { Reply = new Reply { InResponseTo = packet.Sequence } }, trackReply: false, null, null, out _);
    }

    private void HandleEntitySet(NetConnection connection, Packet packet)
    {
        EntitySet set = packet.EntitySet;

        // Which table entity_id belongs in follows from who sent it, not from which table happens to contain
        // it: both sides number the entities they own from zero, so on a connection carrying entities in both
        // directions the same id is live in both tables at once.
        if (set.IsRequest)
        {
            if (connection.OutgoingEntitiesById.TryGetValue(set.EntityId, out INetEntity? entity))
            {
                HandleEntitySetRequest(connection, entity, set, packet.Sequence);
            }

            return;
        }

        if (connection.RemoteEntities.TryGetValue(set.EntityId, out NetRemoteEntityBinding? binding) && binding is not null)
        {
            LearnPropertyName(connection.IncomingPropertyNames, binding.ModelId, set.PropertyId, set.NewProperty);

            if (IsNewestForProperty(connection.ViewedPropertySequences, set.EntityId, set.PropertyId, packet.Sequence))
            {
                binding.ApplyProperty(ResolvePropertyId(connection.IncomingPropertyNames, binding.ModelId, set.PropertyId, binding.ModelType), set.Value.Memory);
            }

            Send(connection, new Packet { Reply = new Reply { InResponseTo = packet.Sequence } }, trackReply: false, null, null, out _);
        }
    }

    private static ConcurrentDictionary<(uint ModelId, uint PropertyId), bool> SentNamesFor(NetConnection connection, bool isViewerRequest) =>
        isViewerRequest ? connection.SentViewedPropertyNames : connection.SentOwnedPropertyNames;

    private static void LearnPropertyName(ConcurrentDictionary<(uint ModelId, uint PropertyId), string> names, uint modelId, uint propertyId, string newProperty)
    {
        if (!string.IsNullOrEmpty(newProperty))
        {
            names[(modelId, propertyId)] = newProperty;
        }
    }

    private static uint ResolvePropertyId(ConcurrentDictionary<(uint ModelId, uint PropertyId), string> names, uint modelId, uint propertyId, NetModelType modelType) =>
        ResolveProperty(names, modelId, propertyId, modelType)?.Id ?? uint.MaxValue;

    /// <summary>
    /// Resolves the property an incoming message's <c>property_id</c> refers to, against this side's own model
    /// interface (Docs/Wire.md#entities).
    /// </summary>
    /// <param name="names">The name table for the direction the message arrived in.</param>
    /// <param name="modelId">The model interface type id the property id is scoped to.</param>
    /// <param name="propertyId">The property id the sender used.</param>
    /// <param name="modelType">This side's validated shape of the model interface.</param>
    /// <returns>The property, or <see langword="null"/> if this side's model declares no such property.</returns>
    internal static NetModelMember? ResolveProperty(ConcurrentDictionary<(uint ModelId, uint PropertyId), string> names, uint modelId, uint propertyId, NetModelType modelType)
    {
        // The sender's name for the id is what identifies the property, not the id's numeric value, so two
        // peers agree even if reflection hands them their interface's properties in different orders — or if
        // one of them was built against a version of the interface that declares different ones.
        if (names.TryGetValue((modelId, propertyId), out string? name))
        {
            return modelType.Properties.FirstOrDefault(property => property.Name == name);
        }

        return propertyId < modelType.Properties.Count ? modelType.Properties[(int)propertyId] : null;
    }

    /// <summary>
    /// Resolves the method an incoming <c>EntityCall</c>'s <c>method_id</c> refers to, against this side's own
    /// controller interface (Docs/Wire.md#entities).
    /// </summary>
    /// <param name="connection">The connection the call arrived on.</param>
    /// <param name="controllerId">The controller interface type id the method id is scoped to.</param>
    /// <param name="methodId">The method id the sender used.</param>
    /// <param name="controllerType">This side's validated shape of the controller interface.</param>
    /// <returns>The method, or <see langword="null"/> if this side's controller declares no such method.</returns>
    internal static NetControllerMember? ResolveMethod(NetConnection connection, uint controllerId, uint methodId, NetControllerType controllerType)
    {
        if (connection.IncomingMethodNames.TryGetValue((controllerId, methodId), out string? name))
        {
            return controllerType.Methods.FirstOrDefault(method => method.Name == name);
        }

        return methodId < controllerType.Methods.Count ? controllerType.Methods[(int)methodId] : null;
    }

    private static bool IsNewestForProperty(ConcurrentDictionary<(ulong EntityId, uint PropertyId), ulong> sequences, ulong entityId, uint propertyId, ulong sequence)
    {
        bool isNewest = true;

        sequences.AddOrUpdate(
            (entityId, propertyId),
            sequence,
            (_, highestSeen) =>
            {
                if (sequence <= highestSeen)
                {
                    isNewest = false;
                    return highestSeen;
                }

                return sequence;
            });

        return isNewest;
    }

    private void HandleEntitySetRequest(NetConnection connection, INetEntity entity, EntitySet set, ulong requestSequence)
    {
        NetModelType modelType = entityTypes[entity].ModelType;
        uint modelId = connection.OutgoingModelIds.TryGetValue(modelType.Type, out uint assignedModelId) ? assignedModelId : 0;
        LearnPropertyName(connection.RequestedPropertyNames, modelId, set.PropertyId, set.NewProperty);

        if (ResolveProperty(connection.RequestedPropertyNames, modelId, set.PropertyId, modelType) is not { } member)
        {
            Send(connection, new Packet { Reply = new Reply { InResponseTo = requestSequence, Failure = "Unknown property." } }, trackReply: false, null, null, out _);
            return;
        }

        if (!member.HasAccess || (member.Role is not null && !connection.Roles.Contains(member.Role)))
        {
            Send(connection, new Packet { Reply = new Reply { InResponseTo = requestSequence, Failure = "Access denied." } }, trackReply: false, null, null, out _);
            return;
        }

        if (IsNewestForProperty(connection.OwnedPropertySequences, set.EntityId, set.PropertyId, requestSequence))
        {
            entity.State.Set(member.Name, ValueCodec.Decode(member.Property.PropertyType, set.Value.Span));
        }

        Send(connection, new Packet { Reply = new Reply { InResponseTo = requestSequence } }, trackReply: false, null, null, out _);
    }

    private void HandleEntityCall(NetConnection connection, Packet packet)
    {
        EntityCall call = packet.EntityCall;

        if (!connection.OutgoingEntitiesById.TryGetValue(call.EntityId, out INetEntity? entity))
        {
            return;
        }

        NetControllerType controllerType = entityTypes[entity].ControllerType;
        uint controllerId = connection.OutgoingControllerIds.TryGetValue(controllerType.Type, out uint assignedControllerId) ? assignedControllerId : 0;

        if (!string.IsNullOrEmpty(call.NewMethod))
        {
            connection.IncomingMethodNames[(controllerId, call.MethodId)] = call.NewMethod;
        }

        if (ResolveMethod(connection, controllerId, call.MethodId, controllerType) is not { } member)
        {
            return;
        }
        NetMethodReceiverChannel receiver = connection.MethodReceiverChannels.GetOrAdd((call.EntityId, call.MethodId), _ => new NetMethodReceiverChannel());
        NetMethodChannelDecision decision = receiver.Decide(call.Operation);

        switch (decision)
        {
            case NetMethodChannelDecision.Ignore:
                return;
            case NetMethodChannelDecision.ResendOutcome:
                Reply? cached = receiver.GetFinishedOutcome(call.Operation);

                if (cached is not null)
                {
                    Reply resend = cached.Clone();
                    resend.InResponseTo = packet.Sequence;
                    Send(connection, new Packet { Reply = resend }, trackReply: false, null, null, out _);
                }

                return;
            default:
                ExecuteCall(connection, entity, member, call, receiver, packet.Sequence);
                return;
        }
    }

    private void ExecuteCall(NetConnection connection, INetEntity entity, NetControllerMember member, EntityCall call, NetMethodReceiverChannel receiver, ulong requestSequence)
    {
        if (!member.HasAccess || (member.Role is not null && !connection.Roles.Contains(member.Role)))
        {
            Reply failure = new() { InResponseTo = requestSequence, Failure = "Access denied." };
            receiver.Finish(call.Operation, failure);
            Send(connection, new Packet { Reply = failure }, trackReply: false, null, null, out _);
            return;
        }

        ParameterInfo[] parameters = member.Method.GetParameters();
        object?[] arguments = new object?[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            ReadOnlySpan<byte> raw = call.Arguments[i].Span;

            if (member.IsSecure)
            {
                byte[]? decrypted = PayloadCodec.Decrypt(connection.PayloadKey.Span, connection.ConnectionId!.Value, call.EntityId, member.Id, call.Operation, i, raw);

                if (decrypted is null)
                {
                    Reply failure = new() { InResponseTo = requestSequence, Failure = "Decryption failed." };
                    receiver.Finish(call.Operation, failure);
                    Send(connection, new Packet { Reply = failure }, trackReply: false, null, null, out _);
                    return;
                }

                arguments[i] = ValueCodec.Decode(parameters[i].ParameterType, decrypted);
            }
            else
            {
                arguments[i] = ValueCodec.Decode(parameters[i].ParameterType, raw);
            }
        }

        RunCall(connection, entity, member, call, receiver, requestSequence, arguments);
    }

    private async void RunCall(NetConnection connection, INetEntity entity, NetControllerMember member, EntityCall call, NetMethodReceiverChannel receiver, ulong requestSequence, object?[] arguments)
    {
        Reply reply;

        try
        {
            object? invokeResult = member.Method.Invoke(entity, arguments);

            if (member.IsTask)
            {
                Task task = (Task)invokeResult!;
                await task.ConfigureAwait(false);

                if (member.ResultType is not null)
                {
                    object? result = task.GetType().GetProperty("Result")!.GetValue(task);
                    ReadOnlyMemory<byte> encoded = ValueCodec.Encode(member.ResultType, result);
                    byte[] payload = member.IsSecure
                        ? PayloadCodec.Encrypt(connection.PayloadKey.Span, connection.ConnectionId!.Value, call.EntityId, member.Id, call.Operation, null, encoded.Span)
                        : encoded.ToArray();
                    reply = new Reply { InResponseTo = requestSequence, Result = ByteString.CopyFrom(payload) };
                }
                else
                {
                    reply = new Reply { InResponseTo = requestSequence, Result = ByteString.Empty };
                }
            }
            else
            {
                reply = new Reply { InResponseTo = requestSequence };
            }
        }
        catch (Exception)
        {
            reply = new Reply { InResponseTo = requestSequence, Failure = "The call failed." };
        }

        try
        {
            receiver.Finish(call.Operation, reply);
            Send(connection, new Packet { Reply = reply }, trackReply: false, null, null, out _);
        }
        catch (Exception)
        {
            // This runs as an async void continuation once the awaited call completes, so it is off the receive
            // loop's own exception handling by then — anything escaping here (a socket disposed mid-teardown,
            // say) would go unobserved and take the process down with it.
        }
    }

    private void HandleCallReply(INetRemoteEntity? remoteEntity, Reply reply, TaskCompletionSource<Reply>? completion)
    {
        if (reply.OutcomeCase == Reply.OutcomeOneofCase.Failure && remoteEntity is { } target)
        {
            failedSubject.OnNext(new NetRemoteEntityFailure { Entity = target, Exception = new NetFailedException(reply.Failure) });
        }

        completion?.TrySetResult(reply);
    }

    private void TransmitCall(NetConnection connection, ulong entityId, NetOutgoingCallContext context, NetPendingCall call)
    {
        EntityCall entityCall = new()
        {
            EntityId = call.EntityId,
            MethodId = call.MethodId,
            NewMethod = connection.SentMethodNames.ContainsKey((context.ControllerId, context.Member.Id)) ? string.Empty : context.Member.Name,
            Operation = call.Operation,
        };

        entityCall.Arguments.AddRange(call.Arguments.Select(argument => ByteString.CopyFrom(argument.Span)));

        Send(connection, new Packet { EntityCall = entityCall }, trackReply: true, call.EntityId, reply =>
        {
            connection.SentMethodNames[(context.ControllerId, context.Member.Id)] = true;
            HandleCallReply(context.RemoteEntity, reply, call.Completion);
            NetPendingCall? next = context.Channel.Advance();

            if (next is not null)
            {
                TransmitCall(connection, entityId, context, next);
            }
        }, out ulong sequence, context.Channel.LastSentSequence);

        context.Channel.RecordSent(sequence);
    }

    private void SendPropertySet(NetConnection connection, ulong entityId, uint modelId, NetModelMember member, Func<ReadOnlyMemory<byte>> encode, bool reportFailureAsRemote, INetRemoteEntity? remoteEntity = null)
    {
        ConcurrentDictionary<(ulong EntityId, uint PropertyId), NetOutgoingPropertyContext> channels =
            reportFailureAsRemote ? connection.ViewedPropertyChannels : connection.OwnedPropertyChannels;

        NetOutgoingPropertyContext context = channels.GetOrAdd(
            (entityId, member.Id),
            _ => new NetOutgoingPropertyContext(new NetPropertyChannel(), modelId, member, reportFailureAsRemote) { RemoteEntity = remoteEntity });

        lock (context.Gate)
        {
            TransmitPropertySet(connection, entityId, context, encode());
        }
    }

    private void SendCurrentPropertyValue(NetConnection connection, ulong entityId, uint modelId, INetEntity entity, NetModelMember member) =>
        SendPropertySet(
            connection,
            entityId,
            modelId,
            member,
            () => ValueCodec.Encode(member.Property.PropertyType, entity.State.Get(member.Name)),
            reportFailureAsRemote: false);

    private void TransmitPropertySet(NetConnection connection, ulong entityId, NetOutgoingPropertyContext context, ReadOnlyMemory<byte> encoded)
    {
        EntitySet entitySet = new()
        {
            EntityId = entityId,
            PropertyId = context.Member.Id,
            NewProperty = SentNamesFor(connection, context.ReportFailureAsRemote).ContainsKey((context.ModelId, context.Member.Id)) ? string.Empty : context.Member.Name,
            Value = ByteString.CopyFrom(encoded.Span),
            IsRequest = context.ReportFailureAsRemote,
        };

        ulong sequence = 0;

        Send(connection, new Packet { EntitySet = entitySet }, trackReply: true, entityId, reply =>
        {
            SentNamesFor(connection, context.ReportFailureAsRemote)[(context.ModelId, context.Member.Id)] = true;
            context.Channel.Acknowledge(sequence);

            if (reply.OutcomeCase == Reply.OutcomeOneofCase.Failure && context.ReportFailureAsRemote && context.RemoteEntity is { } target)
            {
                failedSubject.OnNext(new NetRemoteEntityFailure { Entity = target, Exception = new NetFailedException(reply.Failure) });
            }
        }, out sequence, context.Channel.LastSentSequence);

        context.Channel.RecordSent(sequence, encoded);
    }

    private void OnLocalPropertyChanged(INetEntity entity, NetModelType modelType, NetModelMember member)
    {
        if (!entityGroups.TryGetValue(entity, out List<NetGroupBase>? groups))
        {
            return;
        }

        HashSet<NetConnection> audience;

        lock (groupMembershipGate)
        {
            audience = groups.SelectMany(group => group.GetAudienceSnapshot()).ToHashSet();
        }

        foreach (NetConnection connection in audience)
        {
            if (!connection.OutgoingEntityIds.TryGetValue(entity, out ulong entityId))
            {
                continue;
            }

            if (!connection.OutgoingLifecycles.TryGetValue(entityId, out NetEntityLifecycle? lifecycle) || lifecycle.State != NetLifecycleState.Created)
            {
                continue;
            }

            if (!connection.OutgoingModelIds.TryGetValue(modelType.Type, out uint modelId))
            {
                continue;
            }

            // Reads the property's value inside the channel's gate rather than using the one this notification
            // carried, so that whichever change sends last also sends the freshest value — the notification
            // that triggered this may already be stale by the time it gets the gate.
            SendCurrentPropertyValue(connection, entityId, modelId, entity, member);
        }
    }

    private void ReevaluatePair(INetEntity entity, NetConnection connection)
    {
        if (!entityGroups.TryGetValue(entity, out List<NetGroupBase>? groups) || !entityTypes.TryGetValue(entity, out (NetModelType ModelType, NetControllerType ControllerType) types))
        {
            return;
        }

        List<NetGroupBase> grantingGroups;

        lock (groupMembershipGate)
        {
            grantingGroups = groups.Where(group => group.ContainsViewer(connection)).ToList();
        }

        bool wasVisible = connection.OutgoingEntityIds.ContainsKey(entity);
        bool visible = grantingGroups.Count > 0 && grantingGroups.Any(group => EvaluatePositionVisibility(entity, connection, group, types.ModelType, wasVisible));

        if (visible && !wasVisible)
        {
            SendEntityCreate(connection, entity, types.ModelType, types.ControllerType);
        }
        else if (!visible && wasVisible)
        {
            SendEntityDestroy(connection, entity);
        }
    }

    private static bool EvaluatePositionVisibility(INetEntity entity, NetConnection connection, NetGroupBase group, NetModelType modelType, bool wasVisible)
    {
        if (modelType.Position is null)
        {
            return true;
        }

        if (entity.State.Get(modelType.Position.Name) is not NetPosition entityPosition)
        {
            return true;
        }

        NetPosition? connectionPosition = group.GetEffectivePosition(connection);

        if (connectionPosition is null)
        {
            return true;
        }

        float distance = Vector3.Distance(entityPosition.Position, connectionPosition.Position);
        bool anyRange = false;
        bool withinEnter = true;
        bool exceedsExit = false;

        if (entityPosition.Range is { } entityRange)
        {
            anyRange = true;
            withinEnter &= distance <= entityRange.EnterDistance;
            exceedsExit |= distance > entityRange.ExitDistance;
        }

        if (connectionPosition.Range is { } connectionRange)
        {
            anyRange = true;
            withinEnter &= distance <= connectionRange.EnterDistance;
            exceedsExit |= distance > connectionRange.ExitDistance;
        }

        if (!anyRange)
        {
            return true;
        }

        return wasVisible ? !exceedsExit : withinEnter;
    }

    private void SendEntityCreate(NetConnection connection, INetEntity entity, NetModelType modelType, NetControllerType controllerType)
    {
        (ulong entityId, bool _) = connection.AssignEntityId(entity);
        NetEntityLifecycle lifecycle = connection.OutgoingLifecycles.GetOrAdd(entityId, _ => new NetEntityLifecycle());
        TransmitEntityCreate(connection, entity, entityId, modelType, controllerType, lifecycle);
    }

    private void TransmitEntityCreate(NetConnection connection, INetEntity entity, ulong entityId, NetModelType modelType, NetControllerType controllerType, NetEntityLifecycle lifecycle)
    {
        (uint modelId, bool isNewModel) = connection.AssignModelId(modelType.Type);
        (uint controllerId, bool isNewController) = connection.AssignControllerId(controllerType.Type);

        EntityCreate create = new()
        {
            EntityId = entityId,
            ModelId = modelId,
            NewModel = isNewModel ? modelType.Type.FullName : string.Empty,
            ControllerId = controllerId,
            NewController = isNewController ? controllerType.Type.FullName : string.Empty,
        };

        List<uint> introducedProperties = [];

        foreach (NetModelMember member in modelType.Properties)
        {
            object? value = entity.State.Get(member.Name);

            if (IsDefaultValue(member.Property.PropertyType, value))
            {
                continue;
            }

            create.Properties.Add(new EntityProperty
            {
                PropertyId = member.Id,
                NewProperty = connection.SentOwnedPropertyNames.ContainsKey((modelId, member.Id)) ? string.Empty : member.Name,
                Value = ByteString.CopyFrom(ValueCodec.Encode(member.Property.PropertyType, value).Span),
            });

            introducedProperties.Add(member.Id);
        }

        ulong sequence = 0;

        Send(connection, new Packet { EntityCreate = create }, trackReply: true, entityId, reply =>
        {
            if (!lifecycle.Acknowledge(sequence, out bool shouldSendDestroyNow))
            {
                return;
            }

            if (shouldSendDestroyNow)
            {
                TransmitEntityDestroy(connection, entityId, lifecycle);
                return;
            }

            // Only the names this create actually carried — a property left at its default was skipped
            // entirely, so the connection still hasn't been told what its id means.
            foreach (uint acknowledged in introducedProperties)
            {
                connection.SentOwnedPropertyNames[(modelId, acknowledged)] = true;
            }

            // A property changed while this create was still unacknowledged and, since it was accepted on
            // this attempt, never got the chance to be reflected in a retransmission — send every property's
            // current value now to guarantee the connection ends up consistent regardless of that timing.
            foreach (NetModelMember member in modelType.Properties)
            {
                SendCurrentPropertyValue(connection, entityId, modelId, entity, member);
            }
        }, out sequence, lifecycle.LastSentSequence);

        lifecycle.RecordSent(sequence);
    }

    private void SendEntityDestroy(NetConnection connection, INetEntity entity)
    {
        if (!connection.OutgoingEntityIds.TryGetValue(entity, out ulong entityId) ||
            !connection.OutgoingLifecycles.TryGetValue(entityId, out NetEntityLifecycle? lifecycle))
        {
            return;
        }

        if (lifecycle.RequestDestroy())
        {
            TransmitEntityDestroy(connection, entityId, lifecycle);
        }
    }

    private void TransmitEntityDestroy(NetConnection connection, ulong entityId, NetEntityLifecycle lifecycle)
    {
        ulong sequence = 0;
        Send(connection, new Packet { EntityDestroy = new EntityDestroy { EntityId = entityId } }, trackReply: true, entityId, reply =>
        {
            if (lifecycle.Acknowledge(sequence, out _))
            {
                RetireEntityId(connection, entityId);
            }
        }, out sequence, lifecycle.LastSentSequence);

        lifecycle.RecordSent(sequence);
    }

    /// <summary>
    /// Drops everything a connection was keeping for an entity id once its destroy has been acknowledged. The
    /// id is spent at that point and never refers to anything again (Docs/Wire.md#entities), so holding the
    /// state would both accumulate forever across entity churn and keep the entity looking visible — leaving
    /// it unable to ever be created again, since a fresh id is what a later re-entry would need.
    /// </summary>
    /// <param name="connection">The connection the entity was exposed to.</param>
    /// <param name="entityId">The id being retired.</param>
    private void RetireEntityId(NetConnection connection, ulong entityId)
    {
        connection.OutgoingLifecycles.TryRemove(entityId, out _);

        foreach ((ulong EntityId, uint PropertyId) key in connection.OwnedPropertyChannels.Keys.Where(key => key.EntityId == entityId))
        {
            connection.OwnedPropertyChannels.TryRemove(key, out _);
        }

        foreach ((ulong EntityId, uint MethodId) key in connection.MethodReceiverChannels.Keys.Where(key => key.EntityId == entityId))
        {
            connection.MethodReceiverChannels.TryRemove(key, out _);
        }

        foreach ((ulong EntityId, uint PropertyId) key in connection.OwnedPropertySequences.Keys.Where(key => key.EntityId == entityId))
        {
            connection.OwnedPropertySequences.TryRemove(key, out _);
        }

        foreach (KeyValuePair<ulong, (ulong? EntityId, Action<Reply> Callback)> pending in connection.PendingReplies)
        {
            if (pending.Value.EntityId == entityId)
            {
                connection.PendingReplies.TryRemove(pending.Key, out _);
            }
        }

        if (!connection.OutgoingEntitiesById.TryRemove(entityId, out INetEntity? entity))
        {
            return;
        }

        connection.OutgoingEntityIds.TryRemove(entity, out _);

        // It may have become visible again while the destroy was still in flight, which nothing acted on then
        // because the entity still looked visible. Now that its id is retired, that can be settled.
        ReevaluatePair(entity, connection);
    }
}
