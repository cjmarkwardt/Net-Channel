namespace Markwardt.NetChannel.Tests;

public sealed class NetEntityDestructionTests
{
    [Fact]
    public async Task DestroyingAnEntity_RetractsItFromEveryViewer()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();

        TestEntity entity = new(pair.ServerStateFactory.Create<ITestModel>());
        INetRemoteEntity<ITestModel, ITestController> remote = await pair.ListenThenAdd(entity);

        TaskCompletionSource<INetRemoteEntity> destroyed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable subscription = remote.Destroyed.Subscribe(e => destroyed.TrySetResult(e));

        entity.Destroy();

        await TestHarness.WaitForTask(destroyed.Task);
        Assert.DoesNotContain(entity, pair.Server.All.Entities);
    }

    [Fact]
    public async Task DestroyingAnEntity_StopsSyncingItsProperties()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();

        TestEntity entity = new(pair.ServerStateFactory.Create<ITestModel>());
        INetRemoteEntity<ITestModel, ITestController> remote = await pair.ListenThenAdd(entity);

        entity.State.Model.Value = 1;
        await TestHarness.WaitUntil(() => remote.Model.Value == 1);

        entity.Destroy();
        await Task.Delay(200);

        entity.State.Model.Value = 99;
        await Task.Delay(400);

        Assert.Equal(1, remote.Model.Value);
    }

    [Fact]
    public async Task DestroyingAnEntity_RetractsItFromEveryGroupItIsIn()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();

        INetView view = pair.Server.CreateView();
        view.AddViewers(pair.ServerSideConnection);

        TestEntity entity = new(pair.ServerStateFactory.Create<ITestModel>());
        pair.Server.All.Add<ITestModel, ITestController>(entity);
        view.Add<ITestModel, ITestController>(entity);

        entity.Destroy();
        await Task.Delay(200);

        Assert.DoesNotContain(entity, pair.Server.All.Entities);
        Assert.DoesNotContain(entity, view.Entities);
        Assert.DoesNotContain(entity, ((NetManager)pair.Server).TrackedEntities);
    }

    [Fact]
    public void DestroyingAnEntityThatWasNeverAdded_IsANoOp()
    {
        (ServiceProvider provider, INetManager manager) = TestHarness.CreateManager();
        using ServiceProvider disposeProvider = provider;
        using INetManager disposeManager = manager;

        INetStateFactory factory = provider.GetRequiredService<INetStateFactory>();
        TestEntity entity = new(factory.Create<ITestModel>());

        entity.Destroy();

        Assert.True(entity.IsDestroyed);
        Assert.Empty(((NetManager)manager).TrackedEntities);
    }

    [Fact]
    public async Task AddingAnAlreadyDestroyedEntity_LeavesItUntracked()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();

        TestEntity entity = new(pair.ServerStateFactory.Create<ITestModel>());
        entity.Destroy();

        pair.Server.All.Add<ITestModel, ITestController>(entity);
        await Task.Delay(200);

        Assert.DoesNotContain(entity, ((NetManager)pair.Server).TrackedEntities);
    }

    [Fact]
    public async Task InheritedModelAndControllerMembers_SyncEndToEnd()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();

        TaskCompletionSource<INetRemoteEntity<IInheritingModel, IInheritingController>> seen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable listenHandle = pair.Client.Listen<IInheritingModel, IInheritingController>(e => seen.TrySetResult(e));

        INetStateFactory factory = pair.ServerStateFactory;
        InheritingEntity entity = new(factory.Create<IInheritingModel>());
        pair.Server.All.Add<IInheritingModel, IInheritingController>(entity);

        INetRemoteEntity<IInheritingModel, IInheritingController> remote = await TestHarness.WaitForTask(seen.Task);

        entity.State.Model.Inherited = 21;
        entity.State.Model.Own = 22;

        await TestHarness.WaitUntil(() => remote.Model.Inherited == 21);
        await TestHarness.WaitUntil(() => remote.Model.Own == 22);

        await TestHarness.WaitForTask(remote.Controller.InheritedCall());
        await TestHarness.WaitForTask(remote.Controller.OwnCall());
    }

    [Fact]
    public async Task EntityAddedTwiceToOneGroup_StopsSyncingAfterASingleRemove()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();

        TestEntity entity = new(pair.ServerStateFactory.Create<ITestModel>());
        INetRemoteEntity<ITestModel, ITestController> remote = await pair.ListenThenAdd(entity);

        pair.Server.All.Add<ITestModel, ITestController>(entity);

        TaskCompletionSource<INetRemoteEntity> destroyed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable subscription = remote.Destroyed.Subscribe(e => destroyed.TrySetResult(e));

        pair.Server.All.Remove(entity);

        await TestHarness.WaitForTask(destroyed.Task);

        entity.State.Model.Value = 42;
        await Task.Delay(400);

        Assert.NotEqual(42, remote.Model.Value);
    }
}
