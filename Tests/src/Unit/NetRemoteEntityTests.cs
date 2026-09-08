namespace Markwardt.NetChannel.Tests;

public sealed class NetRemoteEntityTests
{
    [Fact]
    public void Properties_MatchesUnderlyingModelType()
    {
        (NetRemoteState<ITestModel> state, ServiceProvider provider, NetManager manager) = CreateState();
        using ServiceProvider disposeProvider = provider;
        using NetManager disposeManager = manager;

        Assert.Equal(3, state.Properties.Count);
        Assert.Contains("Value", state.Properties);
        Assert.Contains("OpenValue", state.Properties);
        Assert.Contains("AdminValue", state.Properties);
    }

    [Fact]
    public void Observe_ByName_FiresWhenValueIsAppliedRemotely()
    {
        (NetRemoteState<ITestModel> state, ServiceProvider provider, NetManager manager) = CreateState();
        using ServiceProvider disposeProvider = provider;
        using NetManager disposeManager = manager;

        List<object?> values = [];
        using IDisposable subscription = state.Observe("Value").Subscribe(values.Add);

        state.ApplyRemote("Value", 5);

        Assert.Equal([5], values);
    }

    [Fact]
    public void Observe_Generic_FiresWhenValueIsAppliedRemotely()
    {
        (NetRemoteState<ITestModel> state, ServiceProvider provider, NetManager manager) = CreateState();
        using ServiceProvider disposeProvider = provider;
        using NetManager disposeManager = manager;

        List<int> values = [];
        using IDisposable subscription = state.Observe(x => x.Value).Subscribe(values.Add);

        state.ApplyRemote("Value", 9);

        Assert.Equal([9], values);
    }

    [Fact]
    public void ApplyRemote_DoesNotSendAnythingBackToTheOwner()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        using ServiceProvider disposeProvider = provider;
        using NetManager disposeManager = manager;

        NetConnection connection = new(manager, NetDirection.Outgoing, "127.0.0.1", 1234, null);
        NetModelType modelType = new NetEntityTypeCache().GetModel(typeof(ITestModel));
        NetRemoteState<ITestModel> state = new(modelType, manager, connection, entityId: 1);

        state.ApplyRemote("Value", 3);

        Assert.Empty(connection.PendingReplies);
        Assert.Equal(3, state.Get("Value"));
    }

    [Fact]
    public void Set_WithNoMatchingRemoteEntityBinding_IsSafeNoOp()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        using ServiceProvider disposeProvider = provider;
        using NetManager disposeManager = manager;

        NetConnection connection = new(manager, NetDirection.Outgoing, "127.0.0.1", 1234, null);
        NetModelType modelType = new NetEntityTypeCache().GetModel(typeof(ITestModel));
        NetRemoteState<ITestModel> state = new(modelType, manager, connection, entityId: 1);

        state.Model.Value = 42;

        Assert.Equal(42, state.Get("Value"));
        Assert.Empty(connection.PendingReplies);
    }

    [Fact]
    public void ApplyProperty_WithUnknownPropertyId_IsIgnoredRatherThanThrowing()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        using ServiceProvider disposeProvider = provider;
        using NetManager disposeManager = manager;

        NetRemoteEntityBinding binding = CreateBinding(manager);

        binding.ApplyProperty(999, ReadOnlyMemory<byte>.Empty);
    }

    [Fact]
    public void ApplyProperty_WithKnownPropertyId_AppliesTheValue()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        using ServiceProvider disposeProvider = provider;
        using NetManager disposeManager = manager;

        NetRemoteEntityBinding binding = CreateBinding(manager);
        NetModelType modelType = manager.TypeCache.GetModel(typeof(ITestModel));
        uint valueId = modelType.Properties.Single(property => property.Name == nameof(ITestModel.Value)).Id;

        binding.ApplyProperty(valueId, manager.ValueCodec.Encode(typeof(int), 77));

        INetRemoteEntity<ITestModel, ITestController> entity = (INetRemoteEntity<ITestModel, ITestController>)binding.RemoteEntity;
        Assert.Equal(77, entity.Model.Value);
    }

    private static NetRemoteEntityBinding CreateBinding(NetManager manager)
    {
        NetModelType modelType = manager.TypeCache.GetModel(typeof(ITestModel));
        NetControllerType controllerType = manager.TypeCache.GetController(typeof(ITestController));
        NetListenerBinding<ITestModel, ITestController> listenerBinding = new(modelType, controllerType);
        NetConnection connection = new(manager, NetDirection.Outgoing, "127.0.0.1", 1234, null);
        return listenerBinding.CreateEntity(manager, connection, entityId: 1, modelId: 0, controllerId: 0);
    }

    private static (NetRemoteState<ITestModel> State, ServiceProvider Provider, NetManager Manager) CreateState()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        NetConnection connection = new(manager, NetDirection.Outgoing, "127.0.0.1", 1234, null);
        NetModelType modelType = new NetEntityTypeCache().GetModel(typeof(ITestModel));
        return (new NetRemoteState<ITestModel>(modelType, manager, connection, entityId: 1), provider, manager);
    }

    private static (ServiceProvider Provider, NetManager Manager) CreateManager()
    {
        (ServiceProvider provider, INetManager manager) = TestHarness.CreateManager();
        return (provider, (NetManager)manager);
    }
}
