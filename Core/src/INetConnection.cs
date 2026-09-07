namespace Markwardt.NetChannel;

/// <summary>
/// A connection to a remote peer.
/// </summary>
public interface INetConnection
{
    /// <summary>
    /// The connection's current status.
    /// </summary>
    NetStatus Status { get; }

    /// <summary>
    /// Which side of the connection initiated it.
    /// </summary>
    NetDirection Direction { get; }

    /// <summary>
    /// The host address of the remote peer.
    /// </summary>
    string Host { get; }

    /// <summary>
    /// The port number of the remote peer.
    /// </summary>
    int Port { get; }

    /// <summary>
    /// An arbitrary value associated with the connection when it was made, or <see langword="null"/> if none was given.
    /// </summary>
    object? Tag { get; }

    /// <summary>
    /// The roles currently granted to this connection.
    /// </summary>
    IReadOnlySet<string> Roles { get; }

    /// <summary>
    /// This connection's current round-trip latency to the remote peer, as most recently measured by a
    /// ping/pong heartbeat exchange.
    /// </summary>
    TimeSpan Latency { get; }

    /// <summary>
    /// This connection's global position for position-based visibility filtering, or <see langword="null"/>
    /// if unset. Purely a local device for this manager's own filtering of its own groups' entities: it is
    /// never transmitted to, visible to, or influenced by the remote peer, which has no equivalent of its
    /// own to set — only this side's manager reads or writes it, typically populated from whatever this side
    /// already knows about where this connection's viewer is (e.g. a server tracking a connected player's
    /// position as part of its own local state). A view can override this for just that view via
    /// <see cref="INetView.SetViewerPosition"/>.
    /// </summary>
    NetPosition? Position { get; set; }

    /// <summary>
    /// Immediately ends the connection without waiting for a graceful shutdown.
    /// </summary>
    void Drop();

    /// <summary>
    /// Gracefully ends the connection, awaiting acknowledgement from the remote peer before it completes.
    /// </summary>
    /// <returns>A task that completes once the connection has ended.</returns>
    Task Disconnect();

    /// <summary>
    /// Grants this connection a role, allowing it to set or call model properties/controller methods restricted by a matching <see cref="NetRoleAttribute"/>.
    /// </summary>
    /// <param name="role">The role to grant.</param>
    void Promote(string role);

    /// <summary>
    /// Revokes a previously granted role from this connection.
    /// </summary>
    /// <param name="role">The role to revoke.</param>
    void Demote(string role);
}
