namespace Markwardt.NetChannel;

/// <summary>
/// Non-generic base for a local proxy of a remote net entity, implemented by
/// <see cref="INetRemoteEntity{TModel, TController}"/>.
/// </summary>
public interface INetRemoteEntity
{
    /// <summary>
    /// Triggered when the remote entity is no longer visible to this peer, after which this instance is no longer valid.
    /// </summary>
    IObservable<INetRemoteEntity> Destroyed { get; }
}

/// <summary>
/// A local proxy for a remote net entity visible to this peer, e.g. as obtained via <see cref="INetManager.Listen{TModel, TController}"/>.
/// </summary>
/// <typeparam name="TModel">The remote entity's model interface.</typeparam>
/// <typeparam name="TController">The remote entity's controller interface.</typeparam>
public interface INetRemoteEntity<TModel, TController> : INetRemoteEntity
    where TModel : class
    where TController : class
{
    /// <summary>
    /// The remote entity's model. Properties are cached locally, so getting one is instant; setting one
    /// updates the local cache immediately and sends the new value to the entity's owner to be applied there,
    /// which then propagates it to every other connection the entity is visible to. A failure (e.g. the
    /// property setter has no <see cref="NetAccessAttribute"/> granting access, this peer lacks a required
    /// <see cref="NetRoleAttribute"/>, or the entity becomes no longer visible before the owner answers) is
    /// reported through <see cref="INetManager.Failed"/>.
    /// </summary>
    TModel Model { get; }

    /// <summary>
    /// The remote entity's controller. Calling a method packages its arguments and sends them to the
    /// entity's owner to execute: a method returning <see cref="Task"/> or <see cref="Task{TResult}"/> is
    /// sent as a request whose task completes with the owner's result once it responds, or faults with a
    /// <see cref="NetFailedException"/> if the owner declines the call, its own controller method throws
    /// while executing it, or the entity becomes no longer visible before it responds (e.g. the owner's
    /// connection is lost) — at the same moment <see cref="INetRemoteEntity.Destroyed"/> fires for that
    /// reason — while a <see langword="void"/> method is sent fire-and-forget. Either way, a failure is also
    /// reported through <see cref="INetManager.Failed"/>.
    /// </summary>
    TController Controller { get; }
}
