namespace LogReader.App.Services;

using LogReader.Core.Interfaces;
using LogReader.Core.Models;

internal sealed class FileSessionRegistry
{
    private static readonly TimeSpan DefaultWarmRetentionDuration = TimeSpan.FromMinutes(2);

    private readonly ILogReaderService _logReader;
    private readonly IFileTailService _tailService;
    private readonly IEncodingDetectionService _encodingDetectionService;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly object _gate = new();
    private readonly Dictionary<FileSessionKey, RegistryEntry> _entries = new();

    public FileSessionRegistry(
        ILogReaderService logReader,
        IFileTailService tailService,
        IEncodingDetectionService encodingDetectionService,
        IUiDispatcher? uiDispatcher = null)
    {
        _logReader = logReader;
        _tailService = tailService;
        _encodingDetectionService = encodingDetectionService;
        _uiDispatcher = uiDispatcher ?? WpfUiDispatcher.Instance;
    }

    internal TimeSpan WarmRetentionDuration { get; set; } = DefaultWarmRetentionDuration;

    internal int ActiveSessionCount
    {
        get
        {
            lock (_gate)
                return _entries.Values.Count(entry => entry.RefCount > 0);
        }
    }

    internal int RetainedSessionCount
    {
        get
        {
            lock (_gate)
                return _entries.Values.Count(entry => entry.RefCount == 0);
        }
    }

    public FileSessionLease Acquire(string filePath, FileEncoding requestedEncoding)
        => Acquire(new FileSessionKey(filePath, requestedEncoding));

    public FileSessionLease Acquire(FileSessionKey key)
    {
        SweepExpiredSessions();

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                existing.RefCount++;
                existing.ReleasedAtUtc = DateTime.MinValue;
                return new FileSessionLease(this, key, existing.Session);
            }

            var session = new FileSession(key, _logReader, _tailService, _encodingDetectionService, _uiDispatcher);
            _entries[key] = new RegistryEntry(session);
            return new FileSessionLease(this, key, session);
        }
    }

    internal void Release(FileSessionKey key)
    {
        FileSession? sessionToDispose = null;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
                return;

            entry.RefCount--;
            if (entry.RefCount > 0)
                return;

            if (WarmRetentionDuration <= TimeSpan.Zero)
            {
                _entries.Remove(key);
                sessionToDispose = entry.Session;
            }
            else
            {
                entry.ReleasedAtUtc = DateTime.UtcNow;
            }
        }

        sessionToDispose?.Dispose();
    }

    internal int SweepExpiredSessions()
        => SweepExpiredSessions(DateTime.UtcNow);

    internal int SweepExpiredSessions(DateTime utcNow)
    {
        List<FileSession>? sessionsToDispose = null;
        lock (_gate)
        {
            foreach (var (key, entry) in _entries.ToList())
            {
                if (entry.RefCount > 0 || entry.ReleasedAtUtc == DateTime.MinValue)
                    continue;

                if (utcNow - entry.ReleasedAtUtc < WarmRetentionDuration)
                    continue;

                _entries.Remove(key);
                sessionsToDispose ??= new List<FileSession>();
                sessionsToDispose.Add(entry.Session);
            }
        }

        if (sessionsToDispose == null)
            return 0;

        foreach (var session in sessionsToDispose)
            session.Dispose();

        return sessionsToDispose.Count;
    }

    public void Dispose()
    {
        List<FileSession>? sessionsToDispose = null;
        lock (_gate)
        {
            if (_entries.Count == 0)
                return;

            sessionsToDispose = _entries.Values
                .Select(entry => entry.Session)
                .ToList();
            _entries.Clear();
        }

        foreach (var session in sessionsToDispose)
            session.Dispose();
    }

    private sealed class RegistryEntry
    {
        public RegistryEntry(FileSession session)
        {
            Session = session;
            RefCount = 1;
        }

        public int RefCount { get; set; }

        public DateTime ReleasedAtUtc { get; set; }

        public FileSession Session { get; }
    }
}
