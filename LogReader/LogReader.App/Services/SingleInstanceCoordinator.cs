namespace LogReader.App.Services;

internal interface IAppInstanceCoordinator
{
    bool TryAcquire();
}

internal sealed class SingleInstanceCoordinator : IAppInstanceCoordinator
{
    private static readonly object Sync = new();
    private static Mutex? s_mutex;
    private readonly string _mutexName;

    public SingleInstanceCoordinator(string? mutexName = null)
    {
        _mutexName = mutexName ?? @"Local\LogReader.SingleInstance";
    }

    public bool TryAcquire()
    {
        lock (Sync)
        {
            if (s_mutex != null)
                return true;

            var mutex = new Mutex(initiallyOwned: true, _mutexName, out var createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                return false;
            }

            s_mutex = mutex;
            return true;
        }
    }

    internal static void ReleaseForTests()
    {
        lock (Sync)
        {
            s_mutex?.Dispose();
            s_mutex = null;
        }
    }
}
