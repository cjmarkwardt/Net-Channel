namespace Markwardt.NetChannel.Tests;

public sealed class NetStateTests
{
    private readonly NetModelType modelType = new NetEntityTypeCache().GetModel(typeof(IValidModel));

    [Fact]
    public void Get_UnsetValueTypeProperty_ReturnsDefault()
    {
        NetState<IValidModel> state = new(modelType);
        Assert.Equal(0, state.Get("Plain"));
    }

    [Fact]
    public void Get_ThenSet_RoundTrips()
    {
        NetState<IValidModel> state = new(modelType);
        state.Set("Plain", 5);
        Assert.Equal(5, state.Get("Plain"));
    }

    [Fact]
    public void Model_Get_ReflectsUnderlyingState()
    {
        NetState<IValidModel> state = new(modelType);
        state.Set("Plain", 9);
        Assert.Equal(9, state.Model.Plain);
    }

    [Fact]
    public void Model_Set_UpdatesUnderlyingState()
    {
        NetState<IValidModel> state = new(modelType);
        state.Model.Plain = 12;
        Assert.Equal(12, state.Get("Plain"));
    }

    [Fact]
    public void Observe_ByName_FiresOnChange()
    {
        NetState<IValidModel> state = new(modelType);
        List<object?> values = [];
        using IDisposable subscription = state.Observe("Plain").Subscribe(values.Add);

        state.Set("Plain", 1);
        state.Set("Plain", 2);

        Assert.Equal([1, 2], values);
    }

    [Fact]
    public void Observe_Generic_FiresOnChange()
    {
        NetState<IValidModel> state = new(modelType);
        List<int> values = [];
        using IDisposable subscription = state.Observe(x => x.Plain).Subscribe(values.Add);

        state.Model.Plain = 3;

        Assert.Equal([3], values);
    }

    [Fact]
    public void Observe_Generic_WithNonMemberExpression_Throws()
    {
        NetState<IValidModel> state = new(modelType);
        Assert.Throws<ArgumentException>(() => state.Observe(x => x.Plain + 1));
    }

    [Fact]
    public void Observe_Generic_WithBoxingConversion_UnwrapsUnaryExpression()
    {
        NetState<IValidModel> state = new(modelType);
        List<object> values = [];
        using IDisposable subscription = state.Observe<object>(x => x.Plain).Subscribe(values.Add);

        state.Model.Plain = 7;

        Assert.Equal([7], values);
    }

    [Fact]
    public void Properties_ContainsEveryModelProperty()
    {
        NetState<IValidModel> state = new(modelType);
        Assert.Equal(3, state.Properties.Count);
        Assert.Contains("Plain", state.Properties);
        Assert.Contains("Open", state.Properties);
        Assert.Contains("RoleGated", state.Properties);
    }
}
