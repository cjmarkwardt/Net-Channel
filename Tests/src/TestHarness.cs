namespace Markwardt.NetChannel.Tests;

public static class TestHarness
{
    [ModuleInitializer]
    internal static void WarmUpThreadPool()
    {
        ThreadPool.SetMinThreads(64, 64);
    }

    public static int GetFreeUdpPort()
    {
        using Socket probe = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    public static (ServiceProvider Provider, INetManager Manager) CreateManager()
    {
        ServiceCollection services = new();
        services.AddNetChannel();
        ServiceProvider provider = services.BuildServiceProvider();
        return (provider, provider.GetRequiredService<INetManager>());
    }

    public static async Task WaitUntil(Func<bool> condition, int timeoutMilliseconds = 15000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                // Checked once more first: under load the poll delay can overrun by a lot, and the condition
                // may well have come true during the last one.
                if (condition())
                {
                    return;
                }

                throw new TimeoutException();
            }

            await Task.Delay(20);
        }
    }

    public static async Task<T> WaitForTask<T>(Task<T> task, int timeoutMilliseconds = 15000)
    {
        Task completed = await Task.WhenAny(task, Task.Delay(timeoutMilliseconds));

        if (completed != task)
        {
            throw new TimeoutException();
        }

        return await task;
    }

    public static async Task WaitForTask(Task task, int timeoutMilliseconds = 15000)
    {
        Task completed = await Task.WhenAny(task, Task.Delay(timeoutMilliseconds));

        if (completed != task)
        {
            throw new TimeoutException();
        }

        await task;
    }
}
