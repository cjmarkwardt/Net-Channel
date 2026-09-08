namespace Markwardt.NetChannel.Internal;

/// <summary>
/// Per-(connection, entity_id, property_id, direction) state for a property's fire-and-supersede delivery
/// (Docs/Wire.md#property-delivery).
/// </summary>
internal sealed class NetPropertyChannel
{
    private readonly Lock gate = new();

    private ReadOnlyMemory<byte>? pendingValue;

    private ulong? lastSentSequence;

    private DateTime lastSentAt;

    private bool acknowledged = true;

    /// <summary>
    /// The <c>Packet.sequence</c> of this channel's most recent attempt, or <see langword="null"/> if nothing
    /// has been sent yet. A resend supersedes it, so whoever tracks a reply for it can stop.
    /// </summary>
    public ulong? LastSentSequence
    {
        get
        {
            lock (gate)
            {
                return lastSentSequence;
            }
        }
    }

    /// <summary>
    /// Records that a value was just sent on this channel, for the given <c>Packet.sequence</c>.
    /// </summary>
    /// <param name="sequence">The sequence the value was sent with.</param>
    /// <param name="value">The value, kept in case it needs to be resent with a fresher value later.</param>
    public void RecordSent(ulong sequence, ReadOnlyMemory<byte> value)
    {
        lock (gate)
        {
            lastSentSequence = sequence;
            lastSentAt = DateTime.UtcNow;
            pendingValue = value;
            acknowledged = false;
        }
    }

    /// <summary>
    /// Records that an acknowledgment covering the given sequence has arrived, idling the channel if it was
    /// this channel's latest sent value or a later one.
    /// </summary>
    /// <param name="sequence">The sequence the acknowledgment covers.</param>
    public void Acknowledge(ulong sequence)
    {
        lock (gate)
        {
            if (lastSentSequence is null || sequence >= lastSentSequence)
            {
                acknowledged = true;
                pendingValue = null;
            }
        }
    }

    /// <summary>
    /// Checks whether this channel's most recently sent value is still unacknowledged after the given
    /// interval, returning it for resending if so.
    /// </summary>
    /// <param name="interval">How long an unacknowledged value must have been outstanding to be due for resend.</param>
    /// <returns>The value to resend, or <see langword="null"/> if nothing is due.</returns>
    public ReadOnlyMemory<byte>? GetValueDueForResend(TimeSpan interval)
    {
        lock (gate)
        {
            return !acknowledged && pendingValue is not null && DateTime.UtcNow - lastSentAt >= interval ? pendingValue : null;
        }
    }
}
