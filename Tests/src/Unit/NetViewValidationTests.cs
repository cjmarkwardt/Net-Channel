namespace Markwardt.NetChannel.Tests;

public sealed class NetViewValidationTests
{
    [Fact]
    public void AddViewers_WithAForeignConnection_ThrowsArgumentException()
    {
        using ViewFixture fixture = new();

        ArgumentException exception = Assert.Throws<ArgumentException>(() => fixture.View.AddViewers(Mock.Of<INetConnection>()));

        Assert.Equal("connection", exception.ParamName);
        Assert.Empty(fixture.View.Viewers);
    }

    [Fact]
    public void RemoveViewers_WithAForeignConnection_ThrowsArgumentException()
    {
        using ViewFixture fixture = new();

        Assert.Throws<ArgumentException>(() => fixture.View.RemoveViewers(Mock.Of<INetConnection>()));
    }

    [Fact]
    public void SetViewerPosition_WithAForeignConnection_ThrowsArgumentException()
    {
        using ViewFixture fixture = new();

        Assert.Throws<ArgumentException>(() => fixture.View.SetViewerPosition(Mock.Of<INetConnection>(), new NetPosition(Vector3.Zero)));
    }

    [Fact]
    public void GetViewerPosition_WithAForeignConnection_ThrowsArgumentException()
    {
        using ViewFixture fixture = new();

        Assert.Throws<ArgumentException>(() => fixture.View.GetViewerPosition(Mock.Of<INetConnection>()));
    }

    private sealed class ViewFixture : IDisposable
    {
        private readonly ServiceProvider provider;

        private readonly INetManager manager;

        public ViewFixture()
        {
            (provider, manager) = TestHarness.CreateManager();
            View = manager.CreateView();
        }

        public INetView View { get; }

        public void Dispose()
        {
            manager.Dispose();
            provider.Dispose();
        }
    }
}
