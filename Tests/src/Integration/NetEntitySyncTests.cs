namespace Markwardt.NetChannel.Tests;

public sealed class NetEntitySyncTests
{
    [Fact]
    public async Task PropertyChange_BroadcastsToEveryViewer()
    {
        (ServiceProvider serverProvider, INetManager server) = TestHarness.CreateManager();
        (ServiceProvider firstClientProvider, INetManager firstClient) = TestHarness.CreateManager();
        (ServiceProvider secondClientProvider, INetManager secondClient) = TestHarness.CreateManager();
        await using ServiceProvider disposeServerProvider = serverProvider;
        await using ServiceProvider disposeFirstClientProvider = firstClientProvider;
        await using ServiceProvider disposeSecondClientProvider = secondClientProvider;
        await using INetManager disposeServer = server;
        await using INetManager disposeFirstClient = firstClient;
        await using INetManager disposeSecondClient = secondClient;

        server.Host(0);
        int port = server.HostPort!.Value;

        INetConnection firstConnection = firstClient.Connect("127.0.0.1", port);
        INetConnection secondConnection = secondClient.Connect("127.0.0.1", port);
        await TestHarness.WaitForTask(firstConnection.Wait());
        await TestHarness.WaitForTask(secondConnection.Wait());
        await TestHarness.WaitUntil(() => server.ActiveConnections.Count == 2);

        TaskCompletionSource<INetRemoteEntity<ITestModel, ITestController>> firstSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<INetRemoteEntity<ITestModel, ITestController>> secondSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable firstListen = firstClient.Listen<ITestModel, ITestController>(e => firstSeen.TrySetResult(e));
        using IDisposable secondListen = secondClient.Listen<ITestModel, ITestController>(e => secondSeen.TrySetResult(e));

        INetStateFactory serverStateFactory = serverProvider.GetRequiredService<INetStateFactory>();
        TestEntity entity = new(serverStateFactory.Create<ITestModel>());
        server.All.Add<ITestModel, ITestController>(entity);

        INetRemoteEntity<ITestModel, ITestController> firstRemote = await TestHarness.WaitForTask(firstSeen.Task);
        INetRemoteEntity<ITestModel, ITestController> secondRemote = await TestHarness.WaitForTask(secondSeen.Task);

        entity.State.Model.Value = 77;

        await TestHarness.WaitUntil(() => firstRemote.Model.Value == 77);
        await TestHarness.WaitUntil(() => secondRemote.Model.Value == 77);
    }

    [Fact]
    public async Task EntityRemovedFromAll_TriggersDestroyedOnViewer()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();

        TestEntity entity = new(pair.ServerStateFactory.Create<ITestModel>());
        INetRemoteEntity<ITestModel, ITestController> remote = await pair.ListenThenAdd(entity);

        TaskCompletionSource<INetRemoteEntity> destroyed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable subscription = remote.Destroyed.Subscribe(e => destroyed.TrySetResult(e));

        pair.Server.All.Remove(entity);

