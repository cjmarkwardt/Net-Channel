namespace Markwardt.NetChannel.Tests;

public sealed class NetLivenessTests
{
    [Fact]
    public async Task AnIdleConnection_KeepsPingingIndefinitely()
    {
        await using ConnectedPair pair = await ConnectedPair.Create(pingInterval: TimeSpan.FromMilliseconds(40));
        pair.Server.DisconnectTimeout = TimeSpan.FromMilliseconds(400);
        pair.Client.DisconnectTimeout = TimeSpan.FromMilliseconds(400);

        // Far longer than the disconnect timeout: if the heartbeat ever stalls, the idle connection is dropped.
        await Task.Delay(2000);

        Assert.Equal(NetStatus.Connected, pair.ClientConnection.Status);
        Assert.Single(pair.Server.ActiveConnections);
        Assert.Single(pair.Client.ActiveConnections);
    }

    [Fact]
    public async Task AnIdleConnection_KeepsMeasuringLatency()
    {
        await using ConnectedPair pair = await ConnectedPair.Create(pingInterval: TimeSpan.FromMilliseconds(40));

        await TestHarness.WaitUntil(() => pair.ClientConnection.Latency > TimeSpan.Zero);
    }

    [Fact]
    public async Task ManyConnections_AllKeepTheirHeartbeatGoing()
    {
        (ServiceProvider serverProvider, INetManager server) = TestHarness.CreateManager();
        await using ServiceProvider disposeServerProvider = serverProvider;
        await using INetManager disposeServer = server;

        server.PingInterval = TimeSpan.FromMilliseconds(40);
        server.DisconnectTimeout = TimeSpan.FromMilliseconds(500);
        server.Host(0);
        int port = server.HostPort!.Value;

        List<ServiceProvider> providers = [];
        List<INetManager> clients = [];

        try
        {
            for (int i = 0; i < 4; i++)
            {
                (ServiceProvider provider, INetManager client) = TestHarness.CreateManager();
                client.PingInterval = TimeSpan.FromMilliseconds(40);
                client.DisconnectTimeout = TimeSpan.FromMilliseconds(500);
                providers.Add(provider);
                clients.Add(client);
                await TestHarness.WaitForTask(client.Connect("127.0.0.1", port).Wait());
            }

            await TestHarness.WaitUntil(() => server.ActiveConnections.Count == 4);
            await Task.Delay(1500);

            Assert.Equal(4, server.ActiveConnections.Count);
            Assert.All(clients, client => Assert.Single(client.ActiveConnections));
        }
        finally
        {
            foreach (INetManager client in clients)
            {
                await client.DisposeAsync();
            }

            foreach (ServiceProvider provider in providers)
            {
                await provider.DisposeAsync();
            }
        }
    }
}
