namespace Markwardt.NetChannel.Tests;

public sealed class NetManagerIntegrationTests
{
    [Fact]
    public async Task EntityPropertyAndMethodSyncEndToEnd()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();

        TestEntity entity = new(pair.ServerStateFactory.Create<ITestModel>());
        entity.State.Model.Value = 42;
        INetRemoteEntity<ITestModel, ITestController> remote = await pair.ListenThenAdd(entity);
        await TestHarness.WaitUntil(() => remote.Model.Value == 42);

        int result = await remote.Controller.Double(21);
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task ViewerPropertySet_WithoutAccess_IsDeniedAndReported()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();

        TestEntity entity = new(pair.ServerStateFactory.Create<ITestModel>());
        INetRemoteEntity<ITestModel, ITestController> remote = await pair.ListenThenAdd(entity);

        List<NetRemoteEntityFailure> failures = [];
        using IDisposable subscription = pair.Client.Failed.Subscribe(failures.Add);

        remote.Model.Value = 99;

        await TestHarness.WaitUntil(() => failures.Count > 0);
        Assert.Equal(0, entity.State.Model.Value);
    }

    [Fact]
    public async Task ViewerPropertySet_WithAccess_PropagatesToOwner()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();

        TestEntity entity = new(pair.ServerStateFactory.Create<ITestModel>());
        INetRemoteEntity<ITestModel, ITestController> remote = await pair.ListenThenAdd(entity);

        remote.Model.OpenValue = 7;

        await TestHarness.WaitUntil(() => entity.State.Model.OpenValue == 7);
    }

    [Fact]
    public async Task ViewerPropertySet_RoleGated_RequiresPromotion()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();

        TestEntity entity = new(pair.ServerStateFactory.Create<ITestModel>());
        INetRemoteEntity<ITestModel, ITestController> remote = await pair.ListenThenAdd(entity);

        List<NetRemoteEntityFailure> failures = [];
        using IDisposable subscription = pair.Client.Failed.Subscribe(failures.Add);

        remote.Model.AdminValue = 5;
        await TestHarness.WaitUntil(() => failures.Count > 0);
        Assert.Equal(0, entity.State.Model.AdminValue);

        pair.ServerSideConnection.Promote("admin");
        remote.Model.AdminValue = 5;
        await TestHarness.WaitUntil(() => entity.State.Model.AdminValue == 5);
    }

    [Fact]
    public async Task MethodCall_WithoutAccess_FaultsWithNetFailedException()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();

        TestEntity entity = new(pair.ServerStateFactory.Create<ITestModel>());
        INetRemoteEntity<ITestModel, ITestController> remote = await pair.ListenThenAdd(entity);

        NetFailedException exception = await Assert.ThrowsAsync<NetFailedException>(() => remote.Controller.Restricted());
        Assert.Equal("Access denied.", exception.Message);
    }

    [Fact]
    public async Task MethodCall_RoleGated_RequiresPromotion()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();

        TestEntity entity = new(pair.ServerStateFactory.Create<ITestModel>());
        INetRemoteEntity<ITestModel, ITestController> remote = await pair.ListenThenAdd(entity);

        await Assert.ThrowsAsync<NetFailedException>(() => remote.Controller.AdminOnly());

        pair.ServerSideConnection.Promote("admin");
        await remote.Controller.AdminOnly();
    }

    [Fact]
    public async Task MethodCall_ThatThrows_FaultsWithGenericMessage()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();

        TestEntity entity = new(pair.ServerStateFactory.Create<ITestModel>());
        INetRemoteEntity<ITestModel, ITestController> remote = await pair.ListenThenAdd(entity);

        NetFailedException exception = await Assert.ThrowsAsync<NetFailedException>(() => remote.Controller.AlwaysThrows());
        Assert.DoesNotContain("boom", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FireAndForgetMethodCall_ThatThrows_ReportsFailedWithNoTaskToFault()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();

        TestEntity entity = new(pair.ServerStateFactory.Create<ITestModel>());
        INetRemoteEntity<ITestModel, ITestController> remote = await pair.ListenThenAdd(entity);

        List<NetRemoteEntityFailure> failures = [];
        using IDisposable subscription = pair.Client.Failed.Subscribe(failures.Add);

        remote.Controller.FireAndForgetThrows();

        await TestHarness.WaitUntil(() => failures.Count > 0);
    }

    [Fact]
    public async Task SecureMethodCall_RoundTripsThroughEncryption()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();

        TestEntity entity = new(pair.ServerStateFactory.Create<ITestModel>());
        INetRemoteEntity<ITestModel, ITestController> remote = await pair.ListenThenAdd(entity);

        int result = await remote.Controller.SecureDouble(11);
        Assert.Equal(22, result);
    }

    [Fact]
    public async Task View_ControlsVisibilityIndependentlyOfAll()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();

        INetView view = pair.Server.CreateView("my-view");
        Assert.Same(view, pair.Server.GetView("my-view"));

        TaskCompletionSource<INetRemoteEntity<ITestModel, ITestController>> seen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable listenHandle = pair.Client.Listen<ITestModel, ITestController>(e => seen.TrySetResult(e));

        TestEntity entity = new(pair.ServerStateFactory.Create<ITestModel>());
        view.Add<ITestModel, ITestController>(entity);

        await Task.Delay(150);
        Assert.False(seen.Task.IsCompleted);

        view.AddViewers(pair.ServerSideConnection);
        INetRemoteEntity<ITestModel, ITestController> remote = await TestHarness.WaitForTask(seen.Task);

        TaskCompletionSource<INetRemoteEntity> destroyed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable destroyedSubscription = remote.Destroyed.Subscribe(e => destroyed.TrySetResult(e));

        view.RemoveViewers(pair.ServerSideConnection);
        await TestHarness.WaitForTask(destroyed.Task);
    }

    [Fact]
    public async Task PositionBasedVisibility_UsesHysteresis()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();

        NetRange range = new(10, 20);
        pair.ServerSideConnection.Position = new NetPosition(new Vector3(100, 0, 0), range);

        TaskCompletionSource<INetRemoteEntity<IPositionedModel, IEmptyController>> seen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable listenHandle = pair.Client.Listen<IPositionedModel, IEmptyController>(e => seen.TrySetResult(e));

        PositionedEntity entity = new(pair.ServerStateFactory.Create<IPositionedModel>());
        entity.State.Model.Position = new NetPosition(new Vector3(0, 0, 0), range);
        pair.Server.All.Add<IPositionedModel, IEmptyController>(entity);

        await Task.Delay(150);
        Assert.False(seen.Task.IsCompleted);

        pair.ServerSideConnection.Position = new NetPosition(new Vector3(5, 0, 0), range);
        INetRemoteEntity<IPositionedModel, IEmptyController> remote = await TestHarness.WaitForTask(seen.Task);

        TaskCompletionSource<INetRemoteEntity> destroyed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable destroyedSubscription = remote.Destroyed.Subscribe(e => destroyed.TrySetResult(e));

        pair.ServerSideConnection.Position = new NetPosition(new Vector3(15, 0, 0), range);
        await Task.Delay(150);
        Assert.False(destroyed.Task.IsCompleted);

        pair.ServerSideConnection.Position = new NetPosition(new Vector3(25, 0, 0), range);
        await TestHarness.WaitForTask(destroyed.Task);
    }

    [Fact]
    public async Task Connection_MeasuresLatencyViaPing()
    {
        await using ConnectedPair pair = await ConnectedPair.Create(pingInterval: TimeSpan.FromMilliseconds(30));

        await Task.Delay(300);
        Assert.True(pair.ClientConnection.Latency >= TimeSpan.Zero);
        Assert.Equal(NetStatus.Connected, pair.ClientConnection.Status);
    }

    [Fact]
    public async Task Disconnect_EndsConnectionOnBothSides()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();

        await pair.ClientConnection.Disconnect();

        Assert.Equal(NetStatus.Disconnected, pair.ClientConnection.Status);
        await TestHarness.WaitUntil(() => pair.Server.ActiveConnections.Count == 0);
    }

    [Fact]
    public async Task DisposeAsync_EndsEveryConnectionOnBothSides()
    {
        ConnectedPair pair = await ConnectedPair.Create();
        INetConnection clientConnection = pair.ClientConnection;
        INetConnection serverConnection = pair.ServerSideConnection;

        await pair.Client.DisposeAsync();

        Assert.Equal(NetStatus.Disconnected, clientConnection.Status);
        await TestHarness.WaitUntil(() => serverConnection.Status == NetStatus.Disconnected);

        await pair.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_EndsEveryConnectionImmediately()
    {
        ConnectedPair pair = await ConnectedPair.Create();
        INetConnection clientConnection = pair.ClientConnection;

        List<NetStatusChange> changes = [];
        using IDisposable subscription = pair.Client.StatusChanged.Subscribe(changes.Add);

        pair.Client.Dispose();

        Assert.Equal(NetStatus.Disconnected, clientConnection.Status);
        Assert.Contains(changes, change => change.Connection == clientConnection && change.Status == NetStatus.Disconnected);

        await pair.DisposeAsync();
    }

    [Fact]
    public async Task Drop_WithoutNotifying_EventuallyTriggersDroppedOnPeer()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();
        pair.Server.DisconnectTimeout = TimeSpan.FromMilliseconds(150);
        INetConnection serverSideConnection = pair.ServerSideConnection;

        List<NetConnectionFailure> dropped = [];
        using IDisposable subscription = pair.Server.Dropped.Subscribe(dropped.Add);

        pair.ClientConnection.Drop();

        await TestHarness.WaitUntil(() => dropped.Count > 0, timeoutMilliseconds: 3000);
        Assert.Equal(NetStatus.Disconnected, serverSideConnection.Status);
    }

    [Fact]
    public async Task ViewDestroy_RetractsItsEntitiesFromEveryViewer()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();

        INetView view = pair.Server.CreateView();
        view.AddViewers(pair.ServerSideConnection);

        TaskCompletionSource<INetRemoteEntity<ITestModel, ITestController>> seen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable listenHandle = pair.Client.Listen<ITestModel, ITestController>(e => seen.TrySetResult(e));

        TestEntity entity = new(pair.ServerStateFactory.Create<ITestModel>());
        view.Add<ITestModel, ITestController>(entity);

        INetRemoteEntity<ITestModel, ITestController> remote = await TestHarness.WaitForTask(seen.Task);

        TaskCompletionSource<INetRemoteEntity> destroyed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable destroyedSubscription = remote.Destroyed.Subscribe(e => destroyed.TrySetResult(e));

        view.Destroy();

        await TestHarness.WaitForTask(destroyed.Task);
        Assert.DoesNotContain(view, pair.Server.Views);
    }

    [Fact]
    public async Task EntityInTwoGroups_StaysVisibleUntilRemovedFromBoth()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();

        INetView view = pair.Server.CreateView();
        view.AddViewers(pair.ServerSideConnection);

        TaskCompletionSource<INetRemoteEntity<ITestModel, ITestController>> seen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable listenHandle = pair.Client.Listen<ITestModel, ITestController>(e => seen.TrySetResult(e));

        TestEntity entity = new(pair.ServerStateFactory.Create<ITestModel>());
        pair.Server.All.Add<ITestModel, ITestController>(entity);
        view.Add<ITestModel, ITestController>(entity);

        INetRemoteEntity<ITestModel, ITestController> remote = await TestHarness.WaitForTask(seen.Task);

        bool destroyed = false;
        using IDisposable destroyedSubscription = remote.Destroyed.Subscribe(_ => destroyed = true);

        pair.Server.All.Remove(entity);
        await Task.Delay(200);
        Assert.False(destroyed);

        entity.State.Model.Value = 9;
        await TestHarness.WaitUntil(() => remote.Model.Value == 9);

        view.Remove(entity);
        await TestHarness.WaitUntil(() => destroyed);
    }
}
