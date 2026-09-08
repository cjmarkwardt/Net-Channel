namespace Markwardt.NetChannel.Tests;

public sealed class NetMemberResolutionTests
{
    private readonly NetEntityTypeCache typeCache = new();

    [Fact]
    public void ResolveProperty_WithNoNameLearned_FallsBackToThePositionalId()
    {
        NetModelType modelType = typeCache.GetModel(typeof(ITestModel));
        ConcurrentDictionary<(uint ModelId, uint PropertyId), string> names = new();

        NetModelMember? resolved = NetManager.ResolveProperty(names, 0, 1, modelType);

        Assert.Equal(modelType.Properties[1].Name, resolved?.Name);
    }

    [Fact]
    public void ResolveProperty_PrefersTheLearnedNameOverThePositionalId()
    {
        NetModelType modelType = typeCache.GetModel(typeof(ITestModel));
        string lastPropertyName = modelType.Properties[^1].Name;
        ConcurrentDictionary<(uint ModelId, uint PropertyId), string> names = new();
        names[(0, 0)] = lastPropertyName;

        NetModelMember? resolved = NetManager.ResolveProperty(names, 0, 0, modelType);

        Assert.Equal(lastPropertyName, resolved?.Name);
        Assert.NotEqual(modelType.Properties[0].Name, resolved?.Name);
    }

    [Fact]
    public void ResolveProperty_WithANameThisSideDoesNotDeclare_ReturnsNull()
    {
        NetModelType modelType = typeCache.GetModel(typeof(ITestModel));
        ConcurrentDictionary<(uint ModelId, uint PropertyId), string> names = new();
        names[(0, 0)] = "PropertyAddedInALaterVersion";

        Assert.Null(NetManager.ResolveProperty(names, 0, 0, modelType));
    }

    [Fact]
    public void ResolveProperty_WithAnOutOfRangeIdAndNoName_ReturnsNull()
    {
        NetModelType modelType = typeCache.GetModel(typeof(ITestModel));
        ConcurrentDictionary<(uint ModelId, uint PropertyId), string> names = new();

        Assert.Null(NetManager.ResolveProperty(names, 0, 999, modelType));
    }

    [Fact]
    public void ResolveProperty_ScopesLearnedNamesToTheirModelId()
    {
        NetModelType modelType = typeCache.GetModel(typeof(ITestModel));
        ConcurrentDictionary<(uint ModelId, uint PropertyId), string> names = new();
        names[(7, 0)] = modelType.Properties[^1].Name;

        NetModelMember? resolved = NetManager.ResolveProperty(names, 0, 0, modelType);

        Assert.Equal(modelType.Properties[0].Name, resolved?.Name);
    }

    [Fact]
    public void ResolveMethod_PrefersTheLearnedNameOverThePositionalId()
    {
        (ServiceProvider provider, INetManager manager) = TestHarness.CreateManager();
        using ServiceProvider disposeProvider = provider;
        using INetManager disposeManager = manager;

        NetControllerType controllerType = typeCache.GetController(typeof(ITestController));
        string lastMethodName = controllerType.Methods[^1].Name;
        NetConnection connection = new((NetManager)manager, NetDirection.Incoming, "127.0.0.1", 1, null);
        connection.IncomingMethodNames[(0, 0)] = lastMethodName;

        NetControllerMember? resolved = NetManager.ResolveMethod(connection, 0, 0, controllerType);

        Assert.Equal(lastMethodName, resolved?.Name);
    }

    [Fact]
    public void ResolveMethod_WithAMethodThisSideDoesNotDeclare_ReturnsNull()
    {
        (ServiceProvider provider, INetManager manager) = TestHarness.CreateManager();
        using ServiceProvider disposeProvider = provider;
        using INetManager disposeManager = manager;

        NetControllerType controllerType = typeCache.GetController(typeof(ITestController));
        NetConnection connection = new((NetManager)manager, NetDirection.Incoming, "127.0.0.1", 1, null);
        connection.IncomingMethodNames[(0, 0)] = "MethodAddedInALaterVersion";

        Assert.Null(NetManager.ResolveMethod(connection, 0, 0, controllerType));
    }

    [Fact]
    public void ResolveMethod_WithAnOutOfRangeIdAndNoName_ReturnsNull()
    {
        (ServiceProvider provider, INetManager manager) = TestHarness.CreateManager();
        using ServiceProvider disposeProvider = provider;
        using INetManager disposeManager = manager;

        NetControllerType controllerType = typeCache.GetController(typeof(ITestController));
        NetConnection connection = new((NetManager)manager, NetDirection.Incoming, "127.0.0.1", 1, null);

        Assert.Null(NetManager.ResolveMethod(connection, 0, 999, controllerType));
    }
}
