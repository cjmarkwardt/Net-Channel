namespace Markwardt.NetChannel.Tests;

public sealed class NetCryptoTests
{
    private readonly NetCrypto crypto = new();

    [Fact]
    public void GenerateRandom_ReturnsRequestedLength()
    {
        byte[] bytes = crypto.GenerateRandom(32);
        Assert.Equal(32, bytes.Length);
    }

    [Fact]
    public void GenerateRandom_ProducesDifferentValues()
    {
        byte[] a = crypto.GenerateRandom(32);
        byte[] b = crypto.GenerateRandom(32);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Sign_ThenVerify_Succeeds()
    {
        byte[] privateKey = crypto.GenerateRandom(32);
        using NSec.Cryptography.Key key = NSec.Cryptography.Key.Import(NSec.Cryptography.SignatureAlgorithm.Ed25519, privateKey, NSec.Cryptography.KeyBlobFormat.RawPrivateKey);
        byte[] publicKey = key.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey);

        byte[] message = "hello"u8.ToArray();
        byte[] signature = crypto.Sign(privateKey, message);

        Assert.True(crypto.Verify(publicKey, message, signature));
    }

    [Fact]
    public void Verify_FailsForTamperedMessage()
    {
        byte[] privateKey = crypto.GenerateRandom(32);
        using NSec.Cryptography.Key key = NSec.Cryptography.Key.Import(NSec.Cryptography.SignatureAlgorithm.Ed25519, privateKey, NSec.Cryptography.KeyBlobFormat.RawPrivateKey);
        byte[] publicKey = key.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey);

        byte[] signature = crypto.Sign(privateKey, "hello"u8.ToArray());

        Assert.False(crypto.Verify(publicKey, "goodbye"u8.ToArray(), signature));
    }

    [Fact]
    public void AgreementKeys_DeriveMatchingSharedKey()
    {
        using NetAgreementKey alice = crypto.CreateAgreementKey();
        using NetAgreementKey bob = crypto.CreateAgreementKey();

        byte[] aliceKey = alice.DeriveKey(bob.PublicKey, "salt"u8, "info"u8, 32);
        byte[] bobKey = bob.DeriveKey(alice.PublicKey, "salt"u8, "info"u8, 32);

        Assert.Equal(aliceKey, bobKey);
    }

    [Fact]
    public void AgreementKeys_DifferentInfo_ProducesDifferentKeys()
    {
        using NetAgreementKey alice = crypto.CreateAgreementKey();
        using NetAgreementKey bob = crypto.CreateAgreementKey();

        byte[] first = alice.DeriveKey(bob.PublicKey, "salt"u8, "info-a"u8, 32);
        byte[] second = alice.DeriveKey(bob.PublicKey, "salt"u8, "info-b"u8, 32);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void CreateAgreementKey_WithSeed_IsDeterministic()
    {
        byte[] seed = crypto.GenerateRandom(32);

        using NetAgreementKey first = crypto.CreateAgreementKey(seed);
        using NetAgreementKey second = crypto.CreateAgreementKey(seed);

        Assert.Equal(first.PublicKey.ToArray(), second.PublicKey.ToArray());
    }

    [Fact]
    public void DeriveKey_IsDeterministicAndCorrectLength()
    {
        byte[] key = "input-keying-material-should-be-long"u8.ToArray();

        byte[] first = crypto.DeriveKey(key, "salt"u8, "info"u8, 48);
        byte[] second = crypto.DeriveKey(key, "salt"u8, "info"u8, 48);

        Assert.Equal(48, first.Length);
        Assert.Equal(first, second);
    }

    [Fact]
    public void ComputeHmacSha256_Is32BytesAndDeterministic()
    {
        byte[] key = crypto.GenerateRandom(32);
        byte[] data = "message"u8.ToArray();

        byte[] first = crypto.ComputeHmacSha256(key, data);
        byte[] second = crypto.ComputeHmacSha256(key, data);

        Assert.Equal(32, first.Length);
        Assert.Equal(first, second);
    }

    [Fact]
    public void ComputeHmacSha256_DiffersForDifferentKeys()
    {
        byte[] data = "message"u8.ToArray();
        byte[] a = crypto.ComputeHmacSha256(crypto.GenerateRandom(32), data);
        byte[] b = crypto.ComputeHmacSha256(crypto.GenerateRandom(32), data);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Seal_ThenOpen_RoundTrips()
    {
        byte[] key = crypto.GenerateRandom(32);
        byte[] nonce = new byte[12];
        byte[] associatedData = "aad"u8.ToArray();
        byte[] plaintext = "the quick brown fox"u8.ToArray();

        byte[] sealedValue = crypto.Seal(key, nonce, associatedData, plaintext);
        byte[]? opened = crypto.Open(key, nonce, associatedData, sealedValue);

        Assert.NotNull(opened);
        Assert.Equal(plaintext, opened);
        Assert.Equal(plaintext.Length + 16, sealedValue.Length);
    }

    [Fact]
    public void Open_FailsWithWrongKey()
    {
        byte[] key = crypto.GenerateRandom(32);
        byte[] wrongKey = crypto.GenerateRandom(32);
        byte[] nonce = new byte[12];
        byte[] sealedValue = crypto.Seal(key, nonce, [], "data"u8.ToArray());

        Assert.Null(crypto.Open(wrongKey, nonce, [], sealedValue));
    }

    [Fact]
    public void Open_FailsWithTamperedCiphertext()
    {
        byte[] key = crypto.GenerateRandom(32);
        byte[] nonce = new byte[12];
        byte[] sealedValue = crypto.Seal(key, nonce, [], "data"u8.ToArray());
        sealedValue[0] ^= 0xFF;

        Assert.Null(crypto.Open(key, nonce, [], sealedValue));
    }

    [Fact]
    public void Open_FailsWithTooShortInput()
    {
        byte[] key = crypto.GenerateRandom(32);
        Assert.Null(crypto.Open(key, new byte[12], [], new byte[8]));
    }

    [Fact]
    public void ComputeCrc32C_MatchesKnownTestVector()
    {
        uint crc = crypto.ComputeCrc32C("123456789"u8);
        Assert.Equal(0xE3069283u, crc);
    }

    [Fact]
    public void ComputeCrc32C_DiffersWhenDataChanges()
    {
        uint a = crypto.ComputeCrc32C("hello"u8);
        uint b = crypto.ComputeCrc32C("hellO"u8);
        Assert.NotEqual(a, b);
    }
}
