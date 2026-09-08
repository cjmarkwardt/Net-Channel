namespace Markwardt.NetChannel.Tests;

public sealed class NetDatagramCodecTests
{
    private readonly NetDatagramCodec codec = new(new NetCrypto());

    [Fact]
    public void Encode_WithoutSessionKey_SetsChecksumFlagAndRoundTrips()
    {
        Packet packet = new() { Ping = new Ping() };

        byte[] buffer = codec.Encode(packet, null, out int length);

        Assert.Equal(1, buffer[0]);

        Packet? decoded = codec.Decode(buffer.AsSpan(0, length));
        Assert.NotNull(decoded);
        Assert.Equal(Packet.PayloadOneofCase.Ping, decoded.PayloadCase);
    }

    [Fact]
    public void Encode_WithSessionKey_OmitsChecksumAndSetsTag()
    {
        byte[] sessionKey = new NetCrypto().GenerateRandom(32);
        Packet packet = new() { ConnectionId = 5, Ping = new Ping() };

        byte[] buffer = codec.Encode(packet, sessionKey, out int length);

        Assert.Equal(0, buffer[0]);

        Packet? decoded = codec.Decode(buffer.AsSpan(0, length));
        Assert.NotNull(decoded);
        Assert.NotEmpty(decoded.Tag.ToByteArray());
        Assert.True(codec.VerifyTag(decoded, sessionKey));
    }

    [Fact]
    public void VerifyTag_FailsWithWrongKey()
    {
        byte[] sessionKey = new NetCrypto().GenerateRandom(32);
        byte[] wrongKey = new NetCrypto().GenerateRandom(32);
        Packet packet = new() { ConnectionId = 5, Ping = new Ping() };

        byte[] buffer = codec.Encode(packet, sessionKey, out int length);
        Packet decoded = codec.Decode(buffer.AsSpan(0, length))!;

        Assert.False(codec.VerifyTag(decoded, wrongKey));
    }

    [Fact]
    public void Decode_RejectsCorruptedChecksum()
    {
        Packet packet = new() { Ping = new Ping() };
        byte[] buffer = codec.Encode(packet, null, out int length);

        buffer[1] ^= 0xFF;

        Assert.Null(codec.Decode(buffer.AsSpan(0, length)));
    }

    [Fact]
    public void Decode_RejectsCorruptedPayloadUnderChecksum()
    {
        Packet packet = new() { Ping = new Ping() };
        byte[] buffer = codec.Encode(packet, null, out int length);

        buffer[length - 1] ^= 0xFF;

        Assert.Null(codec.Decode(buffer.AsSpan(0, length)));
    }

    [Fact]
    public void Decode_RejectsEmptyDatagram()
    {
        Assert.Null(codec.Decode([]));
    }

    [Fact]
    public void Decode_RejectsTruncatedChecksumPrefix()
    {
        Assert.Null(codec.Decode([1, 2, 3]));
    }

    [Fact]
    public void Decode_RejectsMalformedProtobufPayload()
    {
        NetCrypto crypto = new();
        byte[] garbage = [0xFF, 0xFF, 0xFF];
        uint crc = crypto.ComputeCrc32C(garbage);

        byte[] buffer = new byte[5 + garbage.Length];
        buffer[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(1, 4), crc);
        garbage.CopyTo(buffer.AsSpan(5));

        Assert.Null(codec.Decode(buffer));
    }
}
