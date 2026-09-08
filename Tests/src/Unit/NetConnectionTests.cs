namespace Markwardt.NetChannel.Tests;

public sealed class NetConnectionTests
{
    [Fact]
    public void Wait_AlreadyConnected_CompletesImmediately()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        using ServiceProvider disposeProvider = provider;
        using NetManager disposeManager = manager;

        NetConnection connection = new(manager, NetDirection.Outgoing, "127.0.0.1", 1234, null);
        connection.SetStatus(NetStatus.Connected);

        Task waitTask = connection.Wait();
        Assert.True(waitTask.IsCompletedSuccessfully);
    }

    [Fact]
    public void Wait_AlreadyDisconnected_FaultsImmediately()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        using ServiceProvider disposeProvider = provider;
        using NetManager disposeManager = manager;

        NetConnection connection = new(manager, NetDirection.Outgoing, "127.0.0.1", 1234, null);
        connection.SetStatus(NetStatus.Disconnected);

        Task waitTask = connection.Wait();
        Assert.True(waitTask.IsFaulted);
    }

    [Fact]
    public void Wait_WhileConnecting_ResolvesFaultedWhenRejectedFires()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        using ServiceProvider disposeProvider = provider;
        using NetManager disposeManager = manager;

        NetConnection connection = new(manager, NetDirection.Outgoing, "127.0.0.1", 1234, null);
        Task waitTask = connection.Wait();
        Assert.False(waitTask.IsCompleted);

        manager.DropConnection(connection, new NetFailedException("simulated rejection"));

        Assert.True(waitTask.IsFaulted);
    }

    [Fact]
    public void Wait_WhileConnecting_IgnoresRejectionForADifferentConnection()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        using ServiceProvider disposeProvider = provider;
        using NetManager disposeManager = manager;

        NetConnection connection = new(manager, NetDirection.Outgoing, "127.0.0.1", 1234, null);
        NetConnection other = new(manager, NetDirection.Outgoing, "127.0.0.1", 5678, null);

        Task waitTask = connection.Wait();
        manager.DropConnection(other, new NetFailedException("unrelated failure"));

        Assert.False(waitTask.IsCompleted);
    }

    [Fact]
    public void Promote_AddsRole()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        using ServiceProvider disposeProvider = provider;
        using NetManager disposeManager = manager;

        NetConnection connection = new(manager, NetDirection.Outgoing, "127.0.0.1", 1234, null);
        connection.Promote("admin");

        Assert.Contains("admin", connection.Roles);
    }

    [Fact]
    public void Demote_RemovesRole()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        using ServiceProvider disposeProvider = provider;
        using NetManager disposeManager = manager;

        NetConnection connection = new(manager, NetDirection.Outgoing, "127.0.0.1", 1234, null);
        connection.Promote("admin");
        connection.Demote("admin");

        Assert.DoesNotContain("admin", connection.Roles);
    }

    [Fact]
    public void Demote_UnknownRole_IsNoOp()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        using ServiceProvider disposeProvider = provider;
        using NetManager disposeManager = manager;

        NetConnection connection = new(manager, NetDirection.Outgoing, "127.0.0.1", 1234, null);
        connection.Demote("admin");

        Assert.Empty(connection.Roles);
    }

    [Fact]
    public void Position_RoundTrips_WithNoEntitiesRegistered()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        using ServiceProvider disposeProvider = provider;
        using NetManager disposeManager = manager;

        NetConnection connection = new(manager, NetDirection.Outgoing, "127.0.0.1", 1234, null);
        Assert.Null(connection.Position);

        NetPosition position = new(Vector3.Zero);
        connection.Position = position;

        Assert.Equal(position, connection.Position);
    }

    private static (ServiceProvider Provider, NetManager Manager) CreateManager()
    {
        (ServiceProvider provider, INetManager manager) = TestHarness.CreateManager();
        return (provider, (NetManager)manager);
    }
}
