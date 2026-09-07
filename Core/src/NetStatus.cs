namespace Markwardt.NetChannel;

/// <summary>
/// Indicates the current connection status of a target.
/// </summary>
public enum NetStatus
{
    /// <summary>
    /// There is no connection to the target, and no attempt is being made to connect. For a target that was
    /// previously connected, dropping to this status also removes it as a viewer from every
    /// <see cref="INetView"/> it was in.
    /// </summary>
    Disconnected,

    /// <summary>
    /// An attempt is being made to connect to the target.
    /// </summary>
    Connecting,

    /// <summary>
    /// The target is currently connected.
    /// </summary>
    Connected
}
