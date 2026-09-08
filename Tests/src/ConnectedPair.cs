namespace Markwardt.NetChannel.Tests;

public sealed class ConnectedPair : IAsyncDisposable
{
    private readonly ServiceProvider serverProvider;

    private readonly ServiceProvider clientProvider;

    private ConnectedPair(ServiceProvider serverProvider, INetManager server, ServiceProvider clientProvider, INetManager client, INetConnection clientConnection)
    {
        this.serverProvider = serverProvider;
        Server = server;
        this.clientProvider = clientProvider;
        Client = client;
        ClientConnection = clientConnection;
        ServerStateFactory = serverProvider.GetRequiredService<INetStateFactory>();
    }

    public INetManager Server { get; }

    public INetManager Client { get; }

    public INetConnection ClientConnection { get; }

    public INetStateFactory ServerStateFactory { get; }

    public INetConnection ServerSideConnection => Server.ActiveConnections.Single();

    public static async Task<ConnectedPair> Create(TimeSpan? pingInterval = null)
    {
        (ServiceProvider serverProvider, INetManager server) = TestHarness.CreateManager();
        (ServiceProvider clientProvider, INetManager client) = TestHarness.CreateManager();

        if (pingInterval is { } interval)
        {
            server.PingInterval = interval;
            client.PingInterval = interval;
        }

        server.Host(0);
        int port = server.HostPort!.Value;

        INetConnection clientConnection = client.Connect("127.0.0.1", port);
        await TestHarness.WaitUntil(() => clientConnection.Status == NetStatus.Connected);
        await TestHarness.WaitUntil(() => server.ActiveConnections.Count > 0);

        return new ConnectedPair(serverProvider, server, clientProvider, client, clientConnection);
    }

    public async Task<INetRemoteEntity<ITestModel, ITestController>> ListenThenAdd(TestEntity entity)
    {
        TaskCompletionSource<INetRemoteEntity<ITestModel, ITestController>> seen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable listenHandle = Client.Listen<ITestModel, ITestController>(e => seen.TrySetResult(e));
        Server.All.Add<ITestModel, ITestController>(entity);
        return await TestHarness.WaitForTask(seen.Task);
    }

    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync();
        await Client.DisposeAsync();
        await serverProvider.DisposeAsync();
        await clientProvider.DisposeAsync();
    }
}
