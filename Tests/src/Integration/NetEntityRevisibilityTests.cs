namespace Markwardt.NetChannel.Tests;

public interface IBlobModel
{
    byte[]? Blob { get; set; }
}

public sealed class BlobEntity(INetState<IBlobModel> state) : NetEntity<IBlobModel, IEmptyController>(state), IEmptyController;

public sealed class NetEntityRevisibilityTests
{
    [Fact]
    public async Task EntityRemovedThenReAdded_BecomesVisibleAndSyncsAgain()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();

        TestEntity entity = new(pair.ServerStateFactory.Create<ITestModel>());
        INetRemoteEntity<ITestModel, ITestController> first = await pair.ListenThenAdd(entity);

        entity.State.Model.Value = 1;
        await TestHarness.WaitUntil(() => first.Model.Value == 1);

        TaskCompletionSource<INetRemoteEntity> destroyed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable destroyedSubscription = first.Destroyed.Subscribe(e => destroyed.TrySetResult(e));

        pair.Server.All.Remove(entity);
        await TestHarness.WaitForTask(destroyed.Task);

        INetRemoteEntity<ITestModel, ITestController> second = await pair.ListenThenAdd(entity);

        entity.State.Model.Value = 7;

        await TestHarness.WaitUntil(() => second.Model.Value == 7);
    }

    [Fact]
    public async Task EntityLeavingAndReenteringRange_BecomesVisibleAndSyncsAgain()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();

        NetRange range = new(10, 20);
        pair.ServerSideConnection.Position = new NetPosition(new Vector3(0, 0, 0), range);

        List<INetRemoteEntity<IPositionedModel, IEmptyController>> seen = [];
        using IDisposable listenHandle = pair.Client.Listen<IPositionedModel, IEmptyController>(seen.Add);

        PositionedEntity entity = new(pair.ServerStateFactory.Create<IPositionedModel>());
        entity.State.Model.Position = new NetPosition(new Vector3(0, 0, 0), range);
        pair.Server.All.Add<IPositionedModel, IEmptyController>(entity);

        await TestHarness.WaitUntil(() => seen.Count == 1);

        pair.ServerSideConnection.Position = new NetPosition(new Vector3(100, 0, 0), range);
        await Task.Delay(200);

        pair.ServerSideConnection.Position = new NetPosition(new Vector3(0, 0, 0), range);
        await TestHarness.WaitUntil(() => seen.Count == 2);

        entity.State.Model.Position = new NetPosition(new Vector3(1, 2, 3), range);

        await TestHarness.WaitUntil(() => seen[^1].Model.Position?.Position == new Vector3(1, 2, 3));
    }

    [Fact]
    public async Task RepeatedVisibilityChanges_KeepWorkingAcrossManyCycles()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();

        List<INetRemoteEntity<ITestModel, ITestController>> seen = [];
        using IDisposable listenHandle = pair.Client.Listen<ITestModel, ITestController>(seen.Add);

        TestEntity entity = new(pair.ServerStateFactory.Create<ITestModel>());

        for (int cycle = 1; cycle <= 5; cycle++)
        {
            pair.Server.All.Add<ITestModel, ITestController>(entity);
            await TestHarness.WaitUntil(() => seen.Count == cycle);

            entity.State.Model.Value = cycle;
            await TestHarness.WaitUntil(() => seen[^1].Model.Value == cycle);

            pair.Server.All.Remove(entity);
            await Task.Delay(100);
        }

        Assert.Equal(5, seen.Count);
    }

    [Fact]
    public async Task EntityChurn_LeavesNoPerEntityStateBehindOnEitherSide()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();
        NetConnection owner = (NetConnection)pair.ServerSideConnection;
        NetConnection viewer = (NetConnection)pair.ClientConnection;

        for (int i = 1; i <= 10; i++)
        {
            TestEntity entity = new(pair.ServerStateFactory.Create<ITestModel>());
            INetRemoteEntity<ITestModel, ITestController> remote = await pair.ListenThenAdd(entity);

            entity.State.Model.Value = i;
            await TestHarness.WaitUntil(() => remote.Model.Value == i);

            pair.Server.All.Remove(entity);
            await Task.Delay(50);
        }

        await TestHarness.WaitUntil(() => owner.OutgoingEntityIds.IsEmpty && owner.OutgoingLifecycles.IsEmpty);

        Assert.Empty(owner.OutgoingEntitiesById);
        Assert.Empty(owner.OwnedPropertyChannels);
        Assert.Empty(owner.MethodReceiverChannels);
        Assert.Empty(viewer.RemoteEntities);
        Assert.Empty(viewer.ViewedPropertySequences);
        Assert.Empty(viewer.MethodCallerChannels);
    }

    [Fact]
    public async Task EntityIds_AreNeverReusedAcrossVisibilityCycles()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();
        NetConnection owner = (NetConnection)pair.ServerSideConnection;

        TestEntity entity = new(pair.ServerStateFactory.Create<ITestModel>());

        await pair.ListenThenAdd(entity);
        ulong firstId = owner.OutgoingEntityIds[entity];

        pair.Server.All.Remove(entity);
        await TestHarness.WaitUntil(() => owner.OutgoingEntityIds.IsEmpty);

        await pair.ListenThenAdd(entity);
        ulong secondId = owner.OutgoingEntityIds[entity];

        Assert.NotEqual(firstId, secondId);
    }

    [Fact]
    public async Task PropertyTooLargeForOneDatagram_DoesNotThrowIntoTheSetter()
    {
        await using ConnectedPair pair = await ConnectedPair.Create();

        TaskCompletionSource<INetRemoteEntity<IBlobModel, IEmptyController>> seen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable listenHandle = pair.Client.Listen<IBlobModel, IEmptyController>(e => seen.TrySetResult(e));

        BlobEntity entity = new(pair.ServerStateFactory.Create<IBlobModel>());
        pair.Server.All.Add<IBlobModel, IEmptyController>(entity);
        INetRemoteEntity<IBlobModel, IEmptyController> remote = await TestHarness.WaitForTask(seen.Task);

        entity.State.Model.Blob = new byte[200_000];
        await Task.Delay(300);

        Assert.Null(remote.Model.Blob);

        entity.State.Model.Blob = [1, 2, 3];
        await TestHarness.WaitUntil(() => remote.Model.Blob is not null);

        Assert.Equal<byte>([1, 2, 3], remote.Model.Blob!);
    }
}
