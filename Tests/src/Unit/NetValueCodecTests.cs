namespace Markwardt.NetChannel.Tests;

public sealed class NetValueCodecTests
{
    private readonly NetValueCodec codec = new();

    [Fact]
    public void Int_RoundTrips()
    {
        ReadOnlyMemory<byte> encoded = codec.Encode(typeof(int), 42);
        Assert.Equal(42, codec.Decode(typeof(int), encoded.Span));
    }

    [Fact]
    public void NegativeInt_RoundTrips()
    {
        ReadOnlyMemory<byte> encoded = codec.Encode(typeof(int), -1234);
        Assert.Equal(-1234, codec.Decode(typeof(int), encoded.Span));
    }

    [Fact]
    public void String_RoundTrips()
    {
        ReadOnlyMemory<byte> encoded = codec.Encode(typeof(string), "hello world");
        Assert.Equal("hello world", codec.Decode(typeof(string), encoded.Span));
    }

    [Fact]
    public void NullString_RoundTrips()
    {
        ReadOnlyMemory<byte> encoded = codec.Encode(typeof(string), null);
        Assert.Null(codec.Decode(typeof(string), encoded.Span));
    }

    [Fact]
    public void Bool_RoundTrips()
    {
        ReadOnlyMemory<byte> encoded = codec.Encode(typeof(bool), true);
        Assert.Equal(true, codec.Decode(typeof(bool), encoded.Span));
    }

    [Fact]
    public void Double_RoundTrips()
    {
        ReadOnlyMemory<byte> encoded = codec.Encode(typeof(double), 3.14159);
        Assert.Equal(3.14159, codec.Decode(typeof(double), encoded.Span));
    }

    [Fact]
    public void NullableInt_WithValue_RoundTrips()
    {
        ReadOnlyMemory<byte> encoded = codec.Encode(typeof(int?), 7);
        Assert.Equal(7, codec.Decode(typeof(int?), encoded.Span));
    }

    [Fact]
    public void NullableInt_WithNull_RoundTrips()
    {
        ReadOnlyMemory<byte> encoded = codec.Encode(typeof(int?), null);
        Assert.Null(codec.Decode(typeof(int?), encoded.Span));
    }

    [Fact]
    public void List_RoundTrips()
    {
        List<int> value = [1, 2, 3];
        ReadOnlyMemory<byte> encoded = codec.Encode(typeof(List<int>), value);
        List<int>? decoded = (List<int>?)codec.Decode(typeof(List<int>), encoded.Span);
        Assert.Equal(value, decoded);
    }

    [Fact]
    public void Vector3_RoundTrips()
    {
        Vector3 value = new(1, 2, 3);
        ReadOnlyMemory<byte> encoded = codec.Encode(typeof(Vector3), value);
        Assert.Equal(value, codec.Decode(typeof(Vector3), encoded.Span));
    }

    [Fact]
    public void NetPosition_RoundTrips()
    {
        NetPosition value = new(new Vector3(1, 2, 3), new NetRange(1, 2));
        ReadOnlyMemory<byte> encoded = codec.Encode(typeof(NetPosition), value);
        Assert.Equal(value, codec.Decode(typeof(NetPosition), encoded.Span));
    }

    [Fact]
    public void SetSerializer_OverridesDefaultProtobufSerializationForThatType()
    {
        UpperCaseSerializer serializer = new();
        codec.SetSerializer<string>(serializer);

        ReadOnlyMemory<byte> encoded = codec.Encode(typeof(string), "hello");

        Assert.Equal(1, serializer.SerializeCalls);
        Assert.Equal("HELLO", codec.Decode(typeof(string), encoded.Span));
        Assert.Equal(1, serializer.DeserializeCalls);
    }

    [Fact]
    public void SetSerializer_DoesNotAffectOtherTypes()
    {
        codec.SetSerializer<string>(new UpperCaseSerializer());

        ReadOnlyMemory<byte> encoded = codec.Encode(typeof(int), 5);
        Assert.Equal(5, codec.Decode(typeof(int), encoded.Span));
    }

    [Fact]
    public void EmptyList_RoundTripsAsEmptyRatherThanNull()
    {
        List<int> value = [];
        ReadOnlyMemory<byte> encoded = codec.Encode(typeof(List<int>), value);
        List<int>? decoded = (List<int>?)codec.Decode(typeof(List<int>), encoded.Span);

        Assert.NotNull(decoded);
        Assert.Empty(decoded);
    }

    [Fact]
    public void NullList_RoundTripsAsNull()
    {
        ReadOnlyMemory<byte> encoded = codec.Encode(typeof(List<int>), null);
        Assert.Null(codec.Decode(typeof(List<int>), encoded.Span));
    }

    [Fact]
    public void EmptyString_RoundTripsAsEmptyRatherThanNull()
    {
        ReadOnlyMemory<byte> encoded = codec.Encode(typeof(string), string.Empty);
        Assert.Equal(string.Empty, codec.Decode(typeof(string), encoded.Span));
    }

    [Fact]
    public void DefaultValuedRecord_RoundTripsAsAnInstanceRatherThanNull()
    {
        NetRange value = new(0, 0);
        ReadOnlyMemory<byte> encoded = codec.Encode(typeof(NetRange), value);

        Assert.Equal(value, codec.Decode(typeof(NetRange), encoded.Span));
    }

    [Fact]
    public void ZeroInt_RoundTrips()
    {
        ReadOnlyMemory<byte> encoded = codec.Encode(typeof(int), 0);
        Assert.Equal(0, codec.Decode(typeof(int), encoded.Span));
    }

    [Fact]
    public void NullableIntZero_RoundTripsAsZeroRatherThanNull()
    {
        ReadOnlyMemory<byte> encoded = codec.Encode(typeof(int?), 0);
        Assert.Equal(0, codec.Decode(typeof(int?), encoded.Span));
    }

    private sealed class UpperCaseSerializer : INetSerializer
    {
        public int SerializeCalls { get; private set; }

        public int DeserializeCalls { get; private set; }

        public IMemoryOwner<byte> Serialize(object? value)
        {
            SerializeCalls++;
            int byteCount = Encoding.UTF8.GetByteCount((string)value!);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(byteCount);
            int written = Encoding.UTF8.GetBytes((string)value!, buffer);
            return new NetPooledMemory(buffer, written);
        }

        public object? Deserialize(ReadOnlyMemory<byte> data)
        {
            DeserializeCalls++;
            return Encoding.UTF8.GetString(data.Span).ToUpperInvariant();
        }
    }
}
