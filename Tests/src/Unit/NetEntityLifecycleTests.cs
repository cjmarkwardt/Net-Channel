namespace Markwardt.NetChannel.Tests;

public sealed class NetEntityLifecycleTests
{
    [Fact]
    public void InitialState_IsCreatePending()
    {
        NetEntityLifecycle lifecycle = new();
        Assert.Equal(NetLifecycleState.CreatePending, lifecycle.State);
    }

    [Fact]
    public void Acknowledge_Create_TransitionsToCreated()
    {
        NetEntityLifecycle lifecycle = new();
        lifecycle.RecordSent(1);

        bool accepted = lifecycle.Acknowledge(1, out bool shouldSendDestroyNow);

        Assert.True(accepted);
        Assert.False(shouldSendDestroyNow);
        Assert.Equal(NetLifecycleState.Created, lifecycle.State);
    }

    [Fact]
    public void Acknowledge_WithStaleSequence_IsRejected()
    {
        NetEntityLifecycle lifecycle = new();
        lifecycle.RecordSent(5);

        bool accepted = lifecycle.Acknowledge(3, out bool shouldSendDestroyNow);

        Assert.False(accepted);
        Assert.False(shouldSendDestroyNow);
        Assert.Equal(NetLifecycleState.CreatePending, lifecycle.State);
    }

    [Fact]
    public void RequestDestroy_WhileCreated_SendsImmediately()
    {
        NetEntityLifecycle lifecycle = new();
        lifecycle.RecordSent(1);
        lifecycle.Acknowledge(1, out _);

        Assert.True(lifecycle.RequestDestroy());
        Assert.Equal(NetLifecycleState.DestroyPending, lifecycle.State);
    }

    [Fact]
    public void RequestDestroy_WhileCreatePending_QueuesUntilAcknowledged()
    {
        NetEntityLifecycle lifecycle = new();
        lifecycle.RecordSent(1);

        Assert.False(lifecycle.RequestDestroy());
        Assert.Equal(NetLifecycleState.CreatePending, lifecycle.State);

        bool accepted = lifecycle.Acknowledge(1, out bool shouldSendDestroyNow);

        Assert.True(accepted);
        Assert.True(shouldSendDestroyNow);
        Assert.Equal(NetLifecycleState.DestroyPending, lifecycle.State);
    }

    [Fact]
    public void RequestDestroy_CalledTwice_SecondCallIsNoOp()
    {
        NetEntityLifecycle lifecycle = new();
        lifecycle.RecordSent(1);
        lifecycle.Acknowledge(1, out _);

        Assert.True(lifecycle.RequestDestroy());
        Assert.False(lifecycle.RequestDestroy());
    }

    [Fact]
    public void Acknowledge_Destroy_TransitionsToDestroyed()
    {
        NetEntityLifecycle lifecycle = new();
        lifecycle.RecordSent(1);
        lifecycle.Acknowledge(1, out _);
        lifecycle.RequestDestroy();
        lifecycle.RecordSent(2);

        bool accepted = lifecycle.Acknowledge(2, out bool shouldSendDestroyNow);

        Assert.True(accepted);
        Assert.False(shouldSendDestroyNow);
        Assert.Equal(NetLifecycleState.Destroyed, lifecycle.State);
    }

    [Fact]
    public void RequestDestroy_AfterDestroyed_ReturnsFalse()
    {
        NetEntityLifecycle lifecycle = new();
        lifecycle.RecordSent(1);
        lifecycle.Acknowledge(1, out _);
        lifecycle.RequestDestroy();
        lifecycle.RecordSent(2);
        lifecycle.Acknowledge(2, out _);

        Assert.False(lifecycle.RequestDestroy());
    }

    [Fact]
    public void IsDueForResend_BeforeIntervalElapses_ReturnsFalse()
    {
        NetEntityLifecycle lifecycle = new();
        lifecycle.RecordSent(1);

        Assert.False(lifecycle.IsDueForResend(TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void IsDueForResend_AfterIntervalElapses_ReturnsTrue()
    {
        NetEntityLifecycle lifecycle = new();
        lifecycle.RecordSent(1);

        Assert.True(lifecycle.IsDueForResend(TimeSpan.Zero));
    }

    [Fact]
    public void IsDueForResend_WhenDestroyed_ReturnsFalse()
    {
        NetEntityLifecycle lifecycle = new();
        lifecycle.RecordSent(1);
        lifecycle.Acknowledge(1, out _);
        lifecycle.RequestDestroy();
        lifecycle.RecordSent(2);
        lifecycle.Acknowledge(2, out _);

        Assert.False(lifecycle.IsDueForResend(TimeSpan.Zero));
    }
}
