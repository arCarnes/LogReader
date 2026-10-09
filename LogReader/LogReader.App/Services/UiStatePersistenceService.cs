namespace LogReader.App.Services;

using System.IO;
using System.Text.Json;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

internal sealed class UiStatePersistenceService : IDisposable
{
    private readonly IUiStateRepository _repository;
    private readonly Action<Exception> _reportFailure;
    private readonly object _gate = new();
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private UiState _current = new();
    private UiState? _saved;
    private Task _worker = Task.CompletedTask;
    private CancellationTokenSource? _debounce;
    private bool _pending;
    private bool _started;
    private bool _closing;
    private bool _loadFailed;
    private bool _disposed;
    private int _failureReported;
    private long _revision;

    internal UiStatePersistenceService(IUiStateRepository repository, Action<Exception> reportFailure,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _repository = repository;
        _reportFailure = reportFailure;
        _delay = delay ?? Task.Delay;
    }

    internal async Task<UiState> LoadAsync()
    {
        try { return await _repository.LoadAsync(); }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            _loadFailed = true;
            ReportFailure(ex);
            return new UiState();
        }
    }

    internal void Start(UiState restored)
    {
        lock (_gate)
        {
            _current = Copy(restored);
            _saved = _loadFailed ? null : Copy(restored);
            _started = true;
        }
    }

    internal void Queue(UiState snapshot)
    {
        lock (_gate)
        {
            if (!_started || _closing || _disposed || Equal(_current, snapshot))
                return;
            _current = Copy(snapshot);
            _revision++;
            _pending = true;
            _debounce?.Cancel();
            StartWorker();
        }
    }

    internal async Task FlushAsync(UiState snapshot, TimeSpan timeout)
    {
        Task worker;
        lock (_gate)
        {
            if (!_started || _disposed)
                return;
            _closing = true;
            _current = Copy(snapshot);
            _revision++;
            // Retry a failed save on close, but leave a successful unchanged store alone.
            _pending = _pending || !Equal(_saved, _current);
            _debounce?.Cancel();
            StartWorker();
            worker = _worker;
        }
        try { await worker.WaitAsync(timeout); }
        catch (TimeoutException ex) { ReportFailure(ex); }
    }

    private void StartWorker()
    {
        if (_pending && _worker.IsCompleted)
            _worker = Task.Run(SavePendingAsync);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _pending = false;
            _debounce?.Cancel();
        }
    }

    private async Task SavePendingAsync()
    {
        while (true)
        {
            CancellationTokenSource debounce;
            bool closing;
            long revision;
            lock (_gate)
            {
                if (!_pending)
                    return;
                debounce = new CancellationTokenSource();
                _debounce = debounce;
                closing = _closing;
                revision = _revision;
            }
            try
            {
                if (!closing)
                    await _delay(TimeSpan.FromMilliseconds(300), debounce.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (debounce.IsCancellationRequested)
            {
                continue;
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_debounce, debounce))
                        _debounce = null;
                    debounce.Dispose();
                }
            }

            UiState snapshot;
            lock (_gate)
            {
                if (revision != _revision)
                    continue;
                snapshot = Copy(_current);
                _pending = false;
            }
            try
            {
                await _repository.SaveAsync(snapshot).ConfigureAwait(false);
                lock (_gate)
                {
                    _saved = snapshot;
                    if (Equal(_saved, _current))
                        _pending = false;
                }
            }
            catch (Exception ex) when (IsStorageFailure(ex)) { ReportFailure(ex); }

            lock (_gate)
            {
                if (!_pending)
                {
                    // Mark idle under the same lock used by Queue so a final toggle cannot be lost.
                    _worker = Task.CompletedTask;
                    return;
                }
            }
        }
    }

    private void ReportFailure(Exception ex)
    {
        lock (_gate)
            if (_disposed)
                return;
        if (Interlocked.Exchange(ref _failureReported, 1) == 0)
            _reportFailure(ex);
    }

    private static bool IsStorageFailure(Exception ex)
        => ex is IOException or UnauthorizedAccessException or JsonException;

    private static UiState Copy(UiState state)
        => new() { GroupExpansionById = new(state.GroupExpansionById, StringComparer.Ordinal) };

    private static bool Equal(UiState? left, UiState right)
        => left != null && left.GroupExpansionById.Count == right.GroupExpansionById.Count &&
            left.GroupExpansionById.All(pair => right.GroupExpansionById.TryGetValue(pair.Key, out var value) && value == pair.Value);
}

internal sealed class MemoryUiStateRepository : IUiStateRepository
{
    private UiState _state = new();
    public Task<UiState> LoadAsync() => Task.FromResult(_state);
    public Task SaveAsync(UiState state)
    {
        _state = new UiState { GroupExpansionById = new(state.GroupExpansionById, StringComparer.Ordinal) };
        return Task.CompletedTask;
    }
}
