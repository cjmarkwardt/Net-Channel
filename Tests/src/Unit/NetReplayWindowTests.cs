namespace Markwardt.NetChannel.Tests;

public sealed class NetReplayWindowTests
{
    [Fact]
    public void FirstSequence_IsAlwaysAccepted()
    {
        NetReplayWindow window = new();
        Assert.True(window.ShouldAccept(50));
    }

    [Fact]
    public void IncreasingSequences_AreAccepted()
    {
        NetReplayWindow window = new();
        Assert.True(window.ShouldAccept(1));
        Assert.True(window.ShouldAccept(2));
        Assert.True(window.ShouldAccept(3));
    }

    [Fact]
    public void DuplicateSequence_IsRejected()
    {
        NetReplayWindow window = new();
        Assert.True(window.ShouldAccept(5));
        Assert.False(window.ShouldAccept(5));
    }

    [Fact]
    public void OutOfOrderWithinWindow_IsAccepted()
    {
        NetReplayWindow window = new();
        Assert.True(window.ShouldAccept(10));
        Assert.True(window.ShouldAccept(5));
        Assert.True(window.ShouldAccept(7));
    }

    [Fact]
    public void RepeatOfOutOfOrderSequence_IsRejected()
    {
        NetReplayWindow window = new();
        Assert.True(window.ShouldAccept(10));
        Assert.True(window.ShouldAccept(5));
        Assert.False(window.ShouldAccept(5));
    }

    [Fact]
    public void SequenceFarBelowWindow_IsRejected()
    {
        NetReplayWindow window = new();
        Assert.True(window.ShouldAccept(1000));
        Assert.False(window.ShouldAccept(1000 - 64));
    }

    [Fact]
    public void LargeForwardJump_StillAccepts()
    {
        NetReplayWindow window = new();
        Assert.True(window.ShouldAccept(1));
        Assert.True(window.ShouldAccept(1000));
        Assert.False(window.ShouldAccept(1));
    }

    [Fact]
    public void SequenceJustInsideWindow_IsAccepted()
    {
        NetReplayWindow window = new();
        Assert.True(window.ShouldAccept(1000));
        Assert.True(window.ShouldAccept(1000 - 63));
    }
}
