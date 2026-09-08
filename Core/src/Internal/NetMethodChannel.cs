namespace Markwardt.NetChannel.Internal;

/// <summary>
/// A queued call on a <see cref="NetMethodCallerChannel"/>, awaiting its turn to be sent.
/// </summary>
internal sealed record NetPendingCall(ulong Operation, ulong EntityId, uint MethodId, string? NewMethod, IReadOnlyList<ReadOnlyMemory<byte>> Arguments, TaskCompletionSource<Reply>? Completion);

/// <summary>
/// Per-(connection, entity_id, method_id) sender-side state for a method's guaranteed, ordered,
/// single-outstanding delivery (Docs/Wire.md#method-delivery).
/// </summary>
internal sealed class NetMethodCallerChannel
{
    private readonly Queue<NetPendingCall> queue = new();

    private readonly Lock gate = new();

    private ulong nextOperation;

    private NetPendingCall? outstanding;

    private ulong? lastSentSequence;

    // Stamped whenever a call becomes outstanding, not only when one is actually sent, because the channel is
    // visible to the concurrently-running sweep loop from that moment — before the matching RecordSent call.
    // Left at the previous call's value (or the default DateTime.MinValue) it would read as long overdue for
    // that whole window, and the sweep would fire a premature duplicate of a call already on its way.
    private DateTime lastSentAt = DateTime.UtcNow;

    /// <summary>
    /// Allocates the next operation id for a new call on this channel.
    /// </summary>
    /// <returns>The allocated operation id.</returns>
    public ulong AllocateOperation()
    {
        lock (gate)
        {
            return nextOperation++;
        }
    }

    /// <summary>
    /// Queues a call, returning it back if the channel is idle and it should be sent immediately.
    /// </summary>
    /// <param name="call">The call to queue.</param>
    /// <returns>The call to send immediately, or <see langword="null"/> if it must wait behind another.</returns>
    public NetPendingCall? Enqueue(NetPendingCall call)
    {
        lock (gate)
        {
            queue.Enqueue(call);

            if (outstanding is null)
            {
                outstanding = queue.Dequeue();
                lastSentAt = DateTime.UtcNow;
                return outstanding;
            }

            return null;
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
    /// Records that the outstanding call was just (re)sent with the given sequence.
    /// </summary>
    /// <param name="sequence">The sequence the call was sent with.</param>
    public void RecordSent(ulong sequence)
    {
        lock (gate)
        {
            lastSentSequence = sequence;
            lastSentAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Completes the outstanding call and advances to the next queued one, if any.
    /// </summary>
    /// <returns>The next call to send, or <see langword="null"/> if the channel is now idle.</returns>
    public NetPendingCall? Advance()
    {
        lock (gate)
        {
            outstanding = queue.TryDequeue(out NetPendingCall? next) ? next : null;
            lastSentAt = DateTime.UtcNow;
            return outstanding;
        }
    }

    /// <summary>
    /// Checks whether the outstanding call is still unacknowledged after the given interval, returning it for
    /// resending if so.
    /// </summary>
    /// <param name="interval">How long an unacknowledged call must have been outstanding to be due for resend.</param>
    /// <returns>The call to resend, or <see langword="null"/> if nothing is due.</returns>
    public NetPendingCall? GetDueForResend(TimeSpan interval)
    {
        lock (gate)
        {
            return outstanding is not null && DateTime.UtcNow - lastSentAt >= interval ? outstanding : null;
        }
    }
}

/// <summary>
/// Per-(connection, entity_id, method_id) receiver-side state for recognizing a retransmission apart from a
/// genuinely new call (Docs/Wire.md#method-delivery).
/// </summary>
internal sealed class NetMethodReceiverChannel
{
    private readonly Lock gate = new();

    private ulong? inProgressOperation;

    private ulong? finishedOperation;

    private ulong? highestSeenOperation;

    private Reply? finishedOutcome;

    /// <summary>
    /// Decides what a receiver should do with an incoming call's operation.
    /// </summary>
    /// <param name="operation">The incoming call's operation id.</param>
    /// <returns>What the receiver should do with it.</returns>
    public NetMethodChannelDecision Decide(ulong operation)
    {
        lock (gate)
        {
            if (operation == finishedOperation)
            {
                return NetMethodChannelDecision.ResendOutcome;
            }

            if (operation == inProgressOperation)
            {
                return NetMethodChannelDecision.Ignore;
            }

            // Only an operation higher than any seen before is genuinely new. A lower one is a retransmission
            // of a call already answered and since superseded, whose outcome is no longer cached — running it
            // again would invoke the controller method a second time for a single call.
            if (highestSeenOperation is { } highest && operation <= highest)
            {
                return NetMethodChannelDecision.Ignore;
            }

            highestSeenOperation = operation;
            inProgressOperation = operation;
            return NetMethodChannelDecision.RunNew;
        }
    }

    /// <summary>
    /// Gets the cached outcome for the operation currently considered finished, if it matches.
    /// </summary>
    /// <param name="operation">The operation to get the cached outcome for.</param>
    /// <returns>The cached outcome, or <see langword="null"/> if it doesn't match.</returns>
    public Reply? GetFinishedOutcome(ulong operation)
    {
        lock (gate)
        {
            return operation == finishedOperation ? finishedOutcome : null;
        }
    }

    /// <summary>
    /// Records that the in-progress operation has finished, caching its outcome for later retransmissions.
    /// </summary>
    /// <param name="operation">The operation that finished.</param>
    /// <param name="outcome">The outcome to cache and resend for any retransmission of it.</param>
    public void Finish(ulong operation, Reply outcome)
    {
        lock (gate)
        {
            if (inProgressOperation == operation)
            {
                inProgressOperation = null;
            }

            finishedOperation = operation;
            finishedOutcome = outcome;
        }
    }
}

/// <summary>
/// What a method channel receiver should do with an incoming call's operation (Docs/Wire.md#method-delivery).
/// </summary>
internal enum NetMethodChannelDecision
{
    /// <summary>
    /// The operation is genuinely new: run it.
    /// </summary>
    RunNew,

    /// <summary>
    /// The operation matches the one still being run: ignore it, since its own answer is already forthcoming.
    /// </summary>
    Ignore,

    /// <summary>
    /// The operation matches the last one that finished: resend its cached outcome without re-running it.
    /// </summary>
    ResendOutcome,
}
