namespace Markwardt.NetChannel.Tests;

public sealed class NetControllerCallTests
{
    [Fact]
    public async Task SecureVoidMethod_DeliversItsEncryptedArguments()
    {
        (VariedEntity entity, INetRemoteEntity<IVariedModel, IVariedController> remote, ConnectedPair pair) = await Create();
        await using ConnectedPair disposePair = pair;

        remote.Controller.SecureVoid("hunter2");

        Assert.Equal("hunter2", await TestHarness.WaitForTask(entity.Secret));
    }

    [Fact]
    public async Task VoidMethodWithArguments_DeliversThemInOrder()
    {
        (VariedEntity entity, INetRemoteEntity<IVariedModel, IVariedController> remote, ConnectedPair pair) = await Create();
        await using ConnectedPair disposePair = pair;

        remote.Controller.VoidWithArgs(3, "x");

        Assert.Equal((3, "x"), await TestHarness.WaitForTask(entity.VoidArgs));
    }

    [Fact]
    public async Task MethodWithSeveralArgumentTypes_RoundTripsEachOne()
    {
        (VariedEntity _, INetRemoteEntity<IVariedModel, IVariedController> remote, ConnectedPair pair) = await Create();
        await using ConnectedPair disposePair = pair;

        Assert.Equal("a|2|True|1.5", await TestHarness.WaitForTask(remote.Controller.Concat("a", 2, true, 1.5)));
    }

    [Fact]
    public async Task MethodWithNoArguments_ReturnsItsResult()
    {
        (VariedEntity _, INetRemoteEntity<IVariedModel, IVariedController> remote, ConnectedPair pair) = await Create();
        await using ConnectedPair disposePair = pair;

        Assert.Equal(99, await TestHarness.WaitForTask(remote.Controller.NoArguments()));
    }

    [Fact]
    public async Task MethodReturningNull_ReturnsNullRatherThanFaulting()
    {
        (VariedEntity _, INetRemoteEntity<IVariedModel, IVariedController> remote, ConnectedPair pair) = await Create();
        await using ConnectedPair disposePair = pair;

        Assert.Null(await TestHarness.WaitForTask(remote.Controller.ReturnsNull()));
    }

    [Fact]
    public async Task MethodReturningAnEmptyCollection_ReturnsItEmptyRatherThanNull()
    {
        (VariedEntity _, INetRemoteEntity<IVariedModel, IVariedController> remote, ConnectedPair pair) = await Create();
        await using ConnectedPair disposePair = pair;

        List<int> result = await TestHarness.WaitForTask(remote.Controller.ReturnsEmptyList());

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task PromotingAfterADenial_LetsTheNextCallThrough()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();

        TestEntity entity = new(pair.ServerStateFactory.Create<ITestModel>());
        INetRemoteEntity<ITestModel, ITestController> remote = await pair.ListenThenAdd(entity);

        NetFailedException denied = await Assert.ThrowsAsync<NetFailedException>(() => TestHarness.WaitForTask(remote.Controller.AdminOnly()));
        Assert.Equal("Access denied.", denied.Message);

        pair.ServerSideConnection.Promote("admin");
        await TestHarness.WaitForTask(remote.Controller.AdminOnly());

        pair.ServerSideConnection.Demote("admin");
        await Assert.ThrowsAsync<NetFailedException>(() => TestHarness.WaitForTask(remote.Controller.AdminOnly()));
    }

    [Fact]
    public async Task DisposingAListenHandle_StopsFurtherEntitiesReachingIt()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();

        int count = 0;
        IDisposable handle = pair.Client.Listen<ITestModel, ITestController>(_ => count++);

        pair.Server.All.Add<ITestModel, ITestController>(new TestEntity(pair.ServerStateFactory.Create<ITestModel>()));
        await TestHarness.WaitUntil(() => count == 1);

        handle.Dispose();

        pair.Server.All.Add<ITestModel, ITestController>(new TestEntity(pair.ServerStateFactory.Create<ITestModel>()));
        await Task.Delay(400);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ConcurrentPropertySetsFromManyThreads_ConvergeOnTheFinalValue()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();

        TestEntity entity = new(pair.ServerStateFactory.Create<ITestModel>());
        INetRemoteEntity<ITestModel, ITestController> remote = await pair.ListenThenAdd(entity);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (int n = 0; n < 50; n++)
            {
                entity.State.Model.Value = (worker * 100) + n;
            }
        })));

        int final = entity.State.Model.Value;

        await TestHarness.WaitUntil(() => remote.Model.Value == final);
    }

    private static async Task<(VariedEntity Entity, INetRemoteEntity<IVariedModel, IVariedController> Remote, ConnectedPair Pair)> Create()
    {
        ConnectedPair pair = await ConnectedPair.Create();

        TaskCompletionSource<INetRemoteEntity<IVariedModel, IVariedController>> seen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable handle = pair.Client.Listen<IVariedModel, IVariedController>(e => seen.TrySetResult(e));

        VariedEntity entity = new(pair.ServerStateFactory.Create<IVariedModel>());
        pair.Server.All.Add<IVariedModel, IVariedController>(entity);

        return (entity, await TestHarness.WaitForTask(seen.Task), pair);
    }
}
