namespace Markwardt.NetChannel.Tests;

public sealed class NetMethodCallerChannelTests
{
    private static NetPendingCall MakeCall(ulong operation) => new(operation, EntityId: 1, MethodId: 2, NewMethod: null, Arguments: [], Completion: null);

    [Fact]
    public void AllocateOperation_IncrementsSequentially()
    {
        NetMethodCallerChannel channel = new();
        Assert.Equal(0ul, channel.AllocateOperation());
        Assert.Equal(1ul, channel.AllocateOperation());
        Assert.Equal(2ul, channel.AllocateOperation());
    }

    [Fact]
    public void Enqueue_WhenIdle_ReturnsCallImmediately()
    {
        NetMethodCallerChannel channel = new();
        NetPendingCall call = MakeCall(0);

        Assert.Same(call, channel.Enqueue(call));
    }

    [Fact]
    public void Enqueue_WhenBusy_QueuesAndReturnsNull()
    {
        NetMethodCallerChannel channel = new();
        channel.Enqueue(MakeCall(0));

        Assert.Null(channel.Enqueue(MakeCall(1)));
    }

    [Fact]
    public void Advance_ReturnsNextQueuedCall()
    {
        NetMethodCallerChannel channel = new();
        channel.Enqueue(MakeCall(0));
        NetPendingCall second = MakeCall(1);
        channel.Enqueue(second);

        Assert.Same(second, channel.Advance());
    }

    [Fact]
    public void Advance_WithNothingQueued_ReturnsNull()
    {
        NetMethodCallerChannel channel = new();
        channel.Enqueue(MakeCall(0));

        Assert.Null(channel.Advance());
    }

    [Fact]
    public void GetDueForResend_BeforeIntervalElapses_ReturnsNull()
    {
        NetMethodCallerChannel channel = new();
        NetPendingCall call = MakeCall(0);
        channel.Enqueue(call);
        channel.RecordSent(1);

        Assert.Null(channel.GetDueForResend(TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void GetDueForResend_AfterIntervalElapses_ReturnsOutstandingCall()
    {
        NetMethodCallerChannel channel = new();
        NetPendingCall call = MakeCall(0);
        channel.Enqueue(call);
        channel.RecordSent(1);

        Assert.Same(call, channel.GetDueForResend(TimeSpan.Zero));
    }

    [Fact]
    public void GetDueForResend_WhenIdle_ReturnsNull()
    {
        NetMethodCallerChannel channel = new();
        Assert.Null(channel.GetDueForResend(TimeSpan.Zero));
    }
}

public sealed class NetMethodReceiverChannelTests
{
    [Fact]
    public void Decide_FirstOperation_IsRunNew()
    {
        NetMethodReceiverChannel channel = new();
        Assert.Equal(NetMethodChannelDecision.RunNew, channel.Decide(0));
    }

    [Fact]
    public void Decide_SameOperationStillInProgress_IsIgnore()
    {
        NetMethodReceiverChannel channel = new();
        channel.Decide(5);

        Assert.Equal(NetMethodChannelDecision.Ignore, channel.Decide(5));
    }

    [Fact]
    public void Decide_HigherOperationWhileInProgress_IsRunNew()
    {
        NetMethodReceiverChannel channel = new();
        channel.Decide(5);

        Assert.Equal(NetMethodChannelDecision.RunNew, channel.Decide(6));
    }

    [Fact]
    public void Decide_AfterFinish_SameOperation_IsResendOutcome()
    {
        NetMethodReceiverChannel channel = new();
        channel.Decide(5);
        channel.Finish(5, new Reply { InResponseTo = 100 });

        Assert.Equal(NetMethodChannelDecision.ResendOutcome, channel.Decide(5));
    }

    [Fact]
    public void GetFinishedOutcome_ReturnsCachedReplyForMatchingOperation()
    {
        NetMethodReceiverChannel channel = new();
        Reply reply = new() { InResponseTo = 42 };
        channel.Decide(5);
        channel.Finish(5, reply);

        Assert.Same(reply, channel.GetFinishedOutcome(5));
    }

    [Fact]
    public void GetFinishedOutcome_ReturnsNullForMismatchedOperation()
    {
        NetMethodReceiverChannel channel = new();
        channel.Decide(5);
        channel.Finish(5, new Reply());

        Assert.Null(channel.GetFinishedOutcome(6));
    }

    [Fact]
    public void Finish_ThenNewerOperation_IsRunNew()
    {
        NetMethodReceiverChannel channel = new();
        channel.Decide(5);
        channel.Finish(5, new Reply());

        Assert.Equal(NetMethodChannelDecision.RunNew, channel.Decide(6));
    }

    [Fact]
    public void Decide_StaleOperationAfterANewerOneFinished_IsIgnored()
    {
        NetMethodReceiverChannel channel = new();

        Assert.Equal(NetMethodChannelDecision.RunNew, channel.Decide(0));
        channel.Finish(0, new Reply());
        Assert.Equal(NetMethodChannelDecision.RunNew, channel.Decide(1));
        channel.Finish(1, new Reply());

        Assert.Equal(NetMethodChannelDecision.Ignore, channel.Decide(0));
    }

    [Fact]
    public void Decide_StaleOperationWhileANewerOneIsInProgress_IsIgnored()
    {
        NetMethodReceiverChannel channel = new();

        Assert.Equal(NetMethodChannelDecision.RunNew, channel.Decide(4));

        Assert.Equal(NetMethodChannelDecision.Ignore, channel.Decide(3));
    }

    [Fact]
    public void Enqueue_StartsTheResendTimerSoAnImmediateSweepDoesNotDuplicateTheCall()
    {
        NetMethodCallerChannel channel = new();

        NetPendingCall? outstanding = channel.Enqueue(new NetPendingCall(0, 1, 0, null, [], null));

        Assert.NotNull(outstanding);
        Assert.Null(channel.GetDueForResend(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Advance_RestartsTheResendTimerForTheNewlyOutstandingCall()
    {
        NetMethodCallerChannel channel = new();
        channel.Enqueue(new NetPendingCall(0, 1, 0, null, [], null));
        channel.Enqueue(new NetPendingCall(1, 1, 0, null, [], null));
        channel.RecordSent(1);

        NetPendingCall? next = channel.Advance();

        Assert.NotNull(next);
        Assert.Null(channel.GetDueForResend(TimeSpan.FromSeconds(1)));
    }
}
