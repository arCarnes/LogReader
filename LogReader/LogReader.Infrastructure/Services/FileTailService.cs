namespace LogReader.Infrastructure.Services;

using System.Text;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public class FileTailService : IFileTailService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TailState> _tailedFiles = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<TailEventArgs>? LinesAppended;
    public event EventHandler<FileRotatedEventArgs>? FileRotated;
    public event EventHandler<TailErrorEventArgs>? TailError;

    public void StartTailing(string filePath, FileEncoding encoding, int pollingIntervalMs = 250)
    {
        lock (_gate)
        {
            if (_isDisposed != 0)
                return;

            var normalizedInterval = Math.Max(100, pollingIntervalMs);
            if (_tailedFiles.TryGetValue(filePath, out var existing))
            {
                existing.AddReference(normalizedInterval);
                return;
            }

            var cts = new CancellationTokenSource();
            var state = new TailState
            {
                FilePath = filePath,
                Encoding = encoding,
                PollingIntervalMs = normalizedInterval,
                Cts = cts
            };
            _tailedFiles[filePath] = state;
            state.Task = Task.Run(() => TailLoopAsync(state, cts.Token));
        }
    }

    public void StopTailing(string filePath)
    {
        TailState? stateToCancel = null;
        lock (_gate)
        {
            if (_tailedFiles.TryGetValue(filePath, out var state) && state.ReleaseReference() == 0)
            {
                _tailedFiles.Remove(filePath);
                stateToCancel = state;
            }
        }

        if (stateToCancel != null)
            CancelState(stateToCancel);
    }

    public void StopAll()
    {
        List<TailState> states;
        lock (_gate)
            states = RemoveAllStates();

        foreach (var state in states)
            CancelState(state);
    }

    public void Dispose()
    {
        List<TailState> states;
        lock (_gate)
        {
            if (_isDisposed != 0)
                return;

            _isDisposed = 1;
            states = RemoveAllStates();
        }

        foreach (var state in states)
            CancelState(state);
    }

    private int _isDisposed;

    private async Task TailLoopAsync(TailState state, CancellationToken ct)
    {
        long lastSize = 0;
        string? lastCreationTimeId = null;

        try
        {
            // Get initial file state
            if (File.Exists(state.FilePath))
            {
                var info = new FileInfo(state.FilePath);
                lastSize = info.Length;
                lastCreationTimeId = GetFileIdentity(state.FilePath);
            }

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(state.PollingIntervalMs, ct);

                if (!File.Exists(state.FilePath))
                {
                    // File was deleted - might be rotation in progress
                    await Task.Delay(500, ct); // Wait a bit for new file
                    if (File.Exists(state.FilePath))
                    {
                        // File recreated - rotation detected
                        RaiseFileRotated(state.FilePath);
                        lastSize = 0;
                        lastCreationTimeId = GetFileIdentity(state.FilePath);
                    }
                    continue;
                }

                var fileInfo = new FileInfo(state.FilePath);
                var currentSize = fileInfo.Length;
                var currentIdentity = GetFileIdentity(state.FilePath);

                // Rotation detection: file identity changed (creation time changed = new file)
                if (lastCreationTimeId != null && currentIdentity != lastCreationTimeId)
                {
                    RaiseFileRotated(state.FilePath);
                    lastSize = 0;
                    lastCreationTimeId = currentIdentity;
                }
                // File was truncated (smaller than before) - also a rotation/reset
                else if (currentSize < lastSize)
                {
                    RaiseFileRotated(state.FilePath);
                    lastSize = 0;
                    lastCreationTimeId = currentIdentity;
                }

                // Notify if file grew
                if (currentSize > lastSize)
                {
                    RaiseLinesAppended(state.FilePath);
                    lastSize = currentSize;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (_tailedFiles.TryGetValue(state.FilePath, out var current) && ReferenceEquals(current, state))
                    _tailedFiles.Remove(state.FilePath);
            }

            RaiseTailErrorSafely(state.FilePath, ex.Message);
        }
        finally
        {
            state.ScheduleCancellationSourceDisposal();
        }
    }

    private void RaiseLinesAppended(string filePath)
        => RaiseObserverEvent(
            LinesAppended,
            new TailEventArgs
            {
                FilePath = filePath
            },
            filePath);

    private void RaiseFileRotated(string filePath)
        => RaiseObserverEvent(
            FileRotated,
            new FileRotatedEventArgs
            {
                FilePath = filePath
            },
            filePath);

    private void RaiseObserverEvent<TEventArgs>(
        EventHandler<TEventArgs>? handlers,
        TEventArgs args,
        string filePath)
        where TEventArgs : EventArgs
    {
        if (handlers == null)
            return;

        foreach (EventHandler<TEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception ex)
            {
                RaiseTailErrorSafely(filePath, ex.Message);
            }
        }
    }

    private void RaiseTailErrorSafely(string filePath, string errorMessage)
    {
        var handlers = TailError;
        if (handlers == null)
            return;

        var args = new TailErrorEventArgs
        {
            FilePath = filePath,
            ErrorMessage = errorMessage
        };

        foreach (EventHandler<TailErrorEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch
            {
                // Never allow observer exceptions to crash the tail worker.
            }
        }
    }

    private List<TailState> RemoveAllStates()
    {
        var removedStates = new List<TailState>();
        foreach (var entry in _tailedFiles)
        {
            removedStates.Add(entry.Value);
        }

        _tailedFiles.Clear();
        return removedStates;
    }

    private static void CancelState(TailState state)
    {
        state.Cts.Cancel();
    }

    private static string? GetFileIdentity(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            return info.CreationTimeUtc.Ticks.ToString();
        }
        catch
        {
            return null;
        }
    }

    private class TailState
    {
        private int _ctsDisposalScheduled;
        private int _ctsDisposed;
        private int _referenceCount = 1;
        private int _pollingIntervalMs;

        public string FilePath { get; init; } = string.Empty;
        public FileEncoding Encoding { get; init; }
        public int PollingIntervalMs
        {
            get => Volatile.Read(ref _pollingIntervalMs);
            init => _pollingIntervalMs = value;
        }

        public CancellationTokenSource Cts { get; init; } = null!;
        public Task Task { get; set; } = Task.CompletedTask;

        public void AddReference(int pollingIntervalMs)
        {
            _referenceCount++;
            if (pollingIntervalMs < PollingIntervalMs)
                Volatile.Write(ref _pollingIntervalMs, pollingIntervalMs);
        }

        public int ReleaseReference()
        {
            if (_referenceCount <= 0)
                return 0;

            _referenceCount--;
            return _referenceCount;
        }

        public void ScheduleCancellationSourceDisposal()
        {
            if (Interlocked.Exchange(ref _ctsDisposalScheduled, 1) != 0)
                return;

            if (Task.IsCompleted)
            {
                DisposeCancellationSource();
                return;
            }

            _ = Task.ContinueWith(
                static (_, stateObj) => ((TailState)stateObj!).DisposeCancellationSource(),
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void DisposeCancellationSource()
        {
            if (Interlocked.Exchange(ref _ctsDisposed, 1) != 0)
                return;

            Cts.Dispose();
        }
    }
}
