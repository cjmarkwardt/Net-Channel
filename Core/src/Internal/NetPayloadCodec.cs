namespace Markwardt.NetChannel.Internal;

/// <summary>
/// Encrypts and decrypts a single <c>[NetSecure]</c> method's argument/result value (Docs/Wire.md#payload-encryption),
/// implemented by <see cref="NetPayloadCodec"/>.
/// </summary>
internal interface INetPayloadCodec
{
    /// <summary>
    /// Encrypts a single argument or result value under a key derived from the connection's payload key and
    /// this value's own identifying context, so it never repeats under the same key.
    /// </summary>
    /// <param name="payloadKey">The connection's payload key.</param>
    /// <param name="connectionId">The connection's id.</param>
    /// <param name="entityId">The entity id the value belongs to.</param>
    /// <param name="methodId">The method id the value belongs to.</param>
    /// <param name="operation">The call's operation id.</param>
    /// <param name="argumentPosition">The argument's position, or <see langword="null"/> for a result value.</param>
    /// <param name="plaintext">The value to encrypt.</param>
    /// <returns>The ciphertext, with its authentication tag appended.</returns>
    byte[] Encrypt(ReadOnlySpan<byte> payloadKey, ulong connectionId, ulong entityId, uint methodId, ulong operation, int? argumentPosition, ReadOnlySpan<byte> plaintext);

    /// <summary>
    /// Decrypts a value sealed by <see cref="Encrypt"/>.
    /// </summary>
    /// <param name="payloadKey">The connection's payload key.</param>
    /// <param name="connectionId">The connection's id.</param>
    /// <param name="entityId">The entity id the value belongs to.</param>
    /// <param name="methodId">The method id the value belongs to.</param>
    /// <param name="operation">The call's operation id.</param>
    /// <param name="argumentPosition">The argument's position, or <see langword="null"/> for a result value.</param>
    /// <param name="sealedValue">The ciphertext, with its authentication tag appended.</param>
    /// <returns>The decrypted value, or <see langword="null"/> if authentication failed.</returns>
    byte[]? Decrypt(ReadOnlySpan<byte> payloadKey, ulong connectionId, ulong entityId, uint methodId, ulong operation, int? argumentPosition, ReadOnlySpan<byte> sealedValue);
}

/// <inheritdoc cref="INetPayloadCodec" />
internal sealed class NetPayloadCodec(INetCrypto crypto) : INetPayloadCodec
{
    private static readonly byte[] InfoPrefix = "netchannel payload value v1"u8.ToArray();

    private static readonly byte[] Nonce = new byte[12];

    /// <inheritdoc />
    public byte[] Encrypt(ReadOnlySpan<byte> payloadKey, ulong connectionId, ulong entityId, uint methodId, ulong operation, int? argumentPosition, ReadOnlySpan<byte> plaintext) =>
        crypto.Seal(DeriveValueKey(payloadKey, entityId, methodId, operation, argumentPosition), Nonce, BuildAssociatedData(connectionId, entityId), plaintext);

    /// <inheritdoc />
    public byte[]? Decrypt(ReadOnlySpan<byte> payloadKey, ulong connectionId, ulong entityId, uint methodId, ulong operation, int? argumentPosition, ReadOnlySpan<byte> sealedValue) =>
        crypto.Open(DeriveValueKey(payloadKey, entityId, methodId, operation, argumentPosition), Nonce, BuildAssociatedData(connectionId, entityId), sealedValue);

    private byte[] DeriveValueKey(ReadOnlySpan<byte> payloadKey, ulong entityId, uint methodId, ulong operation, int? argumentPosition)
    {
        Span<byte> info = stackalloc byte[InfoPrefix.Length + 8 + 4 + 8 + 1 + 1];
        int offset = 0;
        InfoPrefix.CopyTo(info);
        offset += InfoPrefix.Length;
        BinaryPrimitives.WriteUInt64BigEndian(info[offset..], entityId);
        offset += 8;
        BinaryPrimitives.WriteUInt32BigEndian(info[offset..], methodId);
        offset += 4;
        BinaryPrimitives.WriteUInt64BigEndian(info[offset..], operation);
        offset += 8;

        if (argumentPosition is { } position)
        {
            info[offset++] = 0x00;
            info[offset++] = checked((byte)position);
        }
        else
        {
            info[offset++] = 0x01;
            info = info[..offset];
        }

        return crypto.DeriveKey(payloadKey, [], info, 32);
    }

    private static byte[] BuildAssociatedData(ulong connectionId, ulong entityId)
    {
        byte[] associatedData = new byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(associatedData.AsSpan(0, 8), connectionId);
        BinaryPrimitives.WriteUInt64BigEndian(associatedData.AsSpan(8, 8), entityId);
        return associatedData;
    }
}
