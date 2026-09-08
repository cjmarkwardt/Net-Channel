namespace Markwardt.NetChannel.Tests;

public sealed class NetGroupTests
{
    [Fact]
    public async Task All_Add_MakesEntityAppearInEntities()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        await using ServiceProvider disposeProvider = provider;
        await using NetManager disposeManager = manager;

        TestEntity entity = new(provider.GetRequiredService<INetStateFactory>().Create<ITestModel>());

        manager.All.Add<ITestModel, ITestController>(entity);
        Assert.Contains(entity, manager.All.Entities);
    }

    [Fact]
    public async Task All_Remove_RemovesEntityFromEntities()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        await using ServiceProvider disposeProvider = provider;
        await using NetManager disposeManager = manager;

        TestEntity entity = new(provider.GetRequiredService<INetStateFactory>().Create<ITestModel>());
        manager.All.Add<ITestModel, ITestController>(entity);

        manager.All.Remove(entity);
        Assert.DoesNotContain(entity, manager.All.Entities);
    }

    [Fact]
    public async Task All_Remove_UnknownEntity_IsNoOp()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        await using ServiceProvider disposeProvider = provider;
        await using NetManager disposeManager = manager;

        TestEntity entity = new(provider.GetRequiredService<INetStateFactory>().Create<ITestModel>());

        manager.All.Remove(entity);
        Assert.DoesNotContain(entity, manager.All.Entities);
    }

    [Fact]
    public async Task View_AddThenRemoveViewers_UpdatesViewers()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        await using ServiceProvider disposeProvider = provider;
        await using NetManager disposeManager = manager;

        NetConnection connection = new(manager, NetDirection.Outgoing, "127.0.0.1", 1234, null);
        INetView view = manager.CreateView();

        view.AddViewers(connection);
        Assert.Contains(connection, view.Viewers);

        view.RemoveViewers(connection);
        Assert.DoesNotContain(connection, view.Viewers);
    }

    [Fact]
    public async Task View_SetAndGetViewerPosition_RoundTrips()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        await using ServiceProvider disposeProvider = provider;
        await using NetManager disposeManager = manager;

        NetConnection connection = new(manager, NetDirection.Outgoing, "127.0.0.1", 1234, null);
        INetView view = manager.CreateView();

        Assert.Null(view.GetViewerPosition(connection));

        NetPosition position = new(Vector3.Zero);
        view.SetViewerPosition(connection, position);
        Assert.Equal(position, view.GetViewerPosition(connection));

        view.SetViewerPosition(connection, null);
        Assert.Null(view.GetViewerPosition(connection));
    }

    [Fact]
    public async Task View_RemoveViewerSilently_RemovesWithoutError()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        await using ServiceProvider disposeProvider = provider;
        await using NetManager disposeManager = manager;

        NetConnection connection = new(manager, NetDirection.Outgoing, "127.0.0.1", 1234, null);
        NetView view = (NetView)manager.CreateView();
        view.AddViewers(connection);

        view.RemoveViewerSilently(connection);

        Assert.DoesNotContain(connection, view.Viewers);
    }

    [Fact]
    public async Task View_GetAudienceSnapshot_ReturnsAddedViewers()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        await using ServiceProvider disposeProvider = provider;
        await using NetManager disposeManager = manager;

        NetConnection connection = new(manager, NetDirection.Outgoing, "127.0.0.1", 1234, null);
        NetView view = (NetView)manager.CreateView();
        view.AddViewers(connection);

        Assert.Contains(connection, view.GetAudienceSnapshot());
    }

    [Fact]
    public async Task View_GetEffectivePosition_UsesOverrideWhenSet()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        await using ServiceProvider disposeProvider = provider;
        await using NetManager disposeManager = manager;

        NetConnection connection = new(manager, NetDirection.Outgoing, "127.0.0.1", 1234, null)
        {
            Position = new NetPosition(new Vector3(1, 0, 0)),
        };

        NetView view = (NetView)manager.CreateView();
        Assert.Equal(connection.Position, view.GetEffectivePosition(connection));

        NetPosition overridePosition = new(new Vector3(9, 0, 0));
        view.SetViewerPosition(connection, overridePosition);

        Assert.Equal(overridePosition, view.GetEffectivePosition(connection));
    }

    [Fact]
    public async Task View_Destroy_RemovesItFromManagerViews()
    {
        (ServiceProvider provider, NetManager manager) = CreateManager();
        await using ServiceProvider disposeProvider = provider;
        await using NetManager disposeManager = manager;

        INetView view = manager.CreateView("tagged");
        view.Destroy();

        Assert.DoesNotContain(view, manager.Views);
        Assert.Null(manager.GetView("tagged"));
    }

    private static (ServiceProvider Provider, NetManager Manager) CreateManager()
    {
        (ServiceProvider provider, INetManager manager) = TestHarness.CreateManager();
        return (provider, (NetManager)manager);
    }

    [Fact]
    public void Add_CalledTwiceForTheSameEntity_IsUndoneByASingleRemove()
    {
        (ServiceProvider provider, INetManager manager) = TestHarness.CreateManager();
        using ServiceProvider disposeProvider = provider;
        using INetManager disposeManager = manager;

        INetStateFactory factory = provider.GetRequiredService<INetStateFactory>();
        TestEntity entity = new(factory.Create<ITestModel>());

        manager.All.Add<ITestModel, ITestController>(entity);
        manager.All.Add<ITestModel, ITestController>(entity);
        manager.All.Remove(entity);

        Assert.DoesNotContain(entity, manager.All.Entities);
        Assert.DoesNotContain(entity, ((NetManager)manager).TrackedEntities);
    }

    [Fact]
    public void Add_ToTwoDifferentGroups_StillNeedsBothRemoved()
    {
        (ServiceProvider provider, INetManager manager) = TestHarness.CreateManager();
        using ServiceProvider disposeProvider = provider;
        using INetManager disposeManager = manager;

        INetStateFactory factory = provider.GetRequiredService<INetStateFactory>();
        TestEntity entity = new(factory.Create<ITestModel>());
        INetView view = manager.CreateView();

        manager.All.Add<ITestModel, ITestController>(entity);
        view.Add<ITestModel, ITestController>(entity);
        manager.All.Remove(entity);

        Assert.Contains(entity, ((NetManager)manager).TrackedEntities);

        view.Remove(entity);

        Assert.DoesNotContain(entity, ((NetManager)manager).TrackedEntities);
    }

    [Fact]
    public void View_Destroy_EmptiesItsAudience()
    {
        (ServiceProvider provider, INetManager manager) = TestHarness.CreateManager();
        using ServiceProvider disposeProvider = provider;
        using INetManager disposeManager = manager;

        NetConnection connection = new((NetManager)manager, NetDirection.Outgoing, "127.0.0.1", 1, null);
        INetView view = manager.CreateView();
        view.AddViewers(connection);

        view.Destroy();

        Assert.Empty(view.Viewers);
    }
}
