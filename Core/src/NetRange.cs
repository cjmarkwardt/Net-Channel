namespace Markwardt.NetChannel;

/// <summary>
/// A hysteresis pair of distances used by <see cref="NetPosition"/> for position-based visibility filtering:
/// an invisible relationship becomes visible once within <see cref="EnterDistance"/>, but a visible one only
/// becomes invisible once beyond <see cref="ExitDistance"/>. Keeping <see cref="ExitDistance"/> greater than
/// <see cref="EnterDistance"/> creates a buffer zone in which an existing relationship's visibility is left
/// unchanged, preventing an entity hovering near a single boundary from flickering in and out of visibility.
/// See <see cref="NetPositionAttribute"/>.
/// </summary>
/// <param name="EnterDistance">The distance within which an invisible relationship becomes visible.</param>
/// <param name="ExitDistance">
/// The distance beyond which a visible relationship becomes invisible. Should be strictly greater than
/// <paramref name="EnterDistance"/> — equal values collapse the buffer back to a plain single-threshold
/// cutoff, defeating the purpose of a hysteresis pair.
/// </param>
public sealed record NetRange(float EnterDistance, float ExitDistance);
