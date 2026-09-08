namespace Markwardt.NetChannel;

/// <summary>
/// The main entry point for communicating with other peers across the network.
/// </summary>
public interface INetManager : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// The single protocol version every manager runs, sent as <c>HandshakeInit.protocol_version</c> and
    /// checked against an incoming client's own (see Docs/Wire.md#connection-handshake).
    /// </summary>
    static string ProtocolVersion => "1";

    /// <summary>
    /// Triggered when a connection's status has changed.
    /// </summary>
    IObservable<NetStatusChange> StatusChanged { get; }

    /// <summary>
    /// Triggered when a connecting connection fails to connect and is dropped back to <see cref="NetStatus.Disconnected"/>.
    /// </summary>
    IObservable<NetConnectionFailure> Rejected { get; }

    /// <summary>
    /// Triggered when a connected connection loses its remote connection and is dropped back to <see cref="NetStatus.Disconnected"/>.
    /// Does not trigger when the connection is ended by a local <see cref="INetConnection.Drop"/> or <see cref="INetConnection.Disconnect"/> call.
    /// </summary>
    IObservable<NetConnectionFailure> Dropped { get; }

    /// <summary>
    /// Triggered whenever a set or call made through a remote entity's
    /// <see cref="INetRemoteEntity{TModel, TController}.Model"/>/<see cref="INetRemoteEntity{TModel, TController}.Controller"/>
    /// fails for any reason — e.g. an owner-side decline (insufficient role, or a member with no
    /// <see cref="NetAccessAttribute"/> granting access), the entity no longer being visible, or the owner's
    /// own controller method throwing while executing an otherwise-permitted call. A failure that is also
    /// routed back to a caller's awaited <see cref="Task"/> (a failed
    /// <see cref="Task"/>/<see cref="Task{TResult}"/>-returning controller call) triggers this too, in
    /// addition to faulting that task. Getting a model property never fails, since it is always a local,
    /// unconditional cache read.
    /// </summary>
    IObservable<NetRemoteEntityFailure> Failed { get; }

    /// <summary>
    /// The connections that are currently connecting.
    /// </summary>
    IReadOnlySet<INetConnection> PendingConnections { get; }

    /// <summary>
    /// The connections that are currently connected.
    /// </summary>
    IReadOnlySet<INetConnection> ActiveConnections { get; }

    /// <summary>
    /// The views currently created via <see cref="CreateView"/>.
    /// </summary>
    IReadOnlySet<INetView> Views { get; }

    /// <summary>
    /// The entities visible to all active connections. Adding an entity here exposes it to every currently
    /// active connection, and to every connection that becomes active afterward, without needing to manage
    /// viewers explicitly, unlike a view created via <see cref="CreateView"/>.
    /// </summary>
    INetGroup All { get; }

    /// <summary>
    /// The UDP port this manager is currently listening on for incoming connections, or <see langword="null"/>
    /// if <see cref="Host"/> has not been called.
    /// </summary>
    int? HostPort { get; }

    /// <summary>
    /// How long a connection can go without sending any traffic before this manager automatically sends a
    /// ping on it, proving liveness to the remote peer during an otherwise-idle period. Defaults to 1.5
    /// seconds.
    /// </summary>
    TimeSpan PingInterval { get; set; }

    /// <summary>
    /// How long a connection can go without receiving any traffic before this manager assumes the remote
    /// peer is gone and drops it (see <see cref="Dropped"/>). Defaults to 5 seconds.
    /// </summary>
    TimeSpan DisconnectTimeout { get; set; }

    /// <summary>
    /// Starts (or restarts, on a new socket) accepting incoming connections on a local endpoint, allowing this
    /// manager to act as a server in addition to making its own outgoing connections via <see cref="Connect"/>.
    /// </summary>
    /// <param name="port">The local UDP port to listen on, or <c>0</c> to let the OS assign one.</param>
    /// <param name="identity">
    /// This manager's long-term Ed25519 identity private key (a 32-byte seed), used to sign
    /// <c>HandshakeChallenge.server_public_key</c> so a client connecting in Pinned mode can verify it, or
    /// <see langword="null"/> to leave no identity key configured, in which case only Unauthenticated clients
    /// can connect (see Docs/Wire.md#server-authentication).
    /// </param>
    /// <param name="host">The local IPv4 address to bind to, e.g. <c>"0.0.0.0"</c> for every local interface.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="identity"/> is given but is not 32 bytes, or <paramref name="host"/> is not an IPv4
    /// address — Net-Channel is IPv4 only.
    /// </exception>
    void Host(int port = 0, ReadOnlyMemory<byte>? identity = null, string host = "0.0.0.0");

    /// <summary>
    /// Begins attempting to connect to the given host and port.
    /// </summary>
    /// <param name="host">The IPv4 address, or a host name resolving to one, to connect to.</param>
    /// <param name="port">The port number to connect to.</param>
    /// <param name="tag">An arbitrary value to associate with the connection, retrievable later via <see cref="GetConnection"/>.</param>
    /// <param name="expectedIdentity">
    /// The remote peer's expected Ed25519 identity public key, connecting in Pinned mode, or
    /// <see langword="null"/> to connect in Unauthenticated mode (see Docs/Wire.md#server-authentication).
    /// </param>
    /// <returns>The connection, immediately returned in a connecting state.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="expectedIdentity"/> is given but is not 32 bytes, or <paramref name="host"/> is neither
    /// an IPv4 address nor a host name resolving to one — Net-Channel is IPv4 only.
    /// </exception>
    /// <exception cref="SocketException"><paramref name="host"/> is a host name that could not be resolved.</exception>
    INetConnection Connect(string host, int port, object? tag = null, ReadOnlyMemory<byte>? expectedIdentity = null);

    /// <summary>
    /// Gets the pending or active connection with the given tag.
    /// </summary>
    /// <param name="tag">The tag the connection was made with.</param>
    /// <returns>The matching connection, or <see langword="null"/> if none is found.</returns>
    INetConnection? GetConnection(object tag);

    /// <summary>
    /// Creates a new, empty view of connections and entities.
    /// </summary>
    /// <param name="tag">An arbitrary value to associate with the view, retrievable later via <see cref="GetView"/>.</param>
    /// <returns>The created view.</returns>
    INetView CreateView(object? tag = null);

    /// <summary>
    /// Gets the view with the given tag.
    /// </summary>
    /// <param name="tag">The tag the view was created with.</param>
    /// <returns>The matching view, or <see langword="null"/> if none is found.</returns>
    INetView? GetView(object tag);

    /// <summary>
    /// Registers a listener to run whenever a new entity with model interface <typeparamref name="TModel"/>
    /// and controller interface <typeparamref name="TController"/> becomes visible to this manager, e.g.
    /// because a remote peer added it to a view one of this manager's connections is a viewer of, or to
    /// <see cref="INetManager.All"/>. A remote entity becomes visible when an entity creation message for it
    /// is received; the model and controller interface types carried in that message are matched against
    /// <typeparamref name="TModel"/> and <typeparamref name="TController"/> — both must match — and every
    /// listener registered for that exact pair is invoked with a new
    /// <see cref="INetRemoteEntity{TModel, TController}"/> for the entity. If no listener is registered for
    /// the message's exact model/controller interface type pair, the remote entity is ignored entirely.
    /// </summary>
    /// <typeparam name="TModel">The remote entity's model interface to listen for.</typeparam>
    /// <typeparam name="TController">The remote entity's controller interface to listen for.</typeparam>
    /// <param name="listener">The listener to run with a remote entity proxy for each newly visible entity.</param>
    /// <returns>A handle that unsubscribes the listener when disposed.</returns>
    /// <exception cref="NetInvalidInterfaceException">
    /// <typeparamref name="TModel"/>/<typeparamref name="TController"/> is used here for the first time
    /// anywhere and does not have the shape required of a model/controller interface.
    /// </exception>
    IDisposable Listen<TModel, TController>(Action<INetRemoteEntity<TModel, TController>> listener)
        where TModel : class
        where TController : class;

    /// <summary>
    /// Registers the serializer used to encode and decode every model property value, controller method
    /// argument, and method result of type <typeparamref name="T"/>. A type with no serializer registered for
    /// it defaults to automatic Protocol Buffers serialization.
    /// </summary>
    /// <typeparam name="T">The type to register the serializer for.</typeparam>
    /// <param name="serializer">The serializer to use for <typeparamref name="T"/> from now on.</param>
    void SetSerializer<T>(INetSerializer serializer);
}
