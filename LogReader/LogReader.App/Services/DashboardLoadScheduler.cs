namespace LogReader.App.Services;

using LogReader.Core;

/// <summary>Shared admission for UI previews and index quanta across dashboard scopes.</summary>
internal sealed class DashboardLoadScheduler : IDisposable
{
    private readonly object _gate = new();
    private readonly List<Work> _pending = new();
    private readonly Dictionary<string, int> _activeGroups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _scopeOrder = new(StringComparer.Ordinal);
    private long _nextOrder;
    private int _active;
    private int _backgroundActive;
    private bool _disposed;
    private string? _foreground;

    public void BeginCycle(string dashboardId)
    {
        lock (_gate)
            _scopeOrder[dashboardId] = ++_nextOrder;
    }

    public void SetForeground(string? dashboardId)
    {
        lock (_gate)
        {
            _foreground = dashboardId;
            Drain();
        }
    }

    public async Task<Lease> AcquireAsync(string path, Func<IReadOnlyList<string>> scopes, bool preview, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var plan = AdaptiveParallelismPolicy.CreatePlan(AdaptiveParallelismOperation.DashboardLoad, [path, path]);
        var work = new Work(plan.Groups, scopes, preview, ct);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            work.Order = ++_nextOrder;
            foreach (var scope in scopes())
                _scopeOrder.TryAdd(scope, work.Order);
            _pending.Add(work);
            work.Registration = ct.Register(() => Cancel(work));
            Drain();
        }
        try
        {
            return await work.Completion.Task.ConfigureAwait(false);
        }
        catch
        {
            work.Registration.Dispose();
            throw;
        }
    }

    private bool IsForeground(Work work) => _foreground != null && work.Scopes().Contains(_foreground, StringComparer.Ordinal);
    private int Priority(Work work) => IsForeground(work) ? work.Preview ? 0 : 1 : 2;
    private long ScopeOrder(Work work) => work.Scopes().Select(scope => _scopeOrder.GetValueOrDefault(scope, work.Order))
        .DefaultIfEmpty(work.Order).Min();

    private void Drain()
    {
        if (_disposed)
            return;
        while (_active < 24)
        {
            var work = _pending.Where(item => !item.Token.IsCancellationRequested)
                .OrderBy(Priority).ThenBy(ScopeOrder).ThenBy(item => item.Order)
                .FirstOrDefault(item => (Priority(item) < 2 || _backgroundActive == 0) &&
                    item.Groups.All(group => _activeGroups.GetValueOrDefault(group.Key) < group.Limit));
            if (work == null)
                return;
            // A pending foreground preview reserves the next available permit ahead of indexing.
            if (Priority(work) != 0 && _pending.Any(item => Priority(item) == 0 && !item.Token.IsCancellationRequested))
                return;
            _pending.Remove(work);
            _active++;
            var background = !IsForeground(work);
            if (background) _backgroundActive++;
            foreach (var group in work.Groups)
                _activeGroups[group.Key] = _activeGroups.GetValueOrDefault(group.Key) + 1;
            work.Completion.TrySetResult(new Lease(this, work, background));
        }
    }

    private void Cancel(Work work)
    {
        lock (_gate)
        {
            if (_pending.Remove(work))
                work.Completion.TrySetCanceled(work.Token);
            Drain();
        }
    }

    private void Release(Work work, bool background)
    {
        lock (_gate)
        {
            _active--;
            if (background) _backgroundActive--;
            foreach (var group in work.Groups)
                _activeGroups[group.Key]--;
            Drain();
        }
        work.Registration.Dispose();
    }

    private bool ShouldYield(Work work, bool admittedInBackground)
    {
        lock (_gate)
            return _disposed || work.Token.IsCancellationRequested ||
                !work.Preview && (IsForeground(work)
                    ? _pending.Any(item => Priority(item) == 0)
                    : !admittedInBackground || _pending.Any(item => Priority(item) < 2));
    }

    public void Dispose()
    {
        Work[] pending;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            pending = _pending.ToArray();
            _pending.Clear();
            foreach (var work in pending) work.Completion.TrySetCanceled();
        }
        foreach (var work in pending) work.Registration.Dispose();
    }

    internal sealed class Work
    {
        public Work(IReadOnlyList<ParallelismGroupPlan> groups, Func<IReadOnlyList<string>> scopes, bool preview, CancellationToken token)
            => (Groups, Scopes, Preview, Token) = (groups, scopes, preview, token);
        public IReadOnlyList<ParallelismGroupPlan> Groups { get; }
        public Func<IReadOnlyList<string>> Scopes { get; }
        public bool Preview { get; }
        public CancellationToken Token { get; }
        public long Order { get; set; }
        public CancellationTokenRegistration Registration { get; set; }
        public TaskCompletionSource<Lease> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class Lease : IDisposable
    {
        private DashboardLoadScheduler? _owner;
        private readonly Work _work;
        private readonly bool _background;
        internal Lease(DashboardLoadScheduler owner, Work work, bool background)
            => (_owner, _work, _background) = (owner, work, background);
        public bool ShouldYield() => _owner?.ShouldYield(_work, _background) ?? true;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(_work, _background);
    }
}
