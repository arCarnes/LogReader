namespace LogReader.Core.Interfaces;

using LogReader.Core.Models;

/// <summary>
/// Supplies process-owned line indexes to bounded query code without exposing their backing storage.
/// </summary>
public interface IIndexedLogSessionProvider : IDisposable
{
    IndexedLogSessionProviderSnapshot GetProviderSnapshot();

    IIndexedLogSessionLease AcquireSession(
        string filePath,
        FileEncoding requestedEncoding = FileEncoding.Auto);
}

public interface IIndexedLogSessionLease : IDisposable
{
    string FilePath { get; }

    FileEncoding Encoding { get; }

    Task<T> UseCurrentIndexAsync<T>(
        Func<LineIndex, FileEncoding, CancellationToken, Task<T>> operation,
        CancellationToken ct = default);
}

public sealed record IndexedLogSessionProviderSnapshot(
    int ActiveSessions,
    int RetainedSessions,
    int MappedLineOffsets,
    int MaximumSessions,
    int MaximumMappedLineOffsets,
    TimeSpan WarmRetentionDuration);
