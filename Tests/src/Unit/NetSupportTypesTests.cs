namespace Markwardt.NetChannel.Tests;

public sealed class NetPositionTests
{
    [Fact]
    public void ImplicitConversion_FromVector3_LeavesRangeNull()
    {
        Vector3 vector = new(1, 2, 3);
        NetPosition position = vector;

        Assert.Equal(vector, position.Position);
        Assert.Null(position.Range);
    }

    [Fact]
    public void RecordEquality_ComparesByValue()
    {
        NetPosition a = new(new Vector3(1, 2, 3), new NetRange(1, 2));
        NetPosition b = new(new Vector3(1, 2, 3), new NetRange(1, 2));

        Assert.Equal(a, b);
    }
}

public sealed class NetEntityTests
{
    [Fact]
    public void Destroy_FiresDestroyedAndSetsIsDestroyed()
    {
        NetModelType modelType = new NetEntityTypeCache().GetModel(typeof(ITestModel));
        TestEntity entity = new(new NetState<ITestModel>(modelType));

        List<INetEntity> notifications = [];
        using IDisposable subscription = entity.Destroyed.Subscribe(notifications.Add);

        Assert.False(entity.IsDestroyed);

        entity.Destroy();

        Assert.True(entity.IsDestroyed);
        Assert.Single(notifications);
        Assert.Same(entity, notifications[0]);
    }

    [Fact]
    public void Destroy_CalledTwice_OnlyNotifiesOnce()
    {
        NetModelType modelType = new NetEntityTypeCache().GetModel(typeof(ITestModel));
        TestEntity entity = new(new NetState<ITestModel>(modelType));

        int count = 0;
        using IDisposable subscription = entity.Destroyed.Subscribe(_ => count++);

        entity.Destroy();
        entity.Destroy();

        Assert.Equal(1, count);
    }

    [Fact]
    public void NonGenericState_ReturnsSameInstanceAsTypedState()
    {
        NetModelType modelType = new NetEntityTypeCache().GetModel(typeof(ITestModel));
        TestEntity entity = new(new NetState<ITestModel>(modelType));

        INetEntity nonGeneric = entity;
        Assert.Same(entity.State, nonGeneric.State);
    }
}

public sealed class NetExceptionTests
{
    [Fact]
    public void NetFailedException_CarriesMessage()
    {
        NetFailedException exception = new("failure reason");
        Assert.Equal("failure reason", exception.Message);
    }

    [Fact]
    public void NetInvalidInterfaceException_CarriesMessage()
    {
        NetInvalidInterfaceException exception = new("invalid reason");
        Assert.Equal("invalid reason", exception.Message);
    }
}

public sealed class NetAttributeTests
{
    [Fact]
    public void NetAccessAttribute_StoresHasAccess()
    {
        Assert.True(new NetAccessAttribute(true).HasAccess);
        Assert.False(new NetAccessAttribute(false).HasAccess);
    }

    [Fact]
    public void NetRoleAttribute_StoresRole()
    {
        Assert.Equal("admin", new NetRoleAttribute("admin").Role);
    }
}
