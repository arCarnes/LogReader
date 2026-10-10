namespace LogReader.Infrastructure.Services;

using System.Security.Cryptography;
using System.Text.Json;
using LogReader.Core.Models;

/// <summary>Process-owned continuation state. Admission reserves working memory before a transaction starts.</summary>
internal sealed class QueryContinuationStore : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly LogQueryEffectiveLimits _limits;
    private readonly Func<DateTimeOffset> _now;
    private long _workingBytes;
    private bool _disposed;

    public QueryContinuationStore(LogQueryEffectiveLimits limits, Func<DateTimeOffset> now)
    {
        _limits = limits;
        _now = now;
    }

    public async Task<Lease> AcquireAsync(string? cursor, string operation, string fingerprint,
        string catalogRevision, CancellationToken ct)
    {
        var token = cursor == null ? null : Decode(cursor);
        Session session;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RemoveExpired();
            if (token == null)
            {
                if (_sessions.Count >= _limits.MaximumContinuationSessions)
                    throw new ContinuationException("continuation_capacity_exceeded", "session_limit_exceeded");
                session = new Session(Guid.NewGuid().ToString("N"), operation, fingerprint, catalogRevision, _now());
                _sessions.Add(session.Id, session);
            }
            else
            {
                if (token.Operation != operation)
                    throw new ContinuationException("mismatched_query_cursor");
                if (!_sessions.TryGetValue(token.SessionId, out session!))
                    throw new ContinuationException("expired_query_cursor");
                if (session.CatalogRevision != catalogRevision)
                    throw new ContinuationException("stale_query_cursor");
                if (session.Fingerprint != fingerprint)
                    throw new ContinuationException("mismatched_query_cursor");
            }
            session.Users++;
        }

        try
        {
            await session.Gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            ReleaseUser(session, cursor == null);
            throw;
        }

        try
        {
            lock (_gate)
            {
                var revision = token?.Revision ?? 0;
                var replay = revision == session.Revision - 1 && session.LastResult != null;
                if (!replay && (revision != session.Revision || session.Terminal))
                    throw new ContinuationException("stale_query_cursor");
                // Clone capacity plus two decoded lines, growth-copy buffers and bounded response scratch.
                var workingReservation = McpContinuationLimits.GetWorkingReservationBytes(_limits);
                if (!replay && _workingBytes + RetainedBytes() + workingReservation >
                    _limits.MaximumContinuationBytes)
                    throw new ContinuationException("continuation_capacity_exceeded", "memory_budget_exceeded");
                var reservation = replay ? 0 : workingReservation;
                _workingBytes += reservation;
                session.LastUsed = _now();
                return new Lease(this, session, replay, reservation, cursor == null);
            }
        }
        catch
        {
            session.Gate.Release();
            ReleaseUser(session, cursor == null);
            throw;
        }
    }

    private Token Decode(string cursor)
    {
        try
        {
            if (cursor.Length > 1024)
                throw new FormatException();
            var parts = cursor.Split('.');
            if (parts.Length != 2)
                throw new FormatException();
            var bytes = Convert.FromBase64String(parts[0]);
            var signature = Convert.FromBase64String(parts[1]);
            if (Convert.ToBase64String(bytes) != parts[0] || Convert.ToBase64String(signature) != parts[1])
                throw new FormatException();
            if (!CryptographicOperations.FixedTimeEquals(signature, HMACSHA256.HashData(_key, bytes)))
                throw new FormatException();
            var token = JsonSerializer.Deserialize<Token>(bytes);
            if (token is not { Version: 1, Revision: >= 0 } ||
                token.Operation is not ("search" or "count") || !Guid.TryParseExact(token.SessionId, "N", out _))
                throw new FormatException();
            return token;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new ContinuationException("invalid_query_cursor");
        }
    }

    private string Encode(Session session, long revision)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Token(1, session.Operation, session.Id, revision));
        return Convert.ToBase64String(bytes) + "." + Convert.ToBase64String(HMACSHA256.HashData(_key, bytes));
    }

    private long RetainedBytes() => _sessions.Values.Sum(static session => session.Bytes);

    private void RemoveExpired()
    {
        var cutoff = _now().AddMilliseconds(-_limits.ContinuationIdleMilliseconds);
        foreach (var session in _sessions.Values.Where(s => s.Users == 0 && s.LastUsed <= cutoff).ToArray())
            _sessions.Remove(session.Id);
    }

    private void ReleaseUser(Session session, bool removeEmpty)
    {
        lock (_gate)
        {
            session.Users--;
            if (removeEmpty && (session.State == null || session.Terminal && session.Revision == 1) && session.Users == 0)
                _sessions.Remove(session.Id);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _sessions.Clear();
            CryptographicOperations.ZeroMemory(_key);
        }
    }

    private sealed record Token(int Version, string Operation, string SessionId, long Revision);

    internal sealed class Session(string id, string operation, string fingerprint, string catalogRevision,
        DateTimeOffset lastUsed)
    {
        public string Id { get; } = id;
        public string Operation { get; } = operation;
        public string Fingerprint { get; } = fingerprint;
        public string CatalogRevision { get; } = catalogRevision;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public DateTimeOffset LastUsed { get; set; } = lastUsed;
        public int Users { get; set; }
        public long Revision { get; set; }
        public long Bytes { get; set; }
        public object? State { get; set; }
        public object? LastResult { get; set; }
        public bool Terminal { get; set; }
    }

    internal sealed class Lease : IDisposable
    {
        private readonly QueryContinuationStore _owner;
        private readonly Session _session;
        private long _reservation;
        private readonly bool _initial;
        private bool _disposed;

        internal Lease(QueryContinuationStore owner, Session session, bool replay, long reservation, bool initial)
        {
            _owner = owner;
            _session = session;
            IsReplay = replay;
            _reservation = reservation;
            _initial = initial;
        }

        public bool IsReplay { get; }
        public object? State => _session.State;
        public object? ReplayResult => _session.LastResult;
        public string NextCursor => _owner.Encode(_session, checked(_session.Revision + 1));

        public void Commit(object state, object result, long bytes, bool terminal)
        {
            lock (_owner._gate)
            {
                if (bytes > _owner._limits.MaximumContinuationSessionBytes || bytes < 0)
                    throw new ContinuationException("continuation_capacity_exceeded", "query_too_large");
                var transfer = bytes - _session.Bytes;
                _owner._workingBytes -= transfer;
                _reservation -= transfer;
                _session.State = state;
                _session.LastResult = result;
                _session.Bytes = bytes;
                _session.Terminal = terminal;
                _session.Revision++;
                _session.LastUsed = _owner._now();
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            lock (_owner._gate)
                _owner._workingBytes -= _reservation;
            _session.Gate.Release();
            _owner.ReleaseUser(_session, _initial);
        }
    }
}

internal sealed class ContinuationException(string code, string? reason = null) : Exception(code)
{
    public string Code { get; } = code;

    public string? Reason { get; } = reason;
}
