namespace LogReader.Infrastructure.Services;

using LogReader.Core.Interfaces;
using LogReader.Core.Models;

/// <summary>
/// Process-local, WPF-free cache for bounded line-index operations.
/// General content search deliberately does not use this cache.
/// </summary>
public sealed class IndexedLogSessionCache : IDisposable
{
    private readonly IBoundedLogReaderService _logReader;
    private readonly IEncodingDetectionService _encodingDetection;
    private readonly IndexedLogSessionCacheOptions _options;
    private readonly Func<DateTime> _utcNow;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _indexMutationGate = new(1, 1);
    private readonly Dictionary<SessionKey, CacheEntry> _entries = new();
    private bool _disposed;

    public IndexedLogSessionCache(
        IBoundedLogReaderService logReader,
        IEncodingDetectionService encodingDetection,
        IndexedLogSessionCacheOptions? options = null)
        : this(logReader, encodingDetection, options, () => DateTime.UtcNow)
    {
    }

    internal IndexedLogSessionCache(
        IBoundedLogReaderService logReader,
        IEncodingDetectionService encodingDetection,
        IndexedLogSessionCacheOptions? options,
        Func<DateTime> utcNow)
    {
        _logReader = logReader ?? throw new ArgumentNullException(nameof(logReader));
        _encodingDetection = encodingDetection ?? throw new ArgumentNullException(nameof(encodingDetection));
        _options = options ?? new IndexedLogSessionCacheOptions();
        _options.Validate();
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    public IndexedLogSessionCacheSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return new IndexedLogSessionCacheSnapshot(
                _entries.Values.Count(static entry => entry.RefCount > 0),
                _entries.Values.Count(static entry => entry.RefCount == 0),
                _entries.Values.Sum(static entry => entry.Session.LineCount),
                _options.MaximumSessions,
                _options.MaximumMappedLineOffsets,
                _options.WarmRetentionDuration);
        }
    }

    public IndexedLogSessionLease Acquire(
        string filePath,
        FileEncoding requestedEncoding = FileEncoding.Auto)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var normalizedPath = Path.GetFullPath(filePath ?? string.Empty);
        var resolvedEncoding = _encodingDetection
            .ResolveEncodingDecision(normalizedPath, requestedEncoding)
            .ResolvedEncoding;
        var key = new SessionKey(normalizedPath, resolvedEncoding);

        List<IndexedLogSession>? sessionsToDispose = null;
        IndexedLogSession session;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            sessionsToDispose = RemoveExpiredEntriesLocked(_utcNow());

            if (_entries.TryGetValue(key, out var existing))
            {
                existing.RefCount++;
                existing.ReleasedAtUtc = DateTime.MinValue;
                existing.LastUsedUtc = _utcNow();
                session = existing.Session;
            }
            else
            {
                sessionsToDispose ??= new List<IndexedLogSession>();
                EvictWarmEntriesForAdmissionLocked(sessionsToDispose);
                if (_entries.Count >= _options.MaximumSessions)
                {
                    throw new IndexedLogSessionCapacityExceededException(
                        "All bounded indexed sessions are currently leased.");
                }

                session = new IndexedLogSession(key);
                _entries.Add(key, new CacheEntry(session, _utcNow()));
            }
        }

        DisposeSessions(sessionsToDispose);
        return new IndexedLogSessionLease(this, session);
    }

    public int SweepExpiredSessions()
    {
        List<IndexedLogSession>? sessionsToDispose;
        lock (_gate)
        {
            if (_disposed)
                return 0;

            sessionsToDispose = RemoveExpiredEntriesLocked(_utcNow());
        }

        DisposeSessions(sessionsToDispose);
        return sessionsToDispose?.Count ?? 0;
    }

    internal async Task<T> UseCurrentIndexAsync<T>(
        IndexedLogSession session,
        Func<LineIndex, FileEncoding, CancellationToken, Task<T>> operation,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await session.OperationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var index = await EnsureCurrentIndexAsync(session, ct).ConfigureAwait(false);
            return await operation(index, session.Key.Encoding, ct).ConfigureAwait(false);
        }
        finally
        {
            session.OperationGate.Release();
            Touch(session);
        }
    }

    internal void Release(IndexedLogSession session)
    {
        IndexedLogSession? sessionToDispose = null;
        lock (_gate)
        {
            if (!_entries.TryGetValue(session.Key, out var entry) ||
                !ReferenceEquals(entry.Session, session))
            {
                return;
            }

            entry.RefCount--;
            if (entry.RefCount > 0)
                return;

            entry.ReleasedAtUtc = _utcNow();
            entry.LastUsedUtc = entry.ReleasedAtUtc;
            if (_disposed || _options.WarmRetentionDuration <= TimeSpan.Zero)
            {
                _entries.Remove(session.Key);
                sessionToDispose = session;
            }
        }

        sessionToDispose?.Dispose();
    }

    public void Dispose()
    {
        List<IndexedLogSession>? sessionsToDispose = null;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            foreach (var (key, entry) in _entries.ToList())
            {
                if (entry.RefCount > 0)
                    continue;

                _entries.Remove(key);
                sessionsToDispose ??= new List<IndexedLogSession>();
                sessionsToDispose.Add(entry.Session);
            }
        }

        DisposeSessions(sessionsToDispose);
    }

    private async Task<LineIndex> EnsureCurrentIndexAsync(
        IndexedLogSession session,
        CancellationToken ct)
    {
        await _indexMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var admittedMaximum = GetAdmittedMaximumLineCount(session);
            LineIndex updated;
            if (session.Index == null)
            {
                updated = await _logReader.BuildBoundedIndexAsync(
                    session.Key.FilePath,
                    session.Key.Encoding,
                    admittedMaximum,
                    ct).ConfigureAwait(false);
            }
            else
            {
                updated = await _logReader.UpdateBoundedIndexAsync(
                    session.Key.FilePath,
                    session.Index,
                    session.Key.Encoding,
                    admittedMaximum,
                    ct).ConfigureAwait(false);
            }

            if (!ReferenceEquals(updated, session.Index))
            {
                var replaced = session.Index;
                session.Index = updated;
                replaced?.Dispose();
            }

            return session.Index;
        }
        finally
        {
            _indexMutationGate.Release();
        }
    }

    private int GetAdmittedMaximumLineCount(IndexedLogSession session)
    {
        lock (_gate)
        {
            var otherOffsetCount = _entries.Values
                .Where(entry => !ReferenceEquals(entry.Session, session))
                .Sum(static entry => entry.Session.LineCount);
            var available = _options.MaximumMappedLineOffsets - otherOffsetCount;
            if (available < Math.Max(1, session.LineCount))
            {
                throw new IndexedLogSessionCapacityExceededException(
                    "The bounded line-index offset budget is exhausted.");
            }

            return available;
        }
    }

    private List<IndexedLogSession>? RemoveExpiredEntriesLocked(DateTime utcNow)
    {
        List<IndexedLogSession>? removed = null;
        foreach (var (key, entry) in _entries.ToList())
        {
            if (entry.RefCount > 0 || entry.ReleasedAtUtc == DateTime.MinValue)
                continue;
            if (utcNow - entry.ReleasedAtUtc < _options.WarmRetentionDuration)
                continue;

            _entries.Remove(key);
            removed ??= new List<IndexedLogSession>();
            removed.Add(entry.Session);
        }

        return removed;
    }

    private void EvictWarmEntriesForAdmissionLocked(List<IndexedLogSession> removed)
    {
        while (_entries.Count >= _options.MaximumSessions)
        {
            var candidate = _entries.Values
                .Where(static entry => entry.RefCount == 0)
                .OrderBy(static entry => entry.LastUsedUtc)
                .FirstOrDefault();
            if (candidate == null)
                return;

            _entries.Remove(candidate.Session.Key);
            removed.Add(candidate.Session);
        }
    }

    private void Touch(IndexedLogSession session)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(session.Key, out var entry) &&
                ReferenceEquals(entry.Session, session))
            {
                entry.LastUsedUtc = _utcNow();
            }
        }
    }

    private static void DisposeSessions(IEnumerable<IndexedLogSession>? sessions)
    {
        if (sessions == null)
            return;

        foreach (var session in sessions)
            session.Dispose();
    }

    internal readonly struct SessionKey : IEquatable<SessionKey>
    {
        public SessionKey(string filePath, FileEncoding encoding)
        {
            FilePath = filePath;
            Encoding = encoding;
        }

        public string FilePath { get; }

        public FileEncoding Encoding { get; }

        public bool Equals(SessionKey other)
            => Encoding == other.Encoding &&
               StringComparer.OrdinalIgnoreCase.Equals(FilePath, other.FilePath);

        public override bool Equals(object? obj)
            => obj is SessionKey other && Equals(other);

        public override int GetHashCode()
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(FilePath),
                Encoding);
    }

    internal sealed class IndexedLogSession : IDisposable
    {
        public IndexedLogSession(SessionKey key)
        {
            Key = key;
        }

        public SessionKey Key { get; }

        public SemaphoreSlim OperationGate { get; } = new(1, 1);

        public LineIndex? Index { get; set; }

        public int LineCount => Index?.LineCount ?? 0;

        public void Dispose()
        {
            Index?.Dispose();
            Index = null;
            OperationGate.Dispose();
        }
    }

    private sealed class CacheEntry
    {
        public CacheEntry(IndexedLogSession session, DateTime utcNow)
        {
            Session = session;
            RefCount = 1;
            LastUsedUtc = utcNow;
        }

        public IndexedLogSession Session { get; }

        public int RefCount { get; set; }

        public DateTime ReleasedAtUtc { get; set; }

        public DateTime LastUsedUtc { get; set; }
    }
}

