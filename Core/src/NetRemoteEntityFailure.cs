namespace Markwardt.NetChannel;

/// <summary>
/// Describes why a get, set, or call made through a remote entity failed. See <see cref="INetManager.Failed"/>.
/// </summary>
public sealed record NetRemoteEntityFailure
{
    /// <summary>
    /// The remote entity the failed get, set, or call was made through.
    /// </summary>
    public required INetRemoteEntity Entity { get; init; }

    /// <summary>
    /// The exception indicating why it failed.
    /// </summary>
    public required Exception Exception { get; init; }
}
