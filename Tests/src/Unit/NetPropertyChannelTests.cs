namespace Markwardt.NetChannel.Tests;

public sealed class NetPropertyChannelTests
{
    [Fact]
    public void GetValueDueForResend_BeforeIntervalElapses_ReturnsNull()
    {
        NetPropertyChannel channel = new();
        channel.RecordSent(1, "value"u8.ToArray());

        Assert.Null(channel.GetValueDueForResend(TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void GetValueDueForResend_AfterIntervalElapses_ReturnsValue()
    {
        NetPropertyChannel channel = new();
        byte[] value = "value"u8.ToArray();
        channel.RecordSent(1, value);

        Assert.Equal(value, channel.GetValueDueForResend(TimeSpan.Zero)!.Value.ToArray());
    }

    [Fact]
    public void Acknowledge_WithMatchingOrLaterSequence_IdlesChannel()
    {
        NetPropertyChannel channel = new();
        channel.RecordSent(5, "value"u8.ToArray());
        channel.Acknowledge(5);

        Assert.Null(channel.GetValueDueForResend(TimeSpan.Zero));
    }

    [Fact]
    public void Acknowledge_WithLaterSequence_IdlesChannel()
    {
        NetPropertyChannel channel = new();
        channel.RecordSent(5, "value"u8.ToArray());
        channel.Acknowledge(9);

        Assert.Null(channel.GetValueDueForResend(TimeSpan.Zero));
    }

    [Fact]
    public void Acknowledge_WithEarlierSequence_DoesNotIdleChannel()
    {
        NetPropertyChannel channel = new();
        channel.RecordSent(5, "value"u8.ToArray());
        channel.Acknowledge(3);

        Assert.NotNull(channel.GetValueDueForResend(TimeSpan.Zero));
    }

    [Fact]
    public void RecordSent_Again_UpdatesPendingValue()
    {
        NetPropertyChannel channel = new();
        channel.RecordSent(1, "first"u8.ToArray());
        channel.RecordSent(2, "second"u8.ToArray());

        Assert.Equal("second"u8.ToArray(), channel.GetValueDueForResend(TimeSpan.Zero)!.Value.ToArray());
    }
}
