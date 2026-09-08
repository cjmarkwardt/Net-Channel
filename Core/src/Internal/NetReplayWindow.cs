namespace Markwardt.NetChannel.Internal;

/// <summary>
/// Tracks the highest <c>Packet.sequence</c> accepted so far on one direction of a connection, plus a sliding
/// window of recently-accepted values, to reject a replayed or duplicated packet while tolerating UDP
/// reordering (Docs/Wire.md#replay-rejection).
/// </summary>
internal sealed class NetReplayWindow
{
    private readonly Lock gate = new();

    private ulong highest;

    private ulong seenMask;

    private bool hasSeen;

    /// <summary>
    /// Checks whether a packet with the given sequence should be accepted, recording it as seen if so.
    /// </summary>
    /// <param name="sequence">The packet's sequence.</param>
    /// <returns><see langword="true"/> if the packet is new and should be accepted; otherwise, <see langword="false"/>.</returns>
    public bool ShouldAccept(ulong sequence)
    {
        lock (gate)
        {
            if (!hasSeen)
            {
                hasSeen = true;
                highest = sequence;
                seenMask = 1;
                return true;
            }

            if (sequence > highest)
            {
                ulong shift = sequence - highest;
                seenMask = shift >= 64 ? 1UL : ((seenMask << (int)shift) | 1UL);
                highest = sequence;
                return true;
            }

            ulong distance = highest - sequence;

            if (distance >= 64)
            {
                return false;
            }

            ulong bit = 1UL << (int)distance;

            if ((seenMask & bit) != 0)
            {
                return false;
            }

            seenMask |= bit;
            return true;
        }
    }
}
