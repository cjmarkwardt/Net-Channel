namespace Markwardt.NetChannel;

/// <summary>
/// A tracked group of local net entities, viewable by an explicit, caller-managed set of connections, created
/// via <see cref="INetManager.CreateView"/>. Every entity in a view is visible to every viewer currently in
/// that view, subject to any <see cref="NetRoleAttribute"/> restriction on its members.
/// </summary>
public interface INetView : INetGroup
{
    /// <summary>
    /// An arbitrary value associated with the view when it was created, or <see langword="null"/> if none was given.
    /// </summary>
    object? Tag { get; }

    /// <summary>
    /// The connections currently in the view as viewers. A connection is automatically removed from this
    /// set, in every view it was a viewer of, as soon as its <see cref="INetConnection.Status"/> drops to
    /// <see cref="NetStatus.Disconnected"/>, whether from a local <see cref="INetConnection.Drop"/>/
    /// <see cref="INetConnection.Disconnect"/> call or a remote failure.
    /// </summary>
    IReadOnlySet<INetConnection> Viewers { get; }

    /// <summary>
    /// Destroys the view.
    /// </summary>
    void Destroy();

    /// <summary>
    /// Adds connections to the view as viewers.
    /// </summary>
    /// <param name="viewers">The connections to add.</param>
    /// <exception cref="ArgumentException">A connection was not created by this library.</exception>
    void AddViewers(params IEnumerable<INetConnection> viewers);

    /// <summary>
    /// Removes connections from the view's viewers.
    /// </summary>
    /// <param name="viewers">The connections to remove.</param>
    /// <exception cref="ArgumentException">A connection was not created by this library.</exception>
    void RemoveViewers(params IEnumerable<INetConnection> viewers);

    /// <summary>
    /// Gets this view's position/range override for a connection, for position-based visibility filtering
    /// scoped to just this view, or <see langword="null"/> if none is set — in which case the connection's
    /// global <see cref="INetConnection.Position"/> applies within this view instead.
    /// </summary>
    /// <param name="connection">The connection to get the override for.</param>
    /// <returns>The override, or <see langword="null"/> if none is set.</returns>
    /// <exception cref="ArgumentException"><paramref name="connection"/> was not created by this library.</exception>
    NetPosition? GetViewerPosition(INetConnection connection);

    /// <summary>
    /// Sets this view's position/range override for a connection. While set, it takes precedence over the
    /// connection's global <see cref="INetConnection.Position"/> for position-based visibility filtering
    /// within this view only, leaving the connection's global position, and its use in every other view,
    /// unaffected.
    /// </summary>
    /// <param name="connection">The connection to set the override for.</param>
    /// <param name="position">The override, or <see langword="null"/> to clear it and fall back to the connection's global position within this view.</param>
    /// <exception cref="ArgumentException"><paramref name="connection"/> was not created by this library.</exception>
    void SetViewerPosition(INetConnection connection, NetPosition? position);
}
