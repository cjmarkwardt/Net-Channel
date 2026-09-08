namespace Markwardt.NetChannel.Tests;

public sealed class NetPayloadCodecTests
{
    private readonly NetPayloadCodec codec = new(new NetCrypto());

    private readonly byte[] payloadKey = new NetCrypto().GenerateRandom(32);

    [Fact]
    public void Encrypt_ThenDecrypt_RoundTrips()
    {
        byte[] plaintext = "argument value"u8.ToArray();
        byte[] sealedValue = codec.Encrypt(payloadKey, connectionId: 1, entityId: 2, methodId: 3, operation: 4, argumentPosition: 0, plaintext);
        byte[]? opened = codec.Decrypt(payloadKey, connectionId: 1, entityId: 2, methodId: 3, operation: 4, argumentPosition: 0, sealedValue);

        Assert.Equal(plaintext, opened);
    }

    [Fact]
    public void Decrypt_FailsWithMismatchedEntityId()
    {
        byte[] sealedValue = codec.Encrypt(payloadKey, 1, 2, 3, 4, 0, "value"u8.ToArray());
        Assert.Null(codec.Decrypt(payloadKey, 1, entityId: 99, 3, 4, 0, sealedValue));
    }

    [Fact]
    public void Decrypt_FailsWithMismatchedOperation()
    {
        byte[] sealedValue = codec.Encrypt(payloadKey, 1, 2, 3, operation: 4, 0, "value"u8.ToArray());
        Assert.Null(codec.Decrypt(payloadKey, 1, 2, 3, operation: 5, 0, sealedValue));
    }

    [Fact]
    public void Decrypt_FailsWithMismatchedArgumentPosition()
    {
        byte[] sealedValue = codec.Encrypt(payloadKey, 1, 2, 3, 4, argumentPosition: 0, "value"u8.ToArray());
        Assert.Null(codec.Decrypt(payloadKey, 1, 2, 3, 4, argumentPosition: 1, sealedValue));
    }

    [Fact]
    public void Decrypt_FailsWithWrongPayloadKey()
    {
        byte[] sealedValue = codec.Encrypt(payloadKey, 1, 2, 3, 4, 0, "value"u8.ToArray());
        byte[] wrongKey = new NetCrypto().GenerateRandom(32);

        Assert.Null(codec.Decrypt(wrongKey, 1, 2, 3, 4, 0, sealedValue));
    }

    [Fact]
    public void ArgumentPosition_And_Result_ProduceDifferentCiphertextForSameValue()
    {
        byte[] plaintext = "same value"u8.ToArray();
        byte[] asArgument = codec.Encrypt(payloadKey, 1, 2, 3, 4, argumentPosition: 0, plaintext);
        byte[] asResult = codec.Encrypt(payloadKey, 1, 2, 3, 4, argumentPosition: null, plaintext);

        Assert.NotEqual(asArgument, asResult);
    }

    [Fact]
    public void DifferentArgumentPositions_ProduceDifferentCiphertextForSameValue()
    {
        byte[] plaintext = "same value"u8.ToArray();
        byte[] first = codec.Encrypt(payloadKey, 1, 2, 3, 4, argumentPosition: 0, plaintext);
        byte[] second = codec.Encrypt(payloadKey, 1, 2, 3, 4, argumentPosition: 1, plaintext);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Retransmission_WithSameOperation_ProducesIdenticalCiphertext()
    {
        byte[] plaintext = "call arguments"u8.ToArray();
        byte[] first = codec.Encrypt(payloadKey, 1, 2, 3, operation: 7, 0, plaintext);
        byte[] second = codec.Encrypt(payloadKey, 1, 2, 3, operation: 7, 0, plaintext);

        Assert.Equal(first, second);
    }
}
