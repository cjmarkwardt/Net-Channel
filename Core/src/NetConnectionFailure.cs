namespace Markwardt.NetChannel;

/// <summary>
/// Describes why a connection was dropped back to <see cref="NetStatus.Disconnected"/>.
/// </summary>
public sealed record NetConnectionFailure
{
    /// <summary>
    /// The connection that was dropped.
    /// </summary>
    public required INetConnection Connection { get; init; }

    /// <summary>
    /// The exception indicating why the connection was dropped.
    /// </summary>
    public required Exception Exception { get; init; }
}
