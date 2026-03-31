namespace LogReader.App.Services;

internal sealed class FileSessionLease : IDisposable
{
    private FileSessionRegistry? _registry;

    internal FileSessionLease(FileSessionRegistry registry, FileSessionKey key, FileSession session)
    {
        _registry = registry;
        Key = key;
        Session = session;
    }

    public FileSessionKey Key { get; }

    public FileSession Session { get; }

    public void Dispose()
    {
        var registry = Interlocked.Exchange(ref _registry, null);
        if (registry == null)
            return;

        registry.Release(Key);
    }
}
