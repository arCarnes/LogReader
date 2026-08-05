namespace LogReader.App.Services;

using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

internal sealed class FileSessionRegistry
{
    private static readonly TimeSpan DefaultWarmRetentionDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DefaultAgentWarmRetentionDuration = TimeSpan.Zero;

    private readonly ILogReaderService _logReader;
    private readonly IFileTailService _tailService;
    private readonly IEncodingDetectionService _encodingDetectionService;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly object _gate = new();
    private readonly Dictionary<FileSessionKey, RegistryEntry> _entries = new();
    private bool _disposed;

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

    internal TimeSpan AgentWarmRetentionDuration { get; set; } = DefaultAgentWarmRetentionDuration;

    internal int ActiveSessionCount
    {
        get
        {
            lock (_gate)
                return _entries.Values.Count(entry => entry.TotalRefCount > 0);
        }
    }

    internal int RetainedSessionCount
    {
        get
        {
            lock (_gate)
                return _entries.Values.Count(entry => entry.TotalRefCount == 0);
        }
    }

    public FileSessionLease Acquire(string filePath, FileEncoding requestedEncoding)
        => Acquire(new FileSessionKey(filePath, requestedEncoding));

    public FileSessionLease Acquire(FileSessionKey key)
    {
        SweepExpiredSessions();

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(key, out var existing))
            {
                existing.UiRefCount++;
                existing.HasUiOwnership = true;
                existing.UiReleasedAtUtc = DateTime.MinValue;
                return new FileSessionLease(this, key, existing.Session);
            }

