namespace Markwardt.NetChannel.Internal;

/// <summary>
/// Encodes and decodes an individual model property value, controller method argument, or method result to
/// and from the opaque <c>bytes</c> the wire schema carries it as (Docs/Wire.md — "out of scope for this
/// document"), implemented by <see cref="NetValueCodec"/>.
/// </summary>
internal interface INetValueCodec
{
    /// <summary>
    /// Encodes a value of the given declared type.
    /// </summary>
    /// <param name="type">The value's declared CLR type.</param>
    /// <param name="value">The value to encode.</param>
    /// <returns>The encoded bytes.</returns>
    ReadOnlyMemory<byte> Encode(Type type, object? value);

    /// <summary>
    /// Decodes a value of the given declared type.
    /// </summary>
    /// <param name="type">The value's declared CLR type.</param>
    /// <param name="encoded">The encoded bytes, as produced by <see cref="Encode"/>.</param>
    /// <returns>The decoded value.</returns>
    object? Decode(Type type, ReadOnlySpan<byte> encoded);

    /// <summary>
    /// Registers the serializer used to encode and decode every value of type <typeparamref name="T"/> from now on.
    /// </summary>
    /// <typeparam name="T">The type to register the serializer for.</typeparam>
    /// <param name="serializer">The serializer to use for <typeparamref name="T"/>.</param>
    void SetSerializer<T>(INetSerializer serializer);
}

/// <inheritdoc cref="INetValueCodec" />
internal sealed class NetValueCodec : INetValueCodec
{
    private readonly ConcurrentDictionary<Type, INetSerializer> serializers = new();

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Encode(Type type, object? value)
    {
        using IMemoryOwner<byte> owner = GetSerializer(type).Serialize(value);
        return owner.Memory.ToArray();
    }

    /// <inheritdoc />
    public object? Decode(Type type, ReadOnlySpan<byte> encoded) => GetSerializer(type).Deserialize(encoded.ToArray());

    /// <inheritdoc />
    public void SetSerializer<T>(INetSerializer serializer) => serializers[typeof(T)] = serializer;

    private INetSerializer GetSerializer(Type type) => serializers.GetOrAdd(type, static t => new NetProtobufSerializer(t));
}
