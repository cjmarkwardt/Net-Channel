namespace Markwardt.NetChannel.Tests;

public interface IValidModel
{
    int Plain { get; set; }

    [NetAccess(true)]
    int Open { get; set; }

    [NetRole("admin")]
    int RoleGated { get; set; }
}

public interface IModelWithMethod
{
    int Value { get; set; }

    void DoSomething();
}

public interface IModelWithGetOnlyProperty
{
    int Value { get; }
}

public interface IModelWithSecureAccessor
{
    int Value { [NetSecure] get; set; }
}

public interface IModelWithAccessOnAccessor
{
    int Value { get; [NetAccess(true)] set; }
}

public interface IModelWithRoleOnAccessor
{
    int Value { get; [NetRole("admin")] set; }
}

public interface IModelWithWrongPositionType
{
    [NetPosition]
    int Value { get; set; }
}

public interface IModelWithTooManyPositions
{
    [NetPosition]
    NetPosition? First { get; set; }

    [NetPosition]
    NetPosition? Second { get; set; }
}

public interface IValidController
{
    void VoidMethod();

    Task TaskMethod();

    Task<int> TaskOfTMethod();

    [NetAccess(false)]
    Task RestrictedMethod();

    [NetRole("admin")]
    Task RoleGatedMethod();

    [NetSecure]
    Task<int> SecureMethod(int value);
}

public interface IControllerWithProperty
{
    int Value { get; set; }
}

public interface IControllerWithInvalidReturnType
{
    int NotAllowed();
}

public sealed class NetEntityTypeCacheTests
{
    private readonly NetEntityTypeCache cache = new();

    [Fact]
    public void GetModel_ValidInterface_AssignsSequentialIds()
    {
        NetModelType model = cache.GetModel(typeof(IValidModel));

        Assert.Equal(3, model.Properties.Count);
        Assert.Equal([0u, 1u, 2u], model.Properties.Select(p => p.Id));
        Assert.Contains(model.Properties, p => p.Name == "Open" && p.HasAccess);
        Assert.Contains(model.Properties, p => p.Name == "Plain" && !p.HasAccess);
        Assert.Contains(model.Properties, p => p.Name == "RoleGated" && p.Role == "admin");
    }

    [Fact]
    public void GetModel_IsCachedAcrossCalls()
    {
        NetModelType first = cache.GetModel(typeof(IValidModel));
        NetModelType second = cache.GetModel(typeof(IValidModel));
        Assert.Same(first, second);
    }

    [Fact]
    public void GetModel_WithMethod_Throws() =>
        Assert.Throws<NetInvalidInterfaceException>(() => cache.GetModel(typeof(IModelWithMethod)));

    [Fact]
    public void GetModel_WithGetOnlyProperty_Throws() =>
        Assert.Throws<NetInvalidInterfaceException>(() => cache.GetModel(typeof(IModelWithGetOnlyProperty)));

    [Fact]
    public void GetModel_WithSecureAccessor_Throws() =>
        Assert.Throws<NetInvalidInterfaceException>(() => cache.GetModel(typeof(IModelWithSecureAccessor)));

    [Fact]
    public void GetModel_WithAccessOnAccessor_Throws() =>
        Assert.Throws<NetInvalidInterfaceException>(() => cache.GetModel(typeof(IModelWithAccessOnAccessor)));

    [Fact]
    public void GetModel_WithRoleOnAccessor_Throws() =>
        Assert.Throws<NetInvalidInterfaceException>(() => cache.GetModel(typeof(IModelWithRoleOnAccessor)));

    [Fact]
    public void GetModel_WithWrongPositionType_Throws() =>
        Assert.Throws<NetInvalidInterfaceException>(() => cache.GetModel(typeof(IModelWithWrongPositionType)));

    [Fact]
    public void GetModel_WithTooManyPositions_Throws() =>
        Assert.Throws<NetInvalidInterfaceException>(() => cache.GetModel(typeof(IModelWithTooManyPositions)));

    [Fact]
    public void GetController_ValidInterface_AssignsSequentialIdsAndFlags()
    {
        NetControllerType controller = cache.GetController(typeof(IValidController));

        Assert.Equal(6, controller.Methods.Count);
        Assert.Equal(Enumerable.Range(0, 6).Select(i => (uint)i), controller.Methods.Select(m => m.Id));

        NetControllerMember taskOfT = controller.Methods.Single(m => m.Name == "TaskOfTMethod");
        Assert.True(taskOfT.IsTask);
        Assert.Equal(typeof(int), taskOfT.ResultType);

        NetControllerMember voidMethod = controller.Methods.Single(m => m.Name == "VoidMethod");
        Assert.False(voidMethod.IsTask);
        Assert.Null(voidMethod.ResultType);
        Assert.True(voidMethod.HasAccess);

        NetControllerMember restricted = controller.Methods.Single(m => m.Name == "RestrictedMethod");
        Assert.False(restricted.HasAccess);

        NetControllerMember roleGated = controller.Methods.Single(m => m.Name == "RoleGatedMethod");
        Assert.Equal("admin", roleGated.Role);

        NetControllerMember secure = controller.Methods.Single(m => m.Name == "SecureMethod");
        Assert.True(secure.IsSecure);
    }

    [Fact]
    public void GetController_IsCachedAcrossCalls()
    {
        NetControllerType first = cache.GetController(typeof(IValidController));
        NetControllerType second = cache.GetController(typeof(IValidController));
        Assert.Same(first, second);
    }

    [Fact]
    public void GetController_WithProperty_Throws() =>
        Assert.Throws<NetInvalidInterfaceException>(() => cache.GetController(typeof(IControllerWithProperty)));

    [Fact]
    public void GetController_WithInvalidReturnType_Throws() =>
        Assert.Throws<NetInvalidInterfaceException>(() => cache.GetController(typeof(IControllerWithInvalidReturnType)));
}
