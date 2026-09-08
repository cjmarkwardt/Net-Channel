namespace Markwardt.NetChannel.Tests;

/// Both peers owning entities over the same connection is the case where each side's independent entity id
/// numbering overlaps, so every id-keyed path has to stay separated by direction.
public sealed class NetBidirectionalEntityTests : IAsyncDisposable
{
    private readonly ServiceProvider serverProvider;

    private readonly ServiceProvider clientProvider;

    private readonly INetManager server;

    private readonly INetManager client;

    public NetBidirectionalEntityTests()
    {
        (serverProvider, server) = TestHarness.CreateManager();
        (clientProvider, client) = TestHarness.CreateManager();
    }

    public async ValueTask DisposeAsync()
    {
        await server.DisposeAsync();
        await client.DisposeAsync();
        await serverProvider.DisposeAsync();
        await clientProvider.DisposeAsync();
    }

    [Fact]
    public async Task EachPeersOwnBroadcast_ReachesTheOtherWithoutCrossingWires()
    {
        Pair pair = await Connect();

        pair.ServerEntity.State.Model.Value = 11;
        pair.ClientEntity.State.Model.Value = 22;

        await TestHarness.WaitUntil(() => pair.ClientView.Model.Value == 11);
        await TestHarness.WaitUntil(() => pair.ServerView.Model.Value == 22);

        Assert.Equal(11, pair.ServerEntity.State.Model.Value);
        Assert.Equal(22, pair.ClientEntity.State.Model.Value);
    }

    [Fact]
    public async Task AViewerRequest_ReachesTheOwnerRatherThanTheViewersOwnEntity()
    {
        Pair pair = await Connect();

        pair.ClientView.Model.OpenValue = 33;

        await TestHarness.WaitUntil(() => pair.ServerEntity.State.Model.OpenValue == 33);

        await Task.Delay(200);
        Assert.Equal(0, pair.ClientEntity.State.Model.OpenValue);
    }

    [Fact]
    public async Task BothDirectionsCanRequestSetsAtOnce()
    {
        Pair pair = await Connect();

        pair.ClientView.Model.OpenValue = 5;
        pair.ServerView.Model.OpenValue = 6;

        await TestHarness.WaitUntil(() => pair.ServerEntity.State.Model.OpenValue == 5);
        await TestHarness.WaitUntil(() => pair.ClientEntity.State.Model.OpenValue == 6);
    }

    [Fact]
    public async Task AMethodCallInEachDirection_ReachesTheRightOwner()
    {
        Pair pair = await Connect();

        Assert.Equal(8, await TestHarness.WaitForTask(pair.ClientView.Controller.Double(4)));
        Assert.Equal(14, await TestHarness.WaitForTask(pair.ServerView.Controller.Double(7)));
    }

    [Fact]
    public async Task BothPeersUseTheSameEntityIdForTheirOwnEntity()
    {
        Pair pair = await Connect();

        NetConnection clientSide = (NetConnection)pair.ClientConnection;

        Assert.Equal(clientSide.OutgoingEntityIds.Values.Single(), clientSide.RemoteEntities.Keys.Single());
    }

    private async Task<Pair> Connect()
    {
        server.Host(0);
        INetConnection clientConnection = client.Connect("127.0.0.1", server.HostPort!.Value);
        await TestHarness.WaitForTask(clientConnection.Wait());
        await TestHarness.WaitUntil(() => server.ActiveConnections.Count == 1);

        TaskCompletionSource<INetRemoteEntity<ITestModel, ITestController>> serverSees = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<INetRemoteEntity<ITestModel, ITestController>> clientSees = new(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Listen<ITestModel, ITestController>(e => serverSees.TrySetResult(e));
        client.Listen<ITestModel, ITestController>(e => clientSees.TrySetResult(e));

        TestEntity serverEntity = new(serverProvider.GetRequiredService<INetStateFactory>().Create<ITestModel>());
        TestEntity clientEntity = new(clientProvider.GetRequiredService<INetStateFactory>().Create<ITestModel>());

        server.All.Add<ITestModel, ITestController>(serverEntity);
        client.All.Add<ITestModel, ITestController>(clientEntity);

        return new Pair
        {
            ClientConnection = clientConnection,
            ServerEntity = serverEntity,
            ClientEntity = clientEntity,
            ServerView = await TestHarness.WaitForTask(serverSees.Task),
            ClientView = await TestHarness.WaitForTask(clientSees.Task),
        };
    }

    private sealed record Pair
    {
        public required INetConnection ClientConnection { get; init; }

        public required TestEntity ServerEntity { get; init; }

        public required TestEntity ClientEntity { get; init; }

        public required INetRemoteEntity<ITestModel, ITestController> ServerView { get; init; }

        public required INetRemoteEntity<ITestModel, ITestController> ClientView { get; init; }
    }
}
