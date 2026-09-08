namespace Markwardt.NetChannel.Internal;

/// <inheritdoc cref="INetSerializer" />
/// <summary>
/// The default <see cref="INetSerializer"/> for a type with none explicitly registered via
/// <see cref="INetManager.SetSerializer{T}"/>: automatic Protocol Buffers serialization via protobuf-net's
/// reflection-based runtime model, requiring no attributes or generated code for the type it serializes.
/// </summary>
/// <param name="type">The type this instance serializes and deserializes.</param>
internal sealed class NetProtobufSerializer(Type type) : INetSerializer
{
    private static readonly Lock ModelLock = new();

    // A value whose Protocol Buffers encoding is genuinely empty (an empty collection, or a message whose every
    // field holds its default) would otherwise be indistinguishable from null, which is also encoded as no
    // bytes at all. This single byte stands in for that empty encoding so the two stay distinguishable. It can
    // never be confused for a real encoding: 0x00 is field number 0, which Protocol Buffers does not allow, so
    // no non-empty encoding ever starts with it.
    private static readonly byte[] EmptyEncoding = [0x00];

    static NetProtobufSerializer()
    {
        lock (ModelLock)
        {
            if (!RuntimeTypeModel.Default.IsDefined(typeof(Vector3)))
            {
                RuntimeTypeModel.Default.Add(typeof(Vector3), false).Add("X", "Y", "Z");
            }
        }
    }

    /// <inheritdoc />
    public IMemoryOwner<byte> Serialize(object? value)
    {
        if (value is null)
        {
            return new NetPooledMemory(ArrayPool<byte>.Shared.Rent(0), 0);
        }

        using MemoryStream stream = new();

        lock (ModelLock)
        {
            RuntimeTypeModel.Default.Serialize(stream, value);
        }

        if (stream.Length == 0)
        {
            byte[] empty = ArrayPool<byte>.Shared.Rent(EmptyEncoding.Length);
            EmptyEncoding.CopyTo(empty, 0);
            return new NetPooledMemory(empty, EmptyEncoding.Length);
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent((int)stream.Length);
        stream.Position = 0;
        int written = stream.Read(buffer, 0, (int)stream.Length);
        return new NetPooledMemory(buffer, written);
    }

    /// <inheritdoc />
    public object? Deserialize(ReadOnlyMemory<byte> data)
    {
        if (data.IsEmpty)
        {
            return type.IsValueType && Nullable.GetUnderlyingType(type) is null ? Activator.CreateInstance(type) : null;
        }

        using MemoryStream stream = new(data.Span.SequenceEqual(EmptyEncoding) ? [] : data.ToArray());

        lock (ModelLock)
        {
            return RuntimeTypeModel.Default.Deserialize(stream, null, type);
        }
    }
}