public sealed class IndexedLogSessionLease : IDisposable
{
    private IndexedLogSessionCache? _cache;
    private readonly IndexedLogSessionCache.IndexedLogSession _session;

    internal IndexedLogSessionLease(
        IndexedLogSessionCache cache,
        IndexedLogSessionCache.IndexedLogSession session)
    {
        _cache = cache;
        _session = session;
    }

    public string FilePath => _session.Key.FilePath;

    public FileEncoding Encoding => _session.Key.Encoding;

    public Task<T> UseCurrentIndexAsync<T>(
        Func<LineIndex, FileEncoding, CancellationToken, Task<T>> operation,
        CancellationToken ct = default)
    {
        var cache = Volatile.Read(ref _cache) ??
            throw new ObjectDisposedException(nameof(IndexedLogSessionLease));
        return cache.UseCurrentIndexAsync(_session, operation, ct);
    }

    public void Dispose()
    {
        var cache = Interlocked.Exchange(ref _cache, null);
        cache?.Release(_session);
    }
}

public sealed class IndexedLogSessionCacheOptions
{
    public int MaximumSessions { get; init; } = 4;

    public int MaximumMappedLineOffsets { get; init; } = 2_000_000;

    public TimeSpan WarmRetentionDuration { get; init; } = TimeSpan.FromSeconds(30);

    internal void Validate()
    {
        if (MaximumSessions < 1)
            throw new ArgumentOutOfRangeException(nameof(MaximumSessions));
        if (MaximumMappedLineOffsets < 1)
            throw new ArgumentOutOfRangeException(nameof(MaximumMappedLineOffsets));
        if (WarmRetentionDuration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(WarmRetentionDuration));
    }
}

public sealed record IndexedLogSessionCacheSnapshot(
    int ActiveSessions,
    int RetainedSessions,
    int MappedLineOffsets,
    int MaximumSessions,
    int MaximumMappedLineOffsets,
    TimeSpan WarmRetentionDuration);

public sealed class IndexedLogSessionCapacityExceededException : IOException
{
    public IndexedLogSessionCapacityExceededException(string message)
        : base(message)
    {
    }
}
