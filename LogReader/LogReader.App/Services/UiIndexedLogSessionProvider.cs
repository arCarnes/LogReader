namespace LogReader.App.Services;

using System.IO;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

/// <summary>
/// Borrows line indexes from the UI-owned registry. Disposing this adapter never disposes the registry.
/// </summary>
internal sealed class UiIndexedLogSessionProvider : IIndexedLogSessionProvider
{
    internal const int DefaultMaximumAgentSessions = 4;
    internal const int DefaultMaximumAgentMappedLineOffsets = 2_000_000;

    private readonly FileSessionRegistry _registry;
    private readonly int _maximumAgentSessions;
    private readonly int _maximumAgentMappedLineOffsets;
    private int _disposed;

    public UiIndexedLogSessionProvider(
        FileSessionRegistry registry,
        int maximumAgentSessions = DefaultMaximumAgentSessions,
        int maximumAgentMappedLineOffsets = DefaultMaximumAgentMappedLineOffsets)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        if (maximumAgentSessions < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumAgentSessions));
        if (maximumAgentMappedLineOffsets < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumAgentMappedLineOffsets));

        _maximumAgentSessions = maximumAgentSessions;
        _maximumAgentMappedLineOffsets = maximumAgentMappedLineOffsets;
    }

    public IndexedLogSessionProviderSnapshot GetProviderSnapshot()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _registry.GetAgentProviderSnapshot(
            _maximumAgentSessions,
            _maximumAgentMappedLineOffsets);
    }

    public IIndexedLogSessionLease AcquireSession(
        string filePath,
        FileEncoding requestedEncoding = FileEncoding.Auto)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _registry.AcquireForAgent(
            filePath,
            requestedEncoding,
            _maximumAgentSessions,
            _maximumAgentMappedLineOffsets);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
    }
}

internal sealed class AgentFileSessionLease : IIndexedLogSessionLease
{
    private FileSessionRegistry? _registry;
    private readonly FileSessionKey _key;
    private readonly FileSession _session;
    private readonly int _maximumMappedLineOffsets;

    internal AgentFileSessionLease(
        FileSessionRegistry registry,
        FileSessionKey key,
        FileSession session,
        int maximumMappedLineOffsets)
    {
        _registry = registry;
        _key = key;
        _session = session;
        _maximumMappedLineOffsets = maximumMappedLineOffsets;
    }

    public string FilePath => _key.FilePath;

    public FileEncoding Encoding => _key.RequestedEncoding;

    internal FileSession DebugSession => _session;

    public async Task<T> UseCurrentIndexAsync<T>(
        Func<LineIndex, FileEncoding, CancellationToken, Task<T>> operation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var registry = Volatile.Read(ref _registry) ??
            throw new ObjectDisposedException(nameof(AgentFileSessionLease));
        var maximumLineCount = registry.GetAgentIndexAdmission(_session, _maximumMappedLineOffsets);
        await _session.EnsureAgentLineIndexAsync(maximumLineCount, ct).ConfigureAwait(false);

        T? result = default;
        var used = await _session.WithLineIndexLeaseAsync(
            async (index, encoding, token) =>
            {
                result = await operation(index, encoding, token).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
        if (!used)
            throw new IOException("The shared UI line index is not currently available.");

        return result!;
    }

    public void Dispose()
    {
        var registry = Interlocked.Exchange(ref _registry, null);
        registry?.ReleaseAgent(_key);
    }
}
