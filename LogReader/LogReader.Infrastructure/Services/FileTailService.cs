namespace LogReader.Infrastructure.Services;

using System.Collections.Concurrent;
using System.Text;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public class FileTailService : IFileTailService
{
    private readonly ConcurrentDictionary<string, TailState> _tailedFiles = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<TailEventArgs>? LinesAppended;
    public event EventHandler<FileRotatedEventArgs>? FileRotated;
    public event EventHandler<TailErrorEventArgs>? TailError;

    public void StartTailing(string filePath, FileEncoding encoding, int pollingIntervalMs = 250)
    {
        if (Volatile.Read(ref _isDisposed) != 0)
            return;

        var cts = new CancellationTokenSource();
        var state = new TailState
        {
            FilePath = filePath,
            Encoding = encoding,
            PollingIntervalMs = Math.Max(100, pollingIntervalMs),
            Cts = cts
        };

        if (!_tailedFiles.TryAdd(filePath, state))
        {
            cts.Dispose();
            return;
        }

        state.Task = Task.Run(() => TailLoopAsync(state, cts.Token));

        // Re-check after publishing: if Dispose() already swept, clean up ourselves.
        if (Volatile.Read(ref _isDisposed) != 0
            && _tailedFiles.TryRemove(filePath, out _))
        {
            CancelState(state);
        }
    }

    public void StopTailing(string filePath)
    {
        if (_tailedFiles.TryRemove(filePath, out var state))
            CancelState(state);
    }

    public void StopAll()
    {
        foreach (var state in RemoveAllStates())
            CancelState(state);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            return;

        var states = RemoveAllStates();
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
            RaiseTailErrorSafely(state.FilePath, ex.Message);

            _tailedFiles.TryRemove(state.FilePath, out _);
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
        foreach (var entry in _tailedFiles.ToArray())
        {
            if (_tailedFiles.TryRemove(entry.Key, out var state))
                removedStates.Add(state);
        }

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

        public string FilePath { get; init; } = string.Empty;
        public FileEncoding Encoding { get; init; }
        public int PollingIntervalMs { get; init; }
        public CancellationTokenSource Cts { get; init; } = null!;
        public Task Task { get; set; } = Task.CompletedTask;

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
