namespace Markwardt.NetChannel.Tests;

public interface ITestModel
{
    int Value { get; set; }

    [NetAccess(true)]
    int OpenValue { get; set; }

    [NetRole("admin")]
    [NetAccess(true)]
    int AdminValue { get; set; }
}

public interface ITestController
{
    Task<int> Double(int value);

    void Ping();

    [NetAccess(false)]
    Task Restricted();

    [NetRole("admin")]
    Task AdminOnly();

    [NetSecure]
    Task<int> SecureDouble(int value);

    Task AlwaysThrows();

    void FireAndForgetThrows();

    [NetAccess(false)]
    Task<int> RestrictedResult();

    Task<int> IncrementCounter();
}

public sealed class TestEntity(INetState<ITestModel> state) : NetEntity<ITestModel, ITestController>(state), ITestController
{
    private int counter;

    public Task<int> Double(int value) => Task.FromResult(value * 2);

    public void Ping()
    {
    }

    public Task Restricted() => Task.CompletedTask;

    public Task AdminOnly() => Task.CompletedTask;

    public Task<int> SecureDouble(int value) => Task.FromResult(value * 2);

    public Task AlwaysThrows() => throw new InvalidOperationException("boom");

    public void FireAndForgetThrows() => throw new InvalidOperationException("boom");

    public Task<int> RestrictedResult() => Task.FromResult(0);

    public Task<int> IncrementCounter() => Task.FromResult(Interlocked.Increment(ref counter));
}

public interface IVariedModel
{
    string? Text { get; set; }
}

public interface IVariedController
{
    [NetSecure]
    void SecureVoid(string secret);

    Task<string> Concat(string a, int b, bool c, double d);

    void VoidWithArgs(int a, string b);

    Task<string?> ReturnsNull();

    Task<List<int>> ReturnsEmptyList();

    Task<int> NoArguments();
}

public sealed class VariedEntity(INetState<IVariedModel> state) : NetEntity<IVariedModel, IVariedController>(state), IVariedController
{
    private readonly TaskCompletionSource<string> secret = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource<(int A, string B)> voidArgs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<string> Secret => secret.Task;

    public Task<(int A, string B)> VoidArgs => voidArgs.Task;

    public void SecureVoid(string value) => secret.TrySetResult(value);

    public Task<string> Concat(string a, int b, bool c, double d) => Task.FromResult($"{a}|{b}|{c}|{d}");

    public void VoidWithArgs(int a, string b) => voidArgs.TrySetResult((a, b));

    public Task<string?> ReturnsNull() => Task.FromResult<string?>(null);

    public Task<List<int>> ReturnsEmptyList() => Task.FromResult(new List<int>());

    public Task<int> NoArguments() => Task.FromResult(99);
}

public interface IPositionedModel
{
    [NetPosition]
    NetPosition? Position { get; set; }
}

public interface IEmptyController;

public sealed class PositionedEntity(INetState<IPositionedModel> state) : NetEntity<IPositionedModel, IEmptyController>(state), IEmptyController;
