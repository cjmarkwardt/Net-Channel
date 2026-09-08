namespace Markwardt.NetChannel.Tests;

public sealed class NetIdentityValidationTests
{
    [Fact]
    public void Host_WithAWrongLengthIdentity_ThrowsRatherThanFailingSilentlyLater()
    {
        (ServiceProvider provider, INetManager manager) = TestHarness.CreateManager();
        using ServiceProvider disposeProvider = provider;
        using INetManager disposeManager = manager;

        ArgumentException exception = Assert.Throws<ArgumentException>(() => manager.Host(0, new byte[7]));

        Assert.Equal("identity", exception.ParamName);
        Assert.Null(manager.HostPort);
    }

    [Fact]
    public void Host_WithNoIdentity_IsAccepted()
    {
        (ServiceProvider provider, INetManager manager) = TestHarness.CreateManager();
        using ServiceProvider disposeProvider = provider;
        using INetManager disposeManager = manager;

        manager.Host(0);

        Assert.NotNull(manager.HostPort);
    }

    [Fact]
    public void Host_WithAValidLengthIdentity_IsAccepted()
    {
        (ServiceProvider provider, INetManager manager) = TestHarness.CreateManager();
        using ServiceProvider disposeProvider = provider;
        using INetManager disposeManager = manager;

        manager.Host(0, new byte[32]);

        Assert.NotNull(manager.HostPort);
    }

    [Fact]
    public void Connect_WithAWrongLengthExpectedIdentity_Throws()
    {
        (ServiceProvider provider, INetManager manager) = TestHarness.CreateManager();
        using ServiceProvider disposeProvider = provider;
        using INetManager disposeManager = manager;

        ArgumentException exception = Assert.Throws<ArgumentException>(() => manager.Connect("127.0.0.1", 1, null, new byte[7]));

        Assert.Equal("expectedIdentity", exception.ParamName);
        Assert.Empty(manager.PendingConnections);
    }
}
