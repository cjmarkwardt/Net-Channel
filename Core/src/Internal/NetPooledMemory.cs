namespace Markwardt.NetChannel.Internal;

/// <summary>
/// Wraps a buffer rented from <see cref="ArrayPool{T}.Shared"/>, exposing only its valid prefix and returning
/// it to the pool on disposal.
/// </summary>
/// <param name="array">The rented buffer.</param>
/// <param name="length">The number of valid bytes at the start of <paramref name="array"/>.</param>
internal sealed class NetPooledMemory(byte[] array, int length) : IMemoryOwner<byte>
{
    /// <inheritdoc />
    public Memory<byte> Memory { get; } = array.AsMemory(0, length);

    /// <inheritdoc />
    public void Dispose() => ArrayPool<byte>.Shared.Return(array);
}