        await TestHarness.WaitForTask(destroyed.Task);
    }

    [Fact]
    public async Task RapidSequentialMethodCalls_CompleteInOrder()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();

        TestEntity entity = new(pair.ServerStateFactory.Create<ITestModel>());
        INetRemoteEntity<ITestModel, ITestController> remote = await pair.ListenThenAdd(entity);

        Task<int> first = remote.Controller.IncrementCounter();
        Task<int> second = remote.Controller.IncrementCounter();
        Task<int> third = remote.Controller.IncrementCounter();

        int[] results = await Task.WhenAll(first, second, third);

        Assert.Equal([1, 2, 3], results);
    }

    [Fact]
    public async Task RapidPropertyChanges_SupersedeToFinalValue()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();

        TestEntity entity = new(pair.ServerStateFactory.Create<ITestModel>());
        INetRemoteEntity<ITestModel, ITestController> remote = await pair.ListenThenAdd(entity);

        entity.State.Model.Value = 1;
        entity.State.Model.Value = 2;
        entity.State.Model.Value = 3;

        await TestHarness.WaitUntil(() => remote.Model.Value == 3);
    }

    [Fact]
    public async Task Listen_MultipleRegistrations_EachOnlyReceivesItsOwnModelControllerPair()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();

        TaskCompletionSource<INetRemoteEntity<ITestModel, ITestController>> testModelSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable testModelListen = pair.Client.Listen<ITestModel, ITestController>(e => testModelSeen.TrySetResult(e));

        bool positionedModelSeen = false;
        using IDisposable positionedListen = pair.Client.Listen<IPositionedModel, IEmptyController>(_ => positionedModelSeen = true);

        PositionedEntity positioned = new(pair.ServerStateFactory.Create<IPositionedModel>());
        pair.Server.All.Add<IPositionedModel, IEmptyController>(positioned);

        TestEntity testEntity = new(pair.ServerStateFactory.Create<ITestModel>());
        pair.Server.All.Add<ITestModel, ITestController>(testEntity);

        await TestHarness.WaitForTask(testModelSeen.Task);
        await Task.Delay(150);

        Assert.True(positionedModelSeen);
        Assert.True(testModelSeen.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task MethodCall_ReturningResult_WithoutAccess_FaultsWithNetFailedException()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();

        TestEntity entity = new(pair.ServerStateFactory.Create<ITestModel>());
        INetRemoteEntity<ITestModel, ITestController> remote = await pair.ListenThenAdd(entity);

        NetFailedException exception = await Assert.ThrowsAsync<NetFailedException>(() => remote.Controller.RestrictedResult());
        Assert.Equal("Access denied.", exception.Message);
    }

    [Fact]
    public async Task PropertySync_TeachesTheViewerEachPropertyName()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();

        TestEntity entity = new(pair.ServerStateFactory.Create<ITestModel>());
        INetRemoteEntity<ITestModel, ITestController> remote = await pair.ListenThenAdd(entity);

        entity.State.Model.Value = 12;
        await TestHarness.WaitUntil(() => remote.Model.Value == 12);

        NetConnection viewerConnection = (NetConnection)pair.ClientConnection;
        Assert.Contains(viewerConnection.IncomingPropertyNames, entry => entry.Value == nameof(ITestModel.Value));
    }

    [Fact]
    public async Task ViewerPropertySet_TeachesTheOwnerThePropertyName()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();

        TestEntity entity = new(pair.ServerStateFactory.Create<ITestModel>());
        INetRemoteEntity<ITestModel, ITestController> remote = await pair.ListenThenAdd(entity);

        remote.Model.OpenValue = 31;
        await TestHarness.WaitUntil(() => entity.State.Model.OpenValue == 31);

        NetConnection ownerConnection = (NetConnection)pair.ServerSideConnection;
        Assert.Contains(ownerConnection.RequestedPropertyNames, entry => entry.Value == nameof(ITestModel.OpenValue));
    }

    [Fact]
    public async Task MethodCall_TeachesTheOwnerTheMethodName()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();

        TestEntity entity = new(pair.ServerStateFactory.Create<ITestModel>());
        INetRemoteEntity<ITestModel, ITestController> remote = await pair.ListenThenAdd(entity);

        Assert.Equal(8, await TestHarness.WaitForTask(remote.Controller.Double(4)));

        NetConnection ownerConnection = (NetConnection)pair.ServerSideConnection;
        Assert.Contains(ownerConnection.IncomingMethodNames, entry => entry.Value == nameof(ITestController.Double));
    }

    [Fact]
    public async Task RepeatedPropertyChanges_StopResendingTheNameOnceAcknowledged()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();

        TestEntity entity = new(pair.ServerStateFactory.Create<ITestModel>());
        INetRemoteEntity<ITestModel, ITestController> remote = await pair.ListenThenAdd(entity);

        entity.State.Model.Value = 1;
        await TestHarness.WaitUntil(() => remote.Model.Value == 1);

        NetConnection ownerConnection = (NetConnection)pair.ServerSideConnection;
        await TestHarness.WaitUntil(() => ownerConnection.SentOwnedPropertyNames.Values.Any(acknowledged => acknowledged));

        entity.State.Model.Value = 2;
        await TestHarness.WaitUntil(() => remote.Model.Value == 2);
    }
}
