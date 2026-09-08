namespace Markwardt.NetChannel.Tests;

public sealed class NetAddressValidationTests
{
    [Fact]
    public void Host_WithAnIPv6Address_ThrowsRatherThanFailingAtTheSocketLayer()
    {
        using ManagerFixture fixture = new();

        ArgumentException exception = Assert.Throws<ArgumentException>(() => fixture.Manager.Host(0, null, "::1"));

        Assert.Equal("host", exception.ParamName);
        Assert.Null(fixture.Manager.HostPort);
    }

    [Fact]
    public void Host_WithSomethingThatIsNotAnAddress_Throws()
    {
        using ManagerFixture fixture = new();

        Assert.Throws<ArgumentException>(() => fixture.Manager.Host(0, null, "not-an-address"));
    }

    [Fact]
    public void Host_WithAnIPv4Address_IsAccepted()
    {
        using ManagerFixture fixture = new();

        fixture.Manager.Host(0, null, "127.0.0.1");

        Assert.NotNull(fixture.Manager.HostPort);
    }

    [Fact]
    public void Connect_ToAnIPv6Address_Throws()
    {
        using ManagerFixture fixture = new();

        ArgumentException exception = Assert.Throws<ArgumentException>(() => fixture.Manager.Connect("::1", 1));

        Assert.Equal("host", exception.ParamName);
    }

    [Fact]
    public void Connect_ToAResolvableHostName_IsAccepted()
    {
        using ManagerFixture fixture = new();

        INetConnection connection = fixture.Manager.Connect("localhost", 1);

        Assert.Equal(NetStatus.Connecting, connection.Status);
    }

    private sealed class ManagerFixture : IDisposable
    {
        private readonly ServiceProvider provider;

        public ManagerFixture() => (provider, Manager) = TestHarness.CreateManager();

        public INetManager Manager { get; }

        public void Dispose()
        {
            Manager.Dispose();
            provider.Dispose();
        }
    }
}
