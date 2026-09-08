namespace Markwardt.NetChannel.Tests;

public sealed class NetManagerTests
{
    [Fact]
    public void Dispose_Synchronous_DoesNotThrow()
    {
        (ServiceProvider provider, INetManager manager) = TestHarness.CreateManager();
        using ServiceProvider disposeProvider = provider;

        manager.Host(0);
        manager.Dispose();
        manager.Dispose();
    }

    [Fact]
    public void Host_CalledTwice_RebindsToNewPort()
    {
        (ServiceProvider provider, INetManager manager) = TestHarness.CreateManager();
        using ServiceProvider disposeProvider = provider;
        using INetManager disposeManager = manager;

        manager.Host(0);
        int firstPort = manager.HostPort!.Value;

        manager.Host(0);
        int secondPort = manager.HostPort!.Value;

        Assert.NotEqual(firstPort, secondPort);
    }

    [Fact]
    public void HostPort_IsNullUntilHosted()
    {
        (ServiceProvider provider, INetManager manager) = TestHarness.CreateManager();
        using ServiceProvider disposeProvider = provider;
        using INetManager disposeManager = manager;

        Assert.Null(manager.HostPort);
    }

    [Fact]
    public void GetConnection_WithUnknownTag_ReturnsNull()
    {
        (ServiceProvider provider, INetManager manager) = TestHarness.CreateManager();
        using ServiceProvider disposeProvider = provider;
        using INetManager disposeManager = manager;

        Assert.Null(manager.GetConnection("does-not-exist"));
    }

    [Fact]
    public void GetView_WithUnknownTag_ReturnsNull()
    {
        (ServiceProvider provider, INetManager manager) = TestHarness.CreateManager();
        using ServiceProvider disposeProvider = provider;
        using INetManager disposeManager = manager;

        Assert.Null(manager.GetView("does-not-exist"));
    }

    [Fact]
    public void Wait_OnAlreadyDisconnectedConnection_FaultsImmediately()
    {
        (ServiceProvider provider, INetManager manager) = TestHarness.CreateManager();
        using ServiceProvider disposeProvider = provider;
        using INetManager disposeManager = manager;

        INetConnection connection = manager.Connect("127.0.0.1", TestHarness.GetFreeUdpPort());
        connection.Drop();

        Task waitTask = connection.Wait();
        Assert.True(waitTask.IsFaulted);
    }

    [Fact]
    public void SetSerializer_IsUsedByValueCodec()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        using ServiceProvider disposeProvider = provider;
        using NetManager disposeManager = manager;

        RecordingSerializer serializer = new();
        manager.SetSerializer<string>(serializer);

        manager.ValueCodec.Encode(typeof(string), "hi");

