namespace Markwardt.NetChannel.Internal;

/// <summary>
/// Frames and unframes a UDP datagram around a serialized <see cref="Packet"/> (Docs/Wire.md#datagram-layout),
/// implemented by <see cref="NetDatagramCodec"/>.
/// </summary>
internal interface INetDatagramCodec
{
    /// <summary>
    /// Encodes a packet into a pooled buffer, computing and setting its <see cref="Packet.Tag"/> first if a
    /// session key is given.
    /// </summary>
    /// <param name="packet">The packet to encode. Its <see cref="Packet.Tag"/> is overwritten if <paramref name="sessionKey"/> is given.</param>
    /// <param name="sessionKey">The session key to authenticate the packet with, or <see langword="null"/> for a handshake packet with no session key yet.</param>
    /// <param name="length">The number of bytes written to the returned buffer.</param>
    /// <returns>A buffer rented from <see cref="ArrayPool{T}.Shared"/> — the caller must return it.</returns>
    byte[] Encode(Packet packet, ReadOnlyMemory<byte>? sessionKey, out int length);

    /// <summary>
    /// Decodes a datagram into a packet, verifying its checksum first if one is present.
    /// </summary>
    /// <param name="datagram">The received datagram.</param>
    /// <returns>The decoded packet, or <see langword="null"/> if it was malformed or failed its checksum.</returns>
    Packet? Decode(ReadOnlySpan<byte> datagram);

    /// <summary>
    /// Verifies a packet's <see cref="Packet.Tag"/> against the given session key.
    /// </summary>
    /// <param name="packet">The packet to verify.</param>
    /// <param name="sessionKey">The session key to verify against.</param>
    /// <returns><see langword="true"/> if the tag verifies; otherwise, <see langword="false"/>.</returns>
    bool VerifyTag(Packet packet, ReadOnlySpan<byte> sessionKey);
}

/// <inheritdoc cref="INetDatagramCodec" />
internal sealed class NetDatagramCodec(INetCrypto crypto) : INetDatagramCodec
{
    /// <inheritdoc />
    public byte[] Encode(Packet packet, ReadOnlyMemory<byte>? sessionKey, out int length)
    {
        bool hasChecksum = sessionKey is null;

        if (sessionKey is { } key)
        {
            packet.Tag = ByteString.Empty;
            byte[] fullTag = crypto.ComputeHmacSha256(key.Span, packet.ToByteArray());
            packet.Tag = ByteString.CopyFrom(fullTag, 0, 16);
        }

        int payloadSize = packet.CalculateSize();
        int prefixSize = hasChecksum ? 5 : 1;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(prefixSize + payloadSize);

        using (CodedOutputStream stream = new(new MemoryStream(buffer, prefixSize, payloadSize)))
        {
            packet.WriteTo(stream);
            stream.Flush();
        }

        if (hasChecksum)
        {
            uint checksum = crypto.ComputeCrc32C(buffer.AsSpan(prefixSize, payloadSize));
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(1, 4), checksum);
        }

        buffer[0] = hasChecksum ? (byte)1 : (byte)0;
        length = prefixSize + payloadSize;
        return buffer;
    }

    /// <inheritdoc />
    public Packet? Decode(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length < 1)
        {
            return null;
        }

        bool hasChecksum = datagram[0] != 0;
        int offset = 1;

        if (hasChecksum)
        {
            if (datagram.Length < 5)
            {
                return null;
            }

            uint claimed = BinaryPrimitives.ReadUInt32LittleEndian(datagram.Slice(1, 4));
            offset = 5;

            if (crypto.ComputeCrc32C(datagram[offset..]) != claimed)
            {
                return null;
            }
        }

        try
        {
            return Packet.Parser.ParseFrom(datagram[offset..]);
        }
        catch (InvalidProtocolBufferException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public bool VerifyTag(Packet packet, ReadOnlySpan<byte> sessionKey)
    {
        ByteString originalTag = packet.Tag;
        packet.Tag = ByteString.Empty;
        byte[] expected = crypto.ComputeHmacSha256(sessionKey, packet.ToByteArray());
        packet.Tag = originalTag;
        return originalTag.Span.Length == 16 && CryptographicOperations.FixedTimeEquals(expected.AsSpan(0, 16), originalTag.Span);
    }
}
