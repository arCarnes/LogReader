namespace LogReader.Infrastructure.Services;

using System.Diagnostics;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public class FileTailService : IFileTailService
{
    private const int RequiredConsistentObservations = 2;

    private readonly object _gate = new();
    private readonly Dictionary<string, TailState> _tailedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, TailFileSnapshot> _probeFile;
    private readonly TimeSpan _missingGracePeriod;
    private long _availabilitySequence;

    public FileTailService()
        : this(ProbeFile, TimeSpan.FromSeconds(2))
    {
    }

    internal FileTailService(Func<string, TailFileSnapshot> probeFile, TimeSpan? missingGracePeriod = null)
    {
        _probeFile = probeFile;
        _missingGracePeriod = missingGracePeriod ?? TimeSpan.FromSeconds(2);
        if (_missingGracePeriod < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(missingGracePeriod));
    }

    public event EventHandler<TailEventArgs>? LinesAppended;
    public event EventHandler<FileRotatedEventArgs>? FileRotated;
    public event EventHandler<FileAvailabilityChangedEventArgs>? FileAvailabilityChanged;
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
        long? missingSinceTimestamp = null;
        var missingPublished = false;
        var recoveryConfirmationPending = false;
        TailObservationCandidate? pendingCandidate = null;
        var consistentObservationCount = 0;

        void PublishMissingIfGraceElapsed()
        {
            if (missingPublished || missingSinceTimestamp == null)
                return;

            if (Stopwatch.GetElapsedTime(missingSinceTimestamp.Value) < _missingGracePeriod)
                return;

            missingPublished = true;
            RaiseFileAvailabilityChanged(state.FilePath, isAvailable: false);
        }

        void ClearPendingCandidate()
        {
            pendingCandidate = null;
            consistentObservationCount = 0;
        }

        try
        {
            if (TryProbeFile(state.FilePath, out var initialSnapshot))
            {
                lastSize = initialSnapshot.Exists ? initialSnapshot.Length : 0;
                lastCreationTimeId = initialSnapshot.Identity;
                if (!initialSnapshot.Exists)
                    missingSinceTimestamp = Stopwatch.GetTimestamp();
            }

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(state.PollingIntervalMs, ct);

                if (!TryProbeFile(state.FilePath, out var snapshot))
                {
                    ClearPendingCandidate();
                    await Task.Delay(500, ct);
                    continue;
                }

                if (!snapshot.Exists)
                {
                    ClearPendingCandidate();
                    recoveryConfirmationPending = false;
                    missingSinceTimestamp ??= Stopwatch.GetTimestamp();
                    PublishMissingIfGraceElapsed();

                    // Preserve the existing short rollover grace probe before declaring a new generation.
                    await Task.Delay(500, ct);
                    if (!TryProbeFile(state.FilePath, out snapshot))
                    {
                        ClearPendingCandidate();
                        continue;
                    }

                    if (!snapshot.Exists)
                    {
                        ClearPendingCandidate();
                        PublishMissingIfGraceElapsed();
                        continue;
                    }
                }

                if (missingSinceTimestamp != null)
                {
                    missingSinceTimestamp = null;
                    if (missingPublished)
                    {
                        missingPublished = false;
                        recoveryConfirmationPending = true;
                        RaiseFileAvailabilityChanged(state.FilePath, isAvailable: true);
                    }
                }

                var currentSize = snapshot.Length;
                var currentIdentity = snapshot.Identity;
                TailObservationCandidate? candidate = null;
                if (recoveryConfirmationPending)
                {
                    candidate = new TailObservationCandidate(
                        TailObservationKind.RecreatedAfterMissing,
                        currentIdentity);
                }
                else if (lastCreationTimeId != null &&
                    currentIdentity != null &&
                    currentIdentity != lastCreationTimeId)
                {
                    candidate = new TailObservationCandidate(
                        TailObservationKind.IdentityChanged,
                        currentIdentity);
                }
                else if (currentSize < lastSize)
                {
                    candidate = new TailObservationCandidate(
                        TailObservationKind.Truncated,
                        Identity: null);
                }
                else if (currentSize > lastSize)
                {
                    candidate = new TailObservationCandidate(
                        TailObservationKind.Grew,
                        Identity: null);
                }

                if (candidate == null)
                {
                    ClearPendingCandidate();
                    if (lastCreationTimeId == null && currentIdentity != null)
                        lastCreationTimeId = currentIdentity;
                    continue;
                }

                if (pendingCandidate != candidate)
                {
                    pendingCandidate = candidate;
                    consistentObservationCount = 1;
                    continue;
                }

                consistentObservationCount++;
                if (consistentObservationCount < RequiredConsistentObservations)
                    continue;

                ClearPendingCandidate();
                if (candidate.Value.Kind == TailObservationKind.Grew)
                {
                    RaiseLinesAppended(state.FilePath);
                    lastSize = currentSize;
                    if (lastCreationTimeId == null && currentIdentity != null)
                        lastCreationTimeId = currentIdentity;
                    continue;
                }

                var changeHint = candidate.Value.Kind switch
                {
                    TailObservationKind.IdentityChanged => FileChangeHint.IdentityChanged,
                    TailObservationKind.Truncated => FileChangeHint.Truncated,
                    TailObservationKind.RecreatedAfterMissing => FileChangeHint.RecreatedAfterMissing,
                    _ => FileChangeHint.UnspecifiedReplacement
                };
                RaiseFileRotated(state.FilePath, changeHint);
                recoveryConfirmationPending = false;
                lastSize = currentSize;
                lastCreationTimeId = currentIdentity;
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

    private void RaiseFileRotated(string filePath, FileChangeHint changeHint)
        => RaiseObserverEvent(
            FileRotated,
            new FileRotatedEventArgs
            {
                FilePath = filePath,
                ChangeHint = changeHint
            },
            filePath);

    private void RaiseFileAvailabilityChanged(string filePath, bool isAvailable)
        => RaiseObserverEvent(
            FileAvailabilityChanged,
            new FileAvailabilityChangedEventArgs
            {
                FilePath = filePath,
                IsAvailable = isAvailable,
                Sequence = Interlocked.Increment(ref _availabilitySequence)
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

    private bool TryProbeFile(string filePath, out TailFileSnapshot snapshot)
    {
        try
        {
            snapshot = _probeFile(filePath);
            return true;
        }
        catch (Exception ex) when (IsRecoverableMetadataException(ex))
        {
            snapshot = default;
            return false;
        }
    }

    private static bool IsRecoverableMetadataException(Exception ex)
        => ex is IOException or UnauthorizedAccessException;

    private static TailFileSnapshot ProbeFile(string filePath)
    {
        try
        {
            using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1,
                FileOptions.RandomAccess);
            var generationToken = FileGenerationTokenProvider.Capture(stream);
            return new TailFileSnapshot(
                true,
                stream.Length,
                generationToken.IsKnown
                    ? $"{generationToken.VolumeId:X16}:{generationToken.FileId:X16}"
                    : null);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return TailFileSnapshot.Missing;
        }
    }

    internal readonly record struct TailFileSnapshot(bool Exists, long Length, string? Identity)
    {
        public static TailFileSnapshot Missing { get; } = new(false, 0, null);
    }

    private enum TailObservationKind
    {
        Grew,
        Truncated,
        IdentityChanged,
        RecreatedAfterMissing
    }

    private readonly record struct TailObservationCandidate(
        TailObservationKind Kind,
        string? Identity);

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
