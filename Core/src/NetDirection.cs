namespace Markwardt.NetChannel;

/// <summary>
/// Indicates which side of a connection initiated it.
/// </summary>
public enum NetDirection
{
    /// <summary>
    /// The connection was initiated by the remote peer.
    /// </summary>
    Incoming,

    /// <summary>
    /// The connection was initiated locally.
    /// </summary>
    Outgoing
}
