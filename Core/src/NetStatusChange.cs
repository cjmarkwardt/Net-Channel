namespace Markwardt.NetChannel;

/// <summary>
/// Describes a change in a connection's status.
/// </summary>
public sealed record NetStatusChange
{
    /// <summary>
    /// The connection whose status has changed.
    /// </summary>
    public required INetConnection Connection { get; init; }

    /// <summary>
    /// The connection's new status.
    /// </summary>
    public required NetStatus Status { get; init; }
}