        Assert.Equal(1, serializer.SerializeCalls);
    }

    [Fact]
    public void DropConnection_CalledTwice_SecondCallIsNoOp()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        using ServiceProvider disposeProvider = provider;
        using NetManager disposeManager = manager;

        NetConnection connection = new(manager, NetDirection.Outgoing, "127.0.0.1", 1234, null);
        manager.DropConnection(connection, null);
        Assert.Equal(NetStatus.Disconnected, connection.Status);

        manager.DropConnection(connection, null);
        Assert.Equal(NetStatus.Disconnected, connection.Status);
    }

    [Fact]
    public void DropConnection_WithTaggedConnection_RemovesFromTagIndex()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        using ServiceProvider disposeProvider = provider;
        using NetManager disposeManager = manager;

        INetConnection connection = manager.Connect("127.0.0.1", TestHarness.GetFreeUdpPort(), tag: "tag1");
        Assert.Same(connection, manager.GetConnection("tag1"));

        connection.Drop();

        Assert.Null(manager.GetConnection("tag1"));
    }

    [Fact]
    public void DropConnection_WithExistingView_RemovesViewerSilently()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        using ServiceProvider disposeProvider = provider;
        using NetManager disposeManager = manager;

        NetConnection connection = new(manager, NetDirection.Outgoing, "127.0.0.1", 1234, null);
        NetView view = (NetView)manager.CreateView();
        view.AddViewers(connection);
        Assert.Contains(connection, view.Viewers);

        manager.DropConnection(connection, null);

        Assert.DoesNotContain(connection, view.Viewers);
    }

    [Fact]
    public void DropConnection_WithPendingReplies_FailsThemAll()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        using ServiceProvider disposeProvider = provider;
        using NetManager disposeManager = manager;

        NetConnection connection = new(manager, NetDirection.Outgoing, "127.0.0.1", 1234, null);
        bool failed = false;
        connection.PendingReplies[1] = (null, reply => failed = reply.OutcomeCase == Reply.OutcomeOneofCase.Failure);

        manager.DropConnection(connection, null);

        Assert.True(failed);
        Assert.Empty(connection.PendingReplies);
    }

    [Fact]
    public void DropConnection_WithRemoteEntities_NotifiesDestroyed()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        using ServiceProvider disposeProvider = provider;
        using NetManager disposeManager = manager;

        NetConnection connection = new(manager, NetDirection.Outgoing, "127.0.0.1", 1234, null);
        bool destroyed = false;

        NetRemoteEntityBinding binding = new()
        {
            EntityId = 1,
            ModelId = 0,
            ControllerId = 0,
            ModelType = new NetEntityTypeCache().GetModel(typeof(ITestModel)),
            ControllerType = new NetEntityTypeCache().GetController(typeof(ITestController)),
            RemoteEntity = Mock.Of<INetRemoteEntity>(),
            ApplyProperty = (_, _) => { },
            NotifyDestroyed = () => destroyed = true,
        };

        connection.RemoteEntities[1] = binding;

        manager.DropConnection(connection, null);

        Assert.True(destroyed);
        Assert.Empty(connection.RemoteEntities);
    }

    [Fact]
    public void SweepConnection_DropsConnectionOnDisconnectTimeout()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        using ServiceProvider disposeProvider = provider;
        using NetManager disposeManager = manager;

        NetConnection connection = (NetConnection)manager.Connect("127.0.0.1", TestHarness.GetFreeUdpPort());
        connection.SetStatus(NetStatus.Connected);
        connection.LastReceivedAt = DateTime.UtcNow - TimeSpan.FromHours(1);
        manager.DisconnectTimeout = TimeSpan.FromSeconds(1);

        manager.SweepConnection(connection);

        Assert.Equal(NetStatus.Disconnected, connection.Status);
    }

    [Fact]
    public void SweepConnection_ResendsPendingEntityCreate()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        using ServiceProvider disposeProvider = provider;
        using NetManager disposeManager = manager;

        NetConnection connection = (NetConnection)manager.Connect("127.0.0.1", TestHarness.GetFreeUdpPort());
        connection.SetStatus(NetStatus.Connected);
        connection.LastReceivedAt = DateTime.UtcNow;

        NetModelType modelType = new NetEntityTypeCache().GetModel(typeof(ITestModel));
        NetControllerType controllerType = new NetEntityTypeCache().GetController(typeof(ITestController));
        TestEntity entity = new(new NetState<ITestModel>(modelType));

        manager.RegisterEntityGroup(entity, modelType, controllerType, (NetGroupBase)manager.All);
        manager.ReevaluateConnection(connection);

        int countBeforeSweep = connection.PendingReplies.Count;
        Assert.True(countBeforeSweep > 0);

        manager.PingInterval = TimeSpan.Zero;
        manager.SweepConnection(connection);

        Assert.True(connection.PendingReplies.Count > countBeforeSweep);
    }

    [Fact]
    public void SweepConnection_ResendsUnacknowledgedPropertySet()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        using ServiceProvider disposeProvider = provider;
        using NetManager disposeManager = manager;

        NetConnection connection = (NetConnection)manager.Connect("127.0.0.1", TestHarness.GetFreeUdpPort());
        connection.SetStatus(NetStatus.Connected);
        connection.LastReceivedAt = DateTime.UtcNow;

        NetModelType modelType = new NetEntityTypeCache().GetModel(typeof(ITestModel));
        NetControllerType controllerType = new NetEntityTypeCache().GetController(typeof(ITestController));
        TestEntity entity = new(new NetState<ITestModel>(modelType));

        manager.RegisterEntityGroup(entity, modelType, controllerType, (NetGroupBase)manager.All);
        manager.ReevaluateConnection(connection);

        ulong entityId = connection.OutgoingEntityIds[entity];
        ulong createSequence = connection.PendingReplies.Keys.Single();
        connection.OutgoingLifecycles[entityId].Acknowledge(createSequence, out _);

        entity.State.Model.Value = 5;
        int countBeforeSweep = connection.PendingReplies.Count;

        manager.PingInterval = TimeSpan.Zero;
        manager.SweepConnection(connection);

        Assert.True(connection.PendingReplies.Count > countBeforeSweep);
    }

    [Fact]
    public void SweepConnection_ResendsPendingEntityDestroy()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        using ServiceProvider disposeProvider = provider;
        using NetManager disposeManager = manager;

        NetConnection connection = (NetConnection)manager.Connect("127.0.0.1", TestHarness.GetFreeUdpPort());
        connection.SetStatus(NetStatus.Connected);
        connection.LastReceivedAt = DateTime.UtcNow;

        NetModelType modelType = new NetEntityTypeCache().GetModel(typeof(ITestModel));
        NetControllerType controllerType = new NetEntityTypeCache().GetController(typeof(ITestController));
        TestEntity entity = new(new NetState<ITestModel>(modelType));

        manager.RegisterEntityGroup(entity, modelType, controllerType, (NetGroupBase)manager.All);
        manager.ReevaluateConnection(connection);

        ulong entityId = connection.OutgoingEntityIds[entity];
        ulong createSequence = connection.PendingReplies.Keys.Single();
        connection.OutgoingLifecycles[entityId].Acknowledge(createSequence, out _);

        manager.All.Remove(entity);
        int countBeforeSweep = connection.PendingReplies.Count;

        manager.PingInterval = TimeSpan.Zero;
        manager.SweepConnection(connection);

        Assert.True(connection.PendingReplies.Count > countBeforeSweep);
    }

    [Fact]
    public void SweepConnection_ResendsUnacknowledgedMethodCall()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        using ServiceProvider disposeProvider = provider;
        using NetManager disposeManager = manager;

        NetConnection connection = (NetConnection)manager.Connect("127.0.0.1", TestHarness.GetFreeUdpPort());
        connection.SetStatus(NetStatus.Connected);
        connection.LastReceivedAt = DateTime.UtcNow;

        NetControllerType controllerType = new NetEntityTypeCache().GetController(typeof(ITestController));
        NetControllerMember member = controllerType.Methods.First(candidate => candidate.Name == nameof(ITestController.Ping));

        ulong operation = manager.AllocateCallOperation(connection, entityId: 1, controllerId: 0, member, Mock.Of<INetRemoteEntity>());
        manager.SendCall(connection, entityId: 1, member, operation, arguments: [], completion: null);

        int countBeforeSweep = connection.PendingReplies.Count;
        Assert.True(countBeforeSweep > 0);

        manager.PingInterval = TimeSpan.Zero;
        manager.SweepConnection(connection);

        Assert.True(connection.PendingReplies.Count > countBeforeSweep);
    }

    [Fact]
    public void RemoveFromLastGroup_StopsTrackingTheEntityEntirely()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        using ServiceProvider disposeProvider = provider;
        using NetManager disposeManager = manager;

        INetStateFactory factory = provider.GetRequiredService<INetStateFactory>();
        TestEntity entity = new(factory.Create<ITestModel>());

        manager.All.Add<ITestModel, ITestController>(entity);
        Assert.Contains(entity, manager.TrackedEntities);

        manager.All.Remove(entity);

        Assert.DoesNotContain(entity, manager.TrackedEntities);
    }

    [Fact]
    public void RemoveFromOneOfTwoGroups_KeepsTrackingTheEntity()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        using ServiceProvider disposeProvider = provider;
        using NetManager disposeManager = manager;

        INetStateFactory factory = provider.GetRequiredService<INetStateFactory>();
        TestEntity entity = new(factory.Create<ITestModel>());
        INetView view = manager.CreateView();

        manager.All.Add<ITestModel, ITestController>(entity);
        view.Add<ITestModel, ITestController>(entity);
        manager.All.Remove(entity);

        Assert.Contains(entity, manager.TrackedEntities);
    }

    [Fact]
    public void ReAddingAPreviouslyRemovedEntity_ResubscribesToItsProperties()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        using ServiceProvider disposeProvider = provider;
        using NetManager disposeManager = manager;

        INetStateFactory factory = provider.GetRequiredService<INetStateFactory>();
        TestEntity entity = new(factory.Create<ITestModel>());

        manager.All.Add<ITestModel, ITestController>(entity);
        manager.All.Remove(entity);
        manager.All.Add<ITestModel, ITestController>(entity);

        entity.State.Model.Value = 5;

        Assert.Contains(entity, manager.TrackedEntities);
        Assert.Equal(5, entity.State.Model.Value);
    }

    [Fact]
    public void DropConnection_ConcurrentCalls_ReportTheDropExactlyOnce()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        using ServiceProvider disposeProvider = provider;
        using NetManager disposeManager = manager;

        NetConnection connection = new(manager, NetDirection.Outgoing, "127.0.0.1", 1, null);
        connection.SetStatus(NetStatus.Connected);

        List<NetConnectionFailure> dropped = [];
        List<NetStatusChange> changes = [];
        using IDisposable droppedSubscription = manager.Dropped.Subscribe(dropped.Add);
        using IDisposable changeSubscription = manager.StatusChanged.Subscribe(changes.Add);

        Parallel.For(0, 16, _ => manager.DropConnection(connection, new NetFailedException("lost")));

        Assert.Single(dropped);
        Assert.Single(changes);
    }

    [Fact]
    public void ViewerPropertySet_Declined_StillReportsFailureAfterTheEntityStoppedBeingVisible()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        using ServiceProvider disposeProvider = provider;
        using NetManager disposeManager = manager;

        NetConnection connection = (NetConnection)manager.Connect("127.0.0.1", TestHarness.GetFreeUdpPort());
        connection.SetStatus(NetStatus.Connected);

        NetModelType modelType = manager.TypeCache.GetModel(typeof(ITestModel));
        NetControllerType controllerType = manager.TypeCache.GetController(typeof(ITestController));
        NetListenerBinding<ITestModel, ITestController> listenerBinding = new(modelType, controllerType);
        NetRemoteEntityBinding binding = listenerBinding.CreateEntity(manager, connection, entityId: 1, modelId: 0, controllerId: 0);
        connection.RemoteEntities[1] = binding;

        NetModelMember member = modelType.Properties.Single(property => property.Name == nameof(ITestModel.OpenValue));
        manager.RequestPropertySet(connection, 1, member, 5);

        List<NetRemoteEntityFailure> failures = [];
        using IDisposable subscription = manager.Failed.Subscribe(failures.Add);

        // The entity stops being visible before the owner answers — exactly the case the reply is meant to
        // report, and the point at which looking the binding back up would no longer find it.
        connection.RemoteEntities.TryRemove(1, out _);

        ulong sequence = connection.PendingReplies.Keys.Single();
        connection.PendingReplies[sequence].Callback(new Reply { InResponseTo = sequence, Failure = "Entity is no longer visible." });

        Assert.Same(binding.RemoteEntity, Assert.Single(failures).Entity);
    }

    [Fact]
    public void MethodCall_Failing_StillReportsFailureAfterTheEntityStoppedBeingVisible()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        using ServiceProvider disposeProvider = provider;
        using NetManager disposeManager = manager;

        NetConnection connection = (NetConnection)manager.Connect("127.0.0.1", TestHarness.GetFreeUdpPort());
        connection.SetStatus(NetStatus.Connected);

        NetControllerType controllerType = manager.TypeCache.GetController(typeof(ITestController));
        NetControllerMember member = controllerType.Methods.Single(method => method.Name == nameof(ITestController.Ping));
        INetRemoteEntity remoteEntity = Mock.Of<INetRemoteEntity>();

        ulong operation = manager.AllocateCallOperation(connection, entityId: 1, controllerId: 0, member, remoteEntity);
        manager.SendCall(connection, entityId: 1, member, operation, arguments: [], completion: null);

        List<NetRemoteEntityFailure> failures = [];
        using IDisposable subscription = manager.Failed.Subscribe(failures.Add);

        ulong sequence = connection.PendingReplies.Keys.Single();
        connection.PendingReplies[sequence].Callback(new Reply { InResponseTo = sequence, Failure = "Entity is no longer visible." });

        Assert.Same(remoteEntity, Assert.Single(failures).Entity);
    }

    [Fact]
    public void SweepConnection_ResendsAnUnacknowledgedViewerPropertyRequest()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        using ServiceProvider disposeProvider = provider;
        using NetManager disposeManager = manager;

        NetConnection connection = (NetConnection)manager.Connect("127.0.0.1", TestHarness.GetFreeUdpPort());
        connection.SetStatus(NetStatus.Connected);
        connection.LastReceivedAt = DateTime.UtcNow;

        NetModelType modelType = manager.TypeCache.GetModel(typeof(ITestModel));
        NetControllerType controllerType = manager.TypeCache.GetController(typeof(ITestController));
        NetListenerBinding<ITestModel, ITestController> listenerBinding = new(modelType, controllerType);
        connection.RemoteEntities[1] = listenerBinding.CreateEntity(manager, connection, entityId: 1, modelId: 0, controllerId: 0);

        NetModelMember member = modelType.Properties.Single(property => property.Name == nameof(ITestModel.OpenValue));
        manager.RequestPropertySet(connection, 1, member, 5);

        int countBeforeSweep = connection.PendingReplies.Count;
        Assert.True(countBeforeSweep > 0);

        manager.PingInterval = TimeSpan.Zero;
        manager.SweepConnection(connection);

        Assert.True(connection.PendingReplies.Count > countBeforeSweep);
    }

    [Fact]
    public void PropertyChannels_ForOwnedAndViewedEntities_AreKeptApartEvenUnderTheSameEntityId()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        using ServiceProvider disposeProvider = provider;
        using NetManager disposeManager = manager;

        NetConnection connection = (NetConnection)manager.Connect("127.0.0.1", TestHarness.GetFreeUdpPort());
        connection.SetStatus(NetStatus.Connected);
        connection.LastReceivedAt = DateTime.UtcNow;

        NetModelType modelType = manager.TypeCache.GetModel(typeof(ITestModel));
        NetControllerType controllerType = manager.TypeCache.GetController(typeof(ITestController));
        TestEntity owned = new(new NetState<ITestModel>(modelType));

        manager.RegisterEntityGroup(owned, modelType, controllerType, (NetGroupBase)manager.All);
        manager.ReevaluateConnection(connection);

        ulong ownedId = connection.OutgoingEntityIds[owned];
        connection.OutgoingLifecycles[ownedId].Acknowledge(connection.PendingReplies.Keys.Single(), out _);
        owned.State.Model.OpenValue = 1;

        NetListenerBinding<ITestModel, ITestController> listenerBinding = new(modelType, controllerType);
        connection.RemoteEntities[ownedId] = listenerBinding.CreateEntity(manager, connection, ownedId, modelId: 0, controllerId: 0);

        NetModelMember member = modelType.Properties.Single(property => property.Name == nameof(ITestModel.OpenValue));
        manager.RequestPropertySet(connection, ownedId, member, 2);

        // The same entity id is live in both directions at once; each must have kept its own channel.
        Assert.Contains((ownedId, member.Id), connection.OwnedPropertyChannels.Keys);
        Assert.Contains((ownedId, member.Id), connection.ViewedPropertyChannels.Keys);
        Assert.False(connection.OwnedPropertyChannels[(ownedId, member.Id)].ReportFailureAsRemote);
        Assert.True(connection.ViewedPropertyChannels[(ownedId, member.Id)].ReportFailureAsRemote);
    }

    [Fact]
    public void RepeatedResends_DoNotAccumulatePendingReplies()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        using ServiceProvider disposeProvider = provider;
        using NetManager disposeManager = manager;

        // Nothing is listening on the far side, so every tracked send stays unanswered and is resent forever.
        NetConnection connection = (NetConnection)manager.Connect("127.0.0.1", TestHarness.GetFreeUdpPort());
        connection.SetStatus(NetStatus.Connected);
        connection.LastReceivedAt = DateTime.UtcNow;

        NetModelType modelType = manager.TypeCache.GetModel(typeof(ITestModel));
        NetControllerType controllerType = manager.TypeCache.GetController(typeof(ITestController));
        TestEntity entity = new(new NetState<ITestModel>(modelType));

        manager.RegisterEntityGroup(entity, modelType, controllerType, (NetGroupBase)manager.All);
        manager.ReevaluateConnection(connection);
        connection.OutgoingLifecycles[connection.OutgoingEntityIds[entity]].Acknowledge(connection.PendingReplies.Keys.Single(), out _);

        entity.State.Model.Value = 1;
        manager.PingInterval = TimeSpan.Zero;

        manager.SweepConnection(connection);
        int afterFirstSweep = connection.PendingReplies.Count;

        for (int sweep = 0; sweep < 20; sweep++)
        {
            manager.SweepConnection(connection);
        }

        Assert.Equal(afterFirstSweep, connection.PendingReplies.Count);
    }

    [Fact]
    public void ResendingAMethodCall_ReplacesTheWaitOnItsPreviousAttempt()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        using ServiceProvider disposeProvider = provider;
        using NetManager disposeManager = manager;

        NetConnection connection = (NetConnection)manager.Connect("127.0.0.1", TestHarness.GetFreeUdpPort());
        connection.SetStatus(NetStatus.Connected);
        connection.LastReceivedAt = DateTime.UtcNow;

        NetControllerType controllerType = manager.TypeCache.GetController(typeof(ITestController));
        NetControllerMember member = controllerType.Methods.Single(method => method.Name == nameof(ITestController.Ping));

        ulong operation = manager.AllocateCallOperation(connection, entityId: 1, controllerId: 0, member, Mock.Of<INetRemoteEntity>());
        manager.SendCall(connection, entityId: 1, member, operation, arguments: [], completion: null);

        ulong firstAttempt = connection.PendingReplies.Keys.Single();

        manager.PingInterval = TimeSpan.Zero;
        manager.SweepConnection(connection);

        Assert.DoesNotContain(firstAttempt, connection.PendingReplies.Keys);
        Assert.Contains(connection.MethodCallerChannels[(1, member.Id)].Channel.LastSentSequence!.Value, connection.PendingReplies.Keys);
    }

    private static (ServiceProvider Provider, NetManager Manager) CreateManager()
    {
        (ServiceProvider provider, INetManager manager) = TestHarness.CreateManager();
        return (provider, (NetManager)manager);
    }

    private sealed class RecordingSerializer : INetSerializer
    {
        public int SerializeCalls { get; private set; }

        public IMemoryOwner<byte> Serialize(object? value)
        {
            SerializeCalls++;
            return new NetPooledMemory(ArrayPool<byte>.Shared.Rent(1), 0);
        }

        public object? Deserialize(ReadOnlyMemory<byte> data) => null;
    }
}
