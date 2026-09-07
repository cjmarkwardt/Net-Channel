namespace Markwardt.NetChannel;

/// <summary>
/// Thrown, or delivered via <see cref="INetManager.Failed"/>, when a model property set or controller method
/// call made through an <see cref="INetRemoteEntity{TModel, TController}"/> fails — because the entity's
/// owner declined to honor it, because its controller method threw while executing it, or because the entity
/// became no longer visible (e.g. its owner's connection was lost) before an answer arrived. In the second
/// case, <see cref="Exception.Message"/> is a generic failure message rather than the thrown exception's own
/// message, since that detail is not sent over the wire.
/// </summary>
/// <param name="message">A human-readable message describing the failure.</param>
public sealed class NetFailedException(string message) : Exception(message);
