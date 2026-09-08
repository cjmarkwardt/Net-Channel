namespace Markwardt.NetChannel.Tests;

public sealed class NetStateValidationTests
{
    private readonly NetState<ITestModel> state = new(new NetEntityTypeCache().GetModel(typeof(ITestModel)));

    [Fact]
    public void Get_WithAPropertyNotOnTheModel_ThrowsArgumentException()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => state.Get("NotAProperty"));
        Assert.Contains("NotAProperty", exception.Message);
    }

    [Fact]
    public void Set_WithAPropertyNotOnTheModel_ThrowsArgumentException() =>
        Assert.Throws<ArgumentException>(() => state.Set("NotAProperty", 1));

    [Fact]
    public void Observe_WithAPropertyNotOnTheModel_ThrowsArgumentException() =>
        Assert.Throws<ArgumentException>(() => state.Observe("NotAProperty"));

    [Fact]
    public void Get_WithAPropertyOnTheModel_ReturnsItsDefault() =>
        Assert.Equal(0, state.Get(nameof(ITestModel.Value)));
}