            var session = new FileSession(key, _logReader, _tailService, _encodingDetectionService, _uiDispatcher);
            _entries[key] = RegistryEntry.CreateForUi(session);
            return new FileSessionLease(this, key, session);
        }
    }

    internal AgentFileSessionLease AcquireForAgent(
        string filePath,
        FileEncoding requestedEncoding,
        int maximumAgentSessions,
        int maximumAgentMappedLineOffsets)
    {
        if (maximumAgentSessions < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumAgentSessions));
        if (maximumAgentMappedLineOffsets < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumAgentMappedLineOffsets));

        SweepExpiredSessions();
        var requestedKey = new FileSessionKey(filePath, requestedEncoding);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var selected = _entries
                .Where(pair => StringComparer.OrdinalIgnoreCase.Equals(pair.Key.FilePath, requestedKey.FilePath))
                .OrderByDescending(static pair => !pair.Value.Session.HasNoLineIndex)
                .ThenByDescending(static pair => pair.Value.UiRefCount > 0)
                .ThenByDescending(pair => pair.Key.Equals(requestedKey))
                .FirstOrDefault();

            FileSessionKey selectedKey;
            RegistryEntry entry;
            if (selected.Value != null)
            {
                selectedKey = selected.Key;
                entry = selected.Value;
                entry.AgentRefCount++;
                entry.AgentReleasedAtUtc = DateTime.MinValue;
            }
            else
            {
                var agentOnlySessions = _entries.Values.Count(static value => !value.HasUiOwnership);
                if (agentOnlySessions >= maximumAgentSessions)
                    throw new IndexedLogSessionCapacityExceededException("All UI-process agent sessions are currently admitted.");

                selectedKey = requestedKey;
                var session = new FileSession(
                    selectedKey,
                    _logReader,
                    _tailService,
                    _encodingDetectionService,
                    _uiDispatcher);
                entry = RegistryEntry.CreateForAgent(session);
                _entries[selectedKey] = entry;
            }

            return new AgentFileSessionLease(
                this,
                selectedKey,
                entry.Session,
                maximumAgentMappedLineOffsets);
        }
    }

    internal void Release(FileSessionKey key)
        => Release(key, LeaseOrigin.Ui);

    internal void ReleaseAgent(FileSessionKey key)
        => Release(key, LeaseOrigin.Agent);

    private void Release(FileSessionKey key, LeaseOrigin origin)
    {
        FileSession? sessionToDispose = null;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
                return;

            if (origin == LeaseOrigin.Ui)
                entry.UiRefCount = Math.Max(0, entry.UiRefCount - 1);
            else
                entry.AgentRefCount = Math.Max(0, entry.AgentRefCount - 1);

            if (entry.TotalRefCount > 0)
            {
                if (origin == LeaseOrigin.Ui && entry.UiRefCount == 0)
                    entry.UiReleasedAtUtc = DateTime.UtcNow;
                return;
            }

            var now = DateTime.UtcNow;
            if (origin == LeaseOrigin.Ui)
                entry.UiReleasedAtUtc = now;
            else
                entry.AgentReleasedAtUtc = now;
            var retention = entry.HasUiOwnership ? WarmRetentionDuration : AgentWarmRetentionDuration;
            if (_disposed || retention <= TimeSpan.Zero)
            {
                _entries.Remove(key);
                sessionToDispose = entry.Session;
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
                if (entry.TotalRefCount > 0)
                    continue;

                var retention = entry.HasUiOwnership ? WarmRetentionDuration : AgentWarmRetentionDuration;
                var releasedAt = entry.HasUiOwnership ? entry.UiReleasedAtUtc : entry.AgentReleasedAtUtc;
                if (releasedAt == DateTime.MinValue || utcNow - releasedAt < retention)
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
            if (_disposed)
                return;

            _disposed = true;
            sessionsToDispose = _entries.Values
                .Select(entry => entry.Session)
                .ToList();
            _entries.Clear();
        }

        foreach (var session in sessionsToDispose)
            session.Dispose();
    }

    internal int GetAgentIndexAdmission(FileSession session, int maximumMappedLineOffsets)
    {
        lock (_gate)
        {
            var current = _entries.Values.FirstOrDefault(entry => ReferenceEquals(entry.Session, session));
            if (current == null)
                throw new ObjectDisposedException(nameof(AgentFileSessionLease));
            if (current.HasUiOwnership && !session.HasNoLineIndex)
                return int.MaxValue;

            var otherOffsets = _entries.Values
                .Where(entry => !entry.HasUiOwnership && !ReferenceEquals(entry.Session, session))
                .Sum(static entry => entry.Session.DebugLineIndex?.LineCount ?? 0);
            var available = maximumMappedLineOffsets - otherOffsets;
            if (available < Math.Max(1, session.DebugLineIndex?.LineCount ?? 0))
                throw new IndexedLogSessionCapacityExceededException("The UI-process agent line-offset budget is exhausted.");

            return available;
        }
    }

    internal IndexedLogSessionProviderSnapshot GetAgentProviderSnapshot(
        int maximumAgentSessions,
        int maximumAgentMappedLineOffsets)
    {
        lock (_gate)
        {
            return new IndexedLogSessionProviderSnapshot(
                _entries.Values.Count(static entry => entry.AgentRefCount > 0),
                _entries.Values.Count(static entry => !entry.HasUiOwnership && entry.TotalRefCount == 0),
                _entries.Values.Where(static entry => !entry.HasUiOwnership)
                    .Sum(static entry => entry.Session.DebugLineIndex?.LineCount ?? 0),
                maximumAgentSessions,
                maximumAgentMappedLineOffsets,
                AgentWarmRetentionDuration);
        }
    }

    private sealed class RegistryEntry
    {
        private RegistryEntry(FileSession session)
        {
            Session = session;
        }

        public static RegistryEntry CreateForUi(FileSession session)
            => new(session) { UiRefCount = 1, HasUiOwnership = true };

        public static RegistryEntry CreateForAgent(FileSession session)
            => new(session) { AgentRefCount = 1 };

        public int UiRefCount { get; set; }

        public int AgentRefCount { get; set; }

        public int TotalRefCount => UiRefCount + AgentRefCount;

        public bool HasUiOwnership { get; set; }

        public DateTime UiReleasedAtUtc { get; set; }

        public DateTime AgentReleasedAtUtc { get; set; }

        public FileSession Session { get; }
    }

    private enum LeaseOrigin
    {
        Ui,
        Agent
    }
}
