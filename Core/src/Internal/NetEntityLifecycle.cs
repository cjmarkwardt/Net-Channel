namespace Markwardt.NetChannel.Internal;

/// <summary>
/// The state of an entity's single-outstanding lifecycle channel to one connection (Docs/Wire.md#method-delivery).
/// </summary>
internal enum NetLifecycleState
{
    /// <summary>
    /// The entity's <c>EntityCreate</c> has been sent and is awaiting acknowledgment.
    /// </summary>
    CreatePending,

    /// <summary>
    /// The entity's <c>EntityCreate</c> has been acknowledged and no <c>EntityDestroy</c> has been requested yet.
    /// </summary>
    Created,

    /// <summary>
    /// The entity's <c>EntityDestroy</c> has been sent and is awaiting acknowledgment.
    /// </summary>
    DestroyPending,

    /// <summary>
    /// The entity's <c>EntityDestroy</c> has been acknowledged.
    /// </summary>
    Destroyed,
}

/// <summary>
/// Per-(connection, entity_id) sender-side state for an entity's guaranteed, ordered, single-outstanding
/// <c>EntityCreate</c>/<c>EntityDestroy</c> delivery (Docs/Wire.md#method-delivery). Unlike a regular method
/// channel, a destroy requested while a create is still outstanding waits for that create's acknowledgment
/// before being sent, rather than being sent immediately, since the two share one channel and the receiver
/// would otherwise ignore a destroy for an entity id it doesn't recognize yet.
/// </summary>
internal sealed class NetEntityLifecycle
{
    private readonly Lock gate = new();

    private NetLifecycleState state = NetLifecycleState.CreatePending;

    private bool destroyRequested;

    private ulong? lastSentSequence;

    // Stamped whenever a message becomes pending, not only when one is actually sent, because the lifecycle
    // is visible to the concurrently-running sweep loop from the moment it enters that state — before the
    // matching RecordSent call. Left at its previous value (or the default DateTime.MinValue at construction)
    // it would read as long overdue for that whole window, and the sweep would fire a premature duplicate.
    private DateTime lastSentAt = DateTime.UtcNow;

    /// <summary>
    /// The lifecycle's current state.
    /// </summary>
    public NetLifecycleState State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

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
    /// Records that the current pending message (create or destroy) was just (re)sent with the given sequence.
    /// </summary>
    /// <param name="sequence">The sequence the message was sent with.</param>
    public void RecordSent(ulong sequence)
    {
        lock (gate)
        {
            lastSentSequence = sequence;
            lastSentAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Records that an acknowledgment covering the given sequence has arrived.
    /// </summary>
    /// <param name="sequence">The sequence the acknowledgment covers.</param>
    /// <param name="shouldSendDestroyNow">
    /// Set to <see langword="true"/> if this acknowledgment advances the channel directly into sending a
    /// destroy that was requested while the create was still outstanding.
    /// </param>
    /// <returns><see langword="true"/> if the acknowledgment was accepted; otherwise, <see langword="false"/>.</returns>
    public bool Acknowledge(ulong sequence, out bool shouldSendDestroyNow)
    {
        lock (gate)
        {
            shouldSendDestroyNow = false;

            if (lastSentSequence is { } last && sequence < last)
            {
                return false;
            }

            if (state == NetLifecycleState.CreatePending)
            {
                if (destroyRequested)
                {
                    state = NetLifecycleState.DestroyPending;
                    lastSentAt = DateTime.UtcNow;
                    shouldSendDestroyNow = true;
                }
                else
                {
                    state = NetLifecycleState.Created;
                }

                return true;
            }

            if (state == NetLifecycleState.DestroyPending)
            {
                state = NetLifecycleState.Destroyed;
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Requests that the entity be destroyed.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if the destroy should be sent immediately; <see langword="false"/> if it must
    /// wait for the outstanding create to be acknowledged first, or if a destroy was already requested.
    /// </returns>
    public bool RequestDestroy()
    {
        lock (gate)
        {
            if (state is NetLifecycleState.Destroyed or NetLifecycleState.DestroyPending)
            {
                return false;
            }

            if (state == NetLifecycleState.CreatePending)
            {
                destroyRequested = true;
                return false;
            }

            state = NetLifecycleState.DestroyPending;
            lastSentAt = DateTime.UtcNow;
            return true;
        }
    }

    /// <summary>
    /// Checks whether the current pending message is still unacknowledged after the given interval.
    /// </summary>
    /// <param name="interval">How long an unacknowledged message must have been outstanding to be due for resend.</param>
    /// <returns><see langword="true"/> if it is due for resend; otherwise, <see langword="false"/>.</returns>
    public bool IsDueForResend(TimeSpan interval)
    {
        lock (gate)
        {
            return state is NetLifecycleState.CreatePending or NetLifecycleState.DestroyPending && DateTime.UtcNow - lastSentAt >= interval;
        }
    }
}
