namespace Markwardt.NetChannel.Internal;

/// <summary>
/// Backs an <see cref="INetRemoteEntity{TModel, TController}.Model"/>: storage and change notification exactly
/// like <see cref="NetState{TModel}"/>, except that setting a property also sends the new value to the
/// entity's owner.
/// </summary>
/// <typeparam name="TModel">The remote entity's model interface.</typeparam>
internal sealed class NetRemoteState<TModel> : INetState<TModel>
    where TModel : class
{
    private readonly NetState<TModel> inner;

    private readonly NetManager manager;

    private readonly NetConnection connection;

    private readonly ulong entityId;

    /// <summary>
    /// Initializes a new instance backed by the given validated model shape, sending a property set through
    /// the given manager/connection/entity whenever <see cref="Model"/> is used to set a property.
    /// </summary>
    /// <param name="modelType">The validated shape of <typeparamref name="TModel"/>.</param>
    /// <param name="manager">The manager to send a property set through.</param>
    /// <param name="connection">The connection to the entity's owner.</param>
    /// <param name="entityId">The id this connection uses to refer to the entity.</param>
    public NetRemoteState(NetModelType modelType, NetManager manager, NetConnection connection, ulong entityId)
    {
        inner = new NetState<TModel>(modelType);
        this.manager = manager;
        this.connection = connection;
        this.entityId = entityId;
        Model = NetModelProxy<TModel>.Create(this);
    }

    /// <inheritdoc />
    public IReadOnlySet<string> Properties => inner.Properties;

    /// <inheritdoc />
    public TModel Model { get; }

    /// <inheritdoc />
    public object? Get(string property) => inner.Get(property);

    /// <inheritdoc />
    public void Set(string property, object? value)
    {
        inner.Set(property, value);
        NetModelMember member = inner.ModelType.Properties.First(candidate => candidate.Name == property);
        manager.RequestPropertySet(connection, entityId, member, value);
    }

    /// <summary>
    /// Applies a value received from the entity's owner, without sending anything back.
    /// </summary>
    /// <param name="property">The property's name.</param>
    /// <param name="value">The value to apply.</param>
    public void ApplyRemote(string property, object? value) => inner.Set(property, value);

    /// <inheritdoc />
    public IObservable<object?> Observe(string property) => inner.Observe(property);

    /// <inheritdoc />
    public IObservable<T> Observe<T>(Expression<Func<TModel, T>> selector) => inner.Observe(selector);
}

/// <inheritdoc cref="INetRemoteEntity{TModel, TController}" />
internal sealed class NetRemoteEntity<TModel, TController> : INetRemoteEntity<TModel, TController>
    where TModel : class
    where TController : class
{
    private readonly ReplaySubject<INetRemoteEntity> destroyed = new(1);

    private readonly NetManager manager;

    private readonly NetConnection connection;

    private readonly ulong entityId;

    private readonly uint controllerId;

    private readonly NetControllerType controllerType;

    /// <summary>
    /// Initializes a new instance for an entity newly visible on the given connection.
    /// </summary>
    /// <param name="manager">The owning manager.</param>
    /// <param name="connection">The connection to the entity's owner.</param>
    /// <param name="entityId">The id this connection uses to refer to the entity.</param>
    /// <param name="controllerId">The id this connection uses to refer to the entity's controller interface type.</param>
    /// <param name="controllerType">The validated shape of <typeparamref name="TController"/>.</param>
    /// <param name="state">The entity's model state.</param>
    public NetRemoteEntity(NetManager manager, NetConnection connection, ulong entityId, uint controllerId, NetControllerType controllerType, NetRemoteState<TModel> state)
    {
        this.manager = manager;
        this.connection = connection;
        this.entityId = entityId;
        this.controllerId = controllerId;
        this.controllerType = controllerType;
        State = state;
        Controller = NetControllerProxy<TController>.Create(Invoke);
    }

    /// <inheritdoc />
    public IObservable<INetRemoteEntity> Destroyed => destroyed;

    /// <inheritdoc />
    public TModel Model => State.Model;

    /// <inheritdoc />
    public TController Controller { get; }

    /// <summary>
    /// The entity's model state.
    /// </summary>
    public NetRemoteState<TModel> State { get; }

    /// <summary>
    /// Marks the entity as no longer visible, triggering <see cref="Destroyed"/>. Only the first call has any effect.
    /// </summary>
    public void NotifyDestroyed()
    {
        destroyed.OnNext(this);
        destroyed.OnCompleted();
    }

    private object? Invoke(MethodInfo method, object?[] args)
    {
        NetControllerMember member = controllerType.Methods.First(candidate => candidate.Method == method);
        ulong operation = manager.AllocateCallOperation(connection, entityId, controllerId, member, this);
        ParameterInfo[] parameters = method.GetParameters();
        ReadOnlyMemory<byte>[] arguments = new ReadOnlyMemory<byte>[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            ReadOnlyMemory<byte> encoded = manager.ValueCodec.Encode(parameters[i].ParameterType, args[i]);
            arguments[i] = member.IsSecure
                ? manager.PayloadCodec.Encrypt(connection.PayloadKey.Span, connection.ConnectionId!.Value, entityId, member.Id, operation, i, encoded.Span)
                : encoded;
        }

        if (member.IsTask)
        {
            TaskCompletionSource<Reply> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.SendCall(connection, entityId, member, operation, arguments, completion);
            return member.ResultType is { } resultType
                ? GetType().GetMethod(nameof(AwaitResult), BindingFlags.NonPublic | BindingFlags.Instance)!.MakeGenericMethod(resultType).Invoke(this, [completion.Task, member, operation])
                : AwaitVoid(completion.Task, member, operation);
        }

        manager.SendCall(connection, entityId, member, operation, arguments, null);
        return null;
    }

    private async Task<T> AwaitResult<T>(Task<Reply> replyTask, NetControllerMember member, ulong operation)
    {
        Reply reply = await replyTask.ConfigureAwait(false);

        if (reply.OutcomeCase == Reply.OutcomeOneofCase.Failure)
        {
            throw new NetFailedException(reply.Failure);
        }

        byte[] resultBytes = reply.Result.ToByteArray();

        if (member.IsSecure)
        {
            resultBytes = manager.PayloadCodec.Decrypt(connection.PayloadKey.Span, connection.ConnectionId!.Value, entityId, member.Id, operation, null, resultBytes)
                ?? throw new NetFailedException("Result failed to decrypt.");
        }

        return (T)manager.ValueCodec.Decode(typeof(T), resultBytes)!;
    }

    private async Task AwaitVoid(Task<Reply> replyTask, NetControllerMember member, ulong operation)
    {
        Reply reply = await replyTask.ConfigureAwait(false);

        if (reply.OutcomeCase == Reply.OutcomeOneofCase.Failure)
        {
            throw new NetFailedException(reply.Failure);
        }
    }
}
