namespace Markwardt.NetChannel.Tests;

public interface IBaseModel
{
    int Inherited { get; set; }
}

public interface IInheritingModel : IBaseModel
{
    int Own { get; set; }
}

public interface IBaseController
{
    Task InheritedCall();
}

public interface IInheritingController : IBaseController
{
    Task OwnCall();
}

public interface IModelWithAMethodOnItsBase : IBaseController
{
    int Value { get; set; }
}

public interface IOverloadedController
{
    void Overloaded();

    void Overloaded(int value);
}

public sealed class InheritingEntity(INetState<IInheritingModel> state)
    : NetEntity<IInheritingModel, IInheritingController>(state), IInheritingController
{
    public Task InheritedCall() => Task.CompletedTask;

    public Task OwnCall() => Task.CompletedTask;
}

public sealed class NetInterfaceInheritanceTests
{
    private readonly NetEntityTypeCache cache = new();

    [Fact]
    public void Model_IncludesPropertiesDeclaredOnBaseInterfaces()
    {
        NetModelType model = cache.GetModel(typeof(IInheritingModel));

        Assert.Equal(
            [nameof(IBaseModel.Inherited), nameof(IInheritingModel.Own)],
            model.Properties.Select(property => property.Name));
    }

    [Fact]
    public void Controller_IncludesMethodsDeclaredOnBaseInterfaces()
    {
        NetControllerType controller = cache.GetController(typeof(IInheritingController));

        Assert.Equal(
            [nameof(IBaseController.InheritedCall), nameof(IInheritingController.OwnCall)],
            controller.Methods.Select(method => method.Name));
    }

    [Fact]
    public void Model_WithAMethodOnABaseInterface_IsRejected()
    {
        NetInvalidInterfaceException exception = Assert.Throws<NetInvalidInterfaceException>(() => cache.GetModel(typeof(IModelWithAMethodOnItsBase)));
        Assert.Contains("must contain only properties", exception.Message);
    }

    [Fact]
    public void Controller_WithAPropertyOnItself_IsRejected()
    {
        NetInvalidInterfaceException exception = Assert.Throws<NetInvalidInterfaceException>(() => cache.GetController(typeof(IModelWithAMethodOnItsBase)));
        Assert.Contains("must contain only methods", exception.Message);
    }

    [Fact]
    public void Controller_WithOverloadedMethods_IsRejected()
    {
        NetInvalidInterfaceException exception = Assert.Throws<NetInvalidInterfaceException>(() => cache.GetController(typeof(IOverloadedController)));
        Assert.Contains("same name", exception.Message);
    }

    [Fact]
    public void MemberIds_AreAssignedInANameOrderIndependentOfReflectionOrder()
    {
        NetModelType model = cache.GetModel(typeof(ITestModel));

        Assert.Equal(model.Properties.Select(property => property.Name).Order(StringComparer.Ordinal), model.Properties.Select(property => property.Name));
        Assert.Equal(model.Properties.Select((_, index) => (uint)index), model.Properties.Select(property => property.Id));
    }

    [Fact]
    public void State_ExposesInheritedPropertiesAsSyncable()
    {
        NetState<IInheritingModel> state = new(cache.GetModel(typeof(IInheritingModel)));

        state.Model.Inherited = 4;

        Assert.Contains(nameof(IBaseModel.Inherited), state.Properties);
        Assert.Equal(4, state.Get(nameof(IBaseModel.Inherited)));
    }
}
