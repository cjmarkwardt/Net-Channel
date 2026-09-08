namespace Markwardt.NetChannel;

/// <summary>
/// Serializes and deserializes a single model property value, controller method argument, or method result to
/// and from the wire's opaque byte representation for one specific CLR type, registered via
/// <see cref="INetManager.SetSerializer{T}"/>. A type with no serializer registered for it defaults to
/// automatic Protocol Buffers serialization instead.
/// </summary>
public interface INetSerializer
{
    /// <summary>
    /// Serializes a value into a pooled buffer.
    /// </summary>
    /// <param name="value">The value to serialize, or <see langword="null"/>.</param>
    /// <returns>
    /// A buffer rented from a shared pool, holding exactly the serialized bytes — the caller disposes it once
    /// done, returning it to the pool.
    /// </returns>
    IMemoryOwner<byte> Serialize(object? value);

    /// <summary>
    /// Deserializes a value previously produced by <see cref="Serialize"/>.
    /// </summary>
    /// <param name="data">The serialized bytes.</param>
    /// <returns>The deserialized value, or <see langword="null"/>.</returns>
    object? Deserialize(ReadOnlyMemory<byte> data);
}
