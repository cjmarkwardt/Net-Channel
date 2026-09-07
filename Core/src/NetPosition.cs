namespace Markwardt.NetChannel;

/// <summary>
/// A position and an optional visibility range, used together for position-based visibility filtering. See
/// <see cref="NetPositionAttribute"/>, <see cref="INetConnection.Position"/>, and
/// <see cref="INetView.SetViewerPosition"/>.
/// </summary>
/// <param name="Position">The position.</param>
/// <param name="Range">The hysteresis visibility range from <paramref name="Position"/>, or <see langword="null"/> for no range-based cutoff.</param>
public sealed record NetPosition(Vector3 Position, NetRange? Range = null)
{
    /// <summary>
    /// Implicitly converts a position with no range into a <see cref="NetPosition"/>.
    /// </summary>
    /// <param name="position">The position.</param>
    /// <returns>The equivalent <see cref="NetPosition"/>, with <see cref="Range"/> left <see langword="null"/>.</returns>
    public static implicit operator NetPosition(Vector3 position) => new(position);
}
