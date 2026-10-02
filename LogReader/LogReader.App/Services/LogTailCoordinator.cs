namespace LogReader.App.Services;

using System.IO;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

internal sealed class LogTailCoordinator : IDisposable
{
    private static readonly int[] RecoveryBackoffSeconds = [2, 5, 15, 30, 60, 120, 300];
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _recoveryLifetime = new();
    private CancellationTokenSource? _recoveryWait;
    private long _recoveryRevision;
    private long _recoveryScheduledAt;
    private TimeSpan _recoveryDelay;
    private int _recoveryFailureCount;
    private AutomaticReloadBlockedException? _recoveryFailure;
    private bool _recoveryRetryable;
    private bool _notificationNeedsRevalidation;
    private string? _recoveryMonitorError;
    private readonly FileSession _owner;
    private readonly IFileTailService _tailService;
    private readonly SemaphoreSlim _tailUpdateGate = new(1, 1);
    private readonly object _pendingUpdateGate = new();

    private int _tailPollingIntervalMs = 250;
    private int _tailRequestActive;
    private long _latestAvailabilitySequence;
    private bool _appendPending;
    private bool _tailUpdateDrainActive;
    private FileChangeHint _pendingChangeHint;
    private FileChangeHint _pausedChangeHint;
    private PendingIndexNotification? _pendingIndexNotification;

    public LogTailCoordinator(FileSession owner, IFileTailService tailService, TimeProvider timeProvider)
    {
        _owner = owner;
        _timeProvider = timeProvider;
        _tailService = tailService;
        _tailService.LinesAppended += OnLinesAppended;
        _tailService.FileRotated += OnFileRotated;
        _tailService.FileAvailabilityChanged += OnFileAvailabilityChanged;
        _tailService.TailError += OnTailError;
    }

    public async Task StartLoadedTailingAsync()
    {
        await _tailUpdateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_owner.IsShutdownOrDisposed)
                return;

            ClearRecovery();

            await PublishAutomaticReloadPausedStateAsync(false).ConfigureAwait(false);
            await StartTailRequestAsync(_tailPollingIntervalMs).ConfigureAwait(false);
            await PublishSuspendedStateAsync(false).ConfigureAwait(false);

            var previousTotalLines = await ReadPublishedTotalLinesAsync().ConfigureAwait(false);
            var updateResult = await _owner.UpdateLineIndexAsync(CancellationToken.None).ConfigureAwait(false);
            if (_owner.IsShutdownOrDisposed)
            {
                SuspendTailing();
                return;
            }

            await NotifyCommittedIndexUpdateAsync(previousTotalLines, updateResult).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (AutomaticReloadBlockedException ex)
        {
            await PauseForAutomaticReloadAsync(ex, FileChangeHint.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (HasPendingIndexNotification)
        {
            await PauseForAutomaticReloadAsync(CreateRecoveryFailure(ex), FileChangeHint.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await PublishSuspendedStateAsync(true).ConfigureAwait(false);
            await NotifyClientsOnSessionContextAsync(client => client.SetStatusText($"Tail error: {ex.Message}")).ConfigureAwait(false);
        }
        finally
        {
            _tailUpdateGate.Release();
        }
    }

    public void SuspendTailing()
    {
        CancelRecoveryWait();
        if (_owner.IsAutomaticReloadPaused)
            _ = PublishRecoveryStatusAsync();
        if (_owner.IsSuspended && !IsTailRequestActive)
            return;

        StopTailRequest();
        _ = PublishSuspendedStateAsync(true);
    }

    public void ResumeTailing()
    {
        if (_owner.IsShutdownOrDisposed)
            return;
        if (_owner.IsAutomaticReloadPaused)
        {
            ScheduleRecovery();
            return;
        }

        _ = ResumeTailingWithCatchUpAsync(_tailPollingIntervalMs);
    }

    public void ApplyVisibleTailingMode(int pollingIntervalMs)
    {
        if (_owner.IsShutdownOrDisposed)
            return;
        if (_owner.IsAutomaticReloadPaused)
        {
            ScheduleRecovery();
            return;
        }

        _ = ResumeTailingWithCatchUpAsync(pollingIntervalMs);
    }

    public async Task ResumeTailingWithCatchUpAsync(int pollingIntervalMs)
    {
        if (_owner.IsShutdownOrDisposed || _owner.IsAutomaticReloadPaused)
        {
            if (_owner.IsShutdownOrDisposed)
                SuspendTailing();
            else
                ScheduleRecovery();
            return;
        }

        await _tailUpdateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ResumeTailingWithCatchUpCoreAsync(pollingIntervalMs).ConfigureAwait(false);
        }
        finally
        {
            _tailUpdateGate.Release();
        }
    }

    private async Task ResumeTailingWithCatchUpCoreAsync(int pollingIntervalMs)
    {
        if (_owner.IsShutdownOrDisposed || _owner.IsAutomaticReloadPaused)
        {
            if (_owner.IsShutdownOrDisposed)
                SuspendTailing();
            else
                ScheduleRecovery();
            return;
        }

        if (_owner.HasNoLineIndex || _owner.IsLoading)
            return;

        if (!_owner.HasVisibleClientsForTailing)
        {
            SuspendTailing();
            return;
        }

        pollingIntervalMs = Math.Max(100, pollingIntervalMs);
        var wasSuspended = _owner.IsSuspended;
        var needsRestart = wasSuspended || !IsTailRequestActive;
        if (!needsRestart && _tailPollingIntervalMs == pollingIntervalMs)
            return;

        string? catchUpErrorMessage = null;
        var startedDuringResume = false;
        int? previousTotalLines = null;
        LineIndexUpdateResult? updateResult = null;
        try
        {
            if (needsRestart)
            {
                await StartTailRequestAsync(pollingIntervalMs).ConfigureAwait(false);
                _tailPollingIntervalMs = pollingIntervalMs;
                startedDuringResume = true;
                await PublishSuspendedStateAsync(false).ConfigureAwait(false);

                previousTotalLines = await ReadPublishedTotalLinesAsync().ConfigureAwait(false);
                updateResult = await _owner.UpdateLineIndexAsync(CancellationToken.None).ConfigureAwait(false);
                if (updateResult != null && _owner.IsShutdownOrDisposed)
                {
                    SuspendTailing();
                    return;
                }
            }
            else
            {
                StopTailRequest();
                previousTotalLines = await ReadPublishedTotalLinesAsync().ConfigureAwait(false);
                updateResult = await _owner.UpdateLineIndexAsync(CancellationToken.None).ConfigureAwait(false);
                if (updateResult != null && _owner.IsShutdownOrDisposed)
                {
                    SuspendTailing();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (AutomaticReloadBlockedException ex)
        {
            await PauseForAutomaticReloadAsync(ex, FileChangeHint.None).ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (IsRecoverableIo(ex))
        {
            await PauseForAutomaticReloadAsync(CreateRecoveryFailure(ex), FileChangeHint.None).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            catchUpErrorMessage = ex.Message;
        }

        if (_owner.IsShutdownOrDisposed)
        {
            SuspendTailing();
            return;
        }

        if (!_owner.HasVisibleClientsForTailing)
        {
            SuspendTailing();
            return;
        }

        try
        {
            if (!startedDuringResume)
            {
                await NotifyCommittedIndexUpdateAsync(previousTotalLines, updateResult).ConfigureAwait(false);

                await StartTailRequestAsync(pollingIntervalMs).ConfigureAwait(false);
                _tailPollingIntervalMs = pollingIntervalMs;
                await PublishSuspendedStateAsync(false).ConfigureAwait(false);
            }

            if (startedDuringResume)
                await NotifyCommittedIndexUpdateAsync(previousTotalLines, updateResult).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(catchUpErrorMessage))
                await NotifyClientsOnSessionContextAsync(client => client.SetStatusText($"Tail resumed (catch-up skipped): {catchUpErrorMessage}")).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) when (HasPendingIndexNotification)
        {
            await PauseForAutomaticReloadAsync(CreateRecoveryFailure(ex), FileChangeHint.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await PublishSuspendedStateAsync(true).ConfigureAwait(false);
            await NotifyClientsOnSessionContextAsync(client => client.SetStatusText($"Tail error: {ex.Message}")).ConfigureAwait(false);
        }
    }

    public void BeginShutdown()
    {
        _recoveryLifetime.Cancel();
        CancelRecoveryWait();
        ClearPendingTailUpdates();
        StopTailRequest();
        _ = PublishSuspendedStateAsync(true);
    }

    public void Dispose()
    {
        BeginShutdown();
        _tailService.LinesAppended -= OnLinesAppended;
        _tailService.FileRotated -= OnFileRotated;
        _tailService.FileAvailabilityChanged -= OnFileAvailabilityChanged;
        _tailService.TailError -= OnTailError;
    }

    private void OnLinesAppended(object? sender, TailEventArgs e)
    {
        if (_owner.IsShutdownOrDisposed || !string.Equals(e.FilePath, _owner.FilePath, StringComparison.OrdinalIgnoreCase))
            return;

        QueueTailUpdate(FileChangeHint.None);
    }

    private void OnFileRotated(object? sender, FileRotatedEventArgs e)
    {
        if (_owner.IsShutdownOrDisposed || !string.Equals(e.FilePath, _owner.FilePath, StringComparison.OrdinalIgnoreCase))
            return;

        QueueTailUpdate(e.ChangeHint);
    }

    private void QueueTailUpdate(FileChangeHint changeHint)
    {
        var startDrain = false;
        lock (_pendingUpdateGate)
        {
            if (_owner.IsShutdownOrDisposed)
                return;
            if (_owner.IsAutomaticReloadPaused)
            {
                if (changeHint == FileChangeHint.None)
                    _appendPending = true;
                else if (GetChangeHintPriority(changeHint) > GetChangeHintPriority(_pausedChangeHint))
                    _pausedChangeHint = changeHint;
                return;
            }

            if (changeHint == FileChangeHint.None)
            {
                _appendPending = true;
            }
            else if (GetChangeHintPriority(changeHint) >
                     GetChangeHintPriority(_pendingChangeHint))
            {
                _pendingChangeHint = changeHint;
            }

            if (!_tailUpdateDrainActive)
            {
                _tailUpdateDrainActive = true;
                startDrain = true;
            }
        }

        if (startDrain)
            _ = ProcessPendingTailUpdatesAsync();
    }

    private async Task ProcessPendingTailUpdatesAsync()
    {
        try
        {
            while (TryTakePendingTailUpdate(out var changeHint))
            {
                await _tailUpdateGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_owner.IsShutdownOrDisposed)
                        return;
                    if (_owner.IsAutomaticReloadPaused)
                    {
                        QueueTailUpdate(changeHint);
                        return;
                    }

                    if (changeHint != FileChangeHint.None)
                    {
                        await NotifyClientsOnSessionContextAsync(
                            client => client.SetStatusText("File changed, checking...")).ConfigureAwait(false);
                    }

                    var previousTotalLines = await ReadPublishedTotalLinesAsync().ConfigureAwait(false);
                    var updateResult = await _owner.UpdateLineIndexAsync(
                        CancellationToken.None,
                        changeHint).ConfigureAwait(false);
                    if (_owner.IsShutdownOrDisposed)
                        return;

                    await NotifyCommittedIndexUpdateAsync(previousTotalLines, updateResult).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
                catch (AutomaticReloadBlockedException ex)
                {
                    await PauseForAutomaticReloadAsync(ex, changeHint).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (changeHint != FileChangeHint.None || HasPendingIndexNotification || IsRecoverableIo(ex))
                {
                    await PauseForAutomaticReloadAsync(CreateRecoveryFailure(ex),
                        HasPendingIndexNotification ? FileChangeHint.None : changeHint).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex)
                {
                    await NotifyClientsOnSessionContextAsync(
                        client => client.SetStatusText($"Tail error: {ex.Message}")).ConfigureAwait(false);
                }
                finally
                {
                    _tailUpdateGate.Release();
                }
            }
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            await NotifyClientsOnSessionContextAsync(
                client => client.SetStatusText($"Tail error: {ex.Message}")).ConfigureAwait(false);
        }
        finally
        {
            RestartTailUpdateDrainIfNeeded();
        }
    }

    private bool TryTakePendingTailUpdate(out FileChangeHint changeHint)
    {
        lock (_pendingUpdateGate)
        {
            if (_owner.IsShutdownOrDisposed || _owner.IsAutomaticReloadPaused)
            {
                changeHint = FileChangeHint.None;
                return false;
            }

            if (_pendingChangeHint != FileChangeHint.None)
            {
                changeHint = _pendingChangeHint;
                _pendingChangeHint = FileChangeHint.None;
                _appendPending = false;
                return true;
            }

            if (_appendPending)
            {
                _appendPending = false;
                changeHint = FileChangeHint.None;
                return true;
            }

            changeHint = FileChangeHint.None;
            return false;
        }
    }

    private void RestartTailUpdateDrainIfNeeded()
    {
        var restart = false;
        lock (_pendingUpdateGate)
        {
            _tailUpdateDrainActive = false;
            if (!_owner.IsShutdownOrDisposed &&
                !_owner.IsAutomaticReloadPaused &&
                (_appendPending || _pendingChangeHint != FileChangeHint.None))
            {
                _tailUpdateDrainActive = true;
                restart = true;
            }
        }

        if (restart)
            _ = ProcessPendingTailUpdatesAsync();
    }

    private void ClearPendingTailUpdates()
    {
        lock (_pendingUpdateGate)
        {
            _appendPending = false;
            _pendingChangeHint = FileChangeHint.None;
            _pendingIndexNotification = null;
        }
    }

    private async void OnTailError(object? sender, TailErrorEventArgs e)
    {
        if (_owner.IsShutdownOrDisposed || !string.Equals(e.FilePath, _owner.FilePath, StringComparison.OrdinalIgnoreCase))
            return;
        lock (_pendingUpdateGate)
        {
            if (_owner.IsAutomaticReloadPaused)
            {
                _recoveryMonitorError = e.ErrorMessage;
                MarkTailRequestInactive();
                return;
            }
        }

        try
        {
            ClearPendingTailUpdates();
            await _owner.InvokeOnSessionContextAsync(() =>
            {
                if (_owner.IsShutdownOrDisposed)
                    return;

                MarkTailRequestInactive();
                _owner.IsSuspended = true;
                NotifyClients(client => client.SetStatusText($"Tailing stopped: {e.ErrorMessage}"));
            }).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            await NotifyClientsOnSessionContextAsync(client => client.SetStatusText($"Tail error: {ex.Message}")).ConfigureAwait(false);
        }
    }

    public Task RetryAutomaticTailingAsync()
    {
        CancelRecoveryWait();
        return RetryAutomaticTailingCoreAsync(isManual: true);
    }

    private async Task RetryAutomaticTailingCoreAsync(bool isManual, long? revision = null, CancellationToken waitToken = default)
    {
        if (_owner.IsShutdownOrDisposed || !_owner.IsAutomaticReloadPaused)
            return;

        await _tailUpdateGate.WaitAsync(waitToken).ConfigureAwait(false);
        try
        {
            if (_owner.IsShutdownOrDisposed || !_owner.IsAutomaticReloadPaused)
                return;
            lock (_pendingUpdateGate)
            {
                if (!isManual && (revision != _recoveryRevision || !_owner.HasVisibleClientsForTailing))
                    return;
            }

            PendingIndexNotification? pendingNotification;
            bool revalidate;
            lock (_pendingUpdateGate)
            {
                pendingNotification = _pendingIndexNotification;
                revalidate = _notificationNeedsRevalidation;
                _recoveryMonitorError = null;
            }

            if (pendingNotification == null || revalidate)
            {
                if (isManual && pendingNotification == null)
                    await _owner.ResetAutomaticReloadDelayAsync().ConfigureAwait(false);
                await UpdateRecoveryIndexAsync().ConfigureAwait(false);
            }

            if (_owner.IsShutdownOrDisposed)
                return;

            // Monitor before the final catch-up. Its baseline is the committed snapshot,
            // so writes between the scan, monitor startup and publication remain observable.
            if (_owner.HasVisibleClientsForTailing)
            {
                await StartTailRequestAsync(_tailPollingIntervalMs).ConfigureAwait(false);
                if (_owner.HasVisibleClientsForTailing && !_owner.IsShutdownOrDisposed)
                    await UpdateRecoveryIndexAsync().ConfigureAwait(false);
                else
                    StopTailRequest();
            }

            PendingIndexNotification? notification;
            lock (_pendingUpdateGate)
                notification = _pendingIndexNotification;
            await NotifyIndexUpdateAsync(notification?.PreviousTotalLines, notification?.UpdateResult,
                _recoveryLifetime.Token).ConfigureAwait(false);
            if (_owner.IsShutdownOrDisposed)
                return;

            await _owner.InvokeOnSessionContextAsync(() =>
            {
                lock (_pendingUpdateGate)
                {
                    if (_recoveryMonitorError is { } error)
                        throw new InvalidOperationException($"Tailing stopped during recovery: {error}");
                    if (GetChangeHintPriority(_pausedChangeHint) > GetChangeHintPriority(_pendingChangeHint))
                        _pendingChangeHint = _pausedChangeHint;
                    ClearRecovery();
                    _owner.IsAutomaticReloadPaused = false;
                    _owner.AutomaticReloadStatusText = null;
                    _owner.AutomaticReloadFailureDetail = null;
                }
            }).ConfigureAwait(false);
            if (_owner.HasVisibleClientsForTailing && !_owner.IsShutdownOrDisposed)
                await PublishSuspendedStateAsync(false).ConfigureAwait(false);
            else
                StopTailRequest();

            var totalLines = await ReadPublishedTotalLinesAsync().ConfigureAwait(false);
            await NotifyClientsOnSessionContextAsync(
                client => client.SetStatusText($"{totalLines:N0} lines")).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (AutomaticReloadBlockedException ex)
        {
            FileChangeHint changeHint;
            lock (_pendingUpdateGate)
                changeHint = _pausedChangeHint;
            await PauseForAutomaticReloadAsync(ex, changeHint).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await PauseForAutomaticReloadAsync(CreateRecoveryFailure(ex), FileChangeHint.None).ConfigureAwait(false);
        }
        finally
        {
            _tailUpdateGate.Release();
            StartTailUpdateDrainIfNeeded();
        }
    }

    private async Task UpdateRecoveryIndexAsync()
    {
        FileChangeHint hint;
        bool appendPending;
        lock (_pendingUpdateGate)
        {
            appendPending = _appendPending;
            hint = _pausedChangeHint;
            _pausedChangeHint = FileChangeHint.None;
            _appendPending = false;
        }
        try
        {
            var previousTotal = await ReadPublishedTotalLinesAsync().ConfigureAwait(false);
            var update = await _owner.UpdateLineIndexAsync(_recoveryLifetime.Token, hint).ConfigureAwait(false);
            RecordCommittedIndexUpdate(previousTotal, update);
            lock (_pendingUpdateGate)
                _notificationNeedsRevalidation = false;
        }
        catch
        {
            lock (_pendingUpdateGate)
            {
                _appendPending |= appendPending;
                if (GetChangeHintPriority(hint) > GetChangeHintPriority(_pausedChangeHint))
                    _pausedChangeHint = hint;
            }
            throw;
        }
    }

    private void StartTailUpdateDrainIfNeeded()
    {
        lock (_pendingUpdateGate)
        {
            if (_tailUpdateDrainActive || _owner.IsShutdownOrDisposed || _owner.IsAutomaticReloadPaused ||
                !_owner.HasVisibleClientsForTailing || (!_appendPending && _pendingChangeHint == FileChangeHint.None))
                return;
            _tailUpdateDrainActive = true;
        }
        _ = ProcessPendingTailUpdatesAsync();
    }

    private static AutomaticReloadBlockedException CreateRecoveryFailure(Exception exception)
        => new($"Retry failed: {exception.Message}", innerException: exception,
            reason: AutomaticReloadReason.ReloadFailed,
            isRetryable: IsRecoverableIo(exception));

    private static bool IsRecoverableIo(Exception exception)
        => exception is not LineIndexCapacityExceededException &&
           exception is IOException or UnauthorizedAccessException;

    private async Task PauseForAutomaticReloadAsync(
        AutomaticReloadBlockedException exception,
        FileChangeHint changeHint)
    {
        CancelRecoveryWait();
        lock (_pendingUpdateGate)
        {
            if (GetChangeHintPriority(_pendingChangeHint) > GetChangeHintPriority(changeHint))
                changeHint = _pendingChangeHint;
            if (GetChangeHintPriority(changeHint) > GetChangeHintPriority(_pausedChangeHint))
                _pausedChangeHint = changeHint;

            if (exception.Reason != AutomaticReloadReason.Cooldown || _recoveryFailure == null)
                _recoveryFailure = exception;
            if (exception.Reason != AutomaticReloadReason.Cooldown)
                _recoveryFailureCount = Math.Min(_recoveryFailureCount + 1, RecoveryBackoffSeconds.Length);
            _recoveryRetryable = exception.IsRetryable;
            var backoff = TimeSpan.FromSeconds(RecoveryBackoffSeconds[Math.Max(0, _recoveryFailureCount - 1)]);
            _recoveryDelay = exception.RetryAfter is { } delay && delay > backoff ? delay : backoff;
            _recoveryScheduledAt = _timeProvider.GetTimestamp();
            if (_pendingIndexNotification != null && exception.InnerException is IOException or UnauthorizedAccessException)
                _notificationNeedsRevalidation = true;
            _pendingChangeHint = FileChangeHint.None;
        }

        StopTailRequest();
        await _owner.InvokeOnSessionContextAsync(() =>
        {
            if (_owner.IsShutdownOrDisposed)
                return;
            _owner.IsAutomaticReloadPaused = true;
            _owner.IsSuspended = true;
        }).ConfigureAwait(false);
        await PublishRecoveryStatusAsync().ConfigureAwait(false);
        ScheduleRecovery();
    }

    private void ScheduleRecovery()
    {
        CancellationTokenSource wait;
        long revision;
        TimeSpan delay;
        lock (_pendingUpdateGate)
        {
            if (_owner.IsShutdownOrDisposed || !_owner.IsAutomaticReloadPaused ||
                !_recoveryRetryable || !_owner.HasVisibleClientsForTailing || _recoveryWait != null)
                return;
            delay = GetRemainingRecoveryDelay();
            wait = CancellationTokenSource.CreateLinkedTokenSource(_recoveryLifetime.Token);
            _recoveryWait = wait;
            revision = ++_recoveryRevision;
        }
        _ = PublishRecoveryStatusAsync();
        _ = RunRecoveryAsync(wait, revision, delay);
    }

    private async Task RunRecoveryAsync(CancellationTokenSource wait, long revision, TimeSpan delay)
    {
        try
        {
            // Task.Run prevents an already-expired deadline from executing a scan inline on the UI thread.
            var scheduledDelay = Task.Delay(delay, _timeProvider, wait.Token);
            await Task.Run(async () =>
            {
                await scheduledDelay.ConfigureAwait(false);
                await RetryAutomaticTailingCoreAsync(false, revision, wait.Token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            lock (_pendingUpdateGate)
            {
                _recoveryFailure = CreateRecoveryFailure(ex);
                _recoveryRetryable = false;
            }
            await PublishRecoveryStatusAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (_pendingUpdateGate)
            {
                if (ReferenceEquals(_recoveryWait, wait))
                    _recoveryWait = null;
            }
            wait.Dispose();
            ScheduleRecovery();
        }
    }

    private void CancelRecoveryWait()
    {
        lock (_pendingUpdateGate)
        {
            ++_recoveryRevision;
            _recoveryWait?.Cancel();
            _recoveryWait = null;
        }
    }

    private void ClearRecovery()
    {
        CancelRecoveryWait();
        lock (_pendingUpdateGate)
        {
            _pausedChangeHint = FileChangeHint.None;
            _pendingIndexNotification = null;
            _notificationNeedsRevalidation = false;
            _recoveryFailure = null;
            _recoveryFailureCount = 0;
            _recoveryRetryable = false;
        }
    }

    private TimeSpan GetRemainingRecoveryDelay()
    {
        var remaining = _recoveryDelay - _timeProvider.GetElapsedTime(_recoveryScheduledAt);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private Task PublishRecoveryStatusAsync()
        => _owner.InvokeOnSessionContextAsync(() =>
        {
            if (_owner.IsShutdownOrDisposed || !_owner.IsAutomaticReloadPaused)
                return;
            lock (_pendingUpdateGate)
            {
                var failure = _recoveryFailure;
                if (failure == null)
                    return;
                var next = !_recoveryRetryable ? "Retry tailing manually." :
                    !_owner.HasVisibleClientsForTailing ? "Recovery will resume when this file is visible." :
                    $"Retrying automatically in about {FormatRetryDelay(GetRemainingRecoveryDelay())}.";
                var reason = failure.Reason switch
                {
                    AutomaticReloadReason.Cooldown => "reload delayed by the shared or per-file cooldown",
                    AutomaticReloadReason.MetadataUnavailable => "file metadata is temporarily unavailable",
                    AutomaticReloadReason.MetadataInconsistent => "file metadata is inconsistent",
                    AutomaticReloadReason.ReplacementChanged => "file changed during replacement verification or reload",
                    _ => failure.Message
                };
                _owner.AutomaticReloadStatusText = $"Automatic tailing paused: {reason}. {next}";
                _owner.AutomaticReloadFailureDetail = failure.InnerException is { } inner
                    ? $"{failure.Message} {inner.GetType().Name}: {inner.Message}" : failure.Message;
                NotifyClients(client => client.SetStatusText(_owner.AutomaticReloadStatusText));
            }
        });

    private static int GetChangeHintPriority(FileChangeHint changeHint)
        => changeHint switch
        {
            FileChangeHint.RecreatedAfterMissing => 4,
            FileChangeHint.Truncated => 3,
            FileChangeHint.IdentityChanged => 2,
            FileChangeHint.UnspecifiedReplacement => 1,
            _ => 0
        };

    private static string FormatRetryDelay(TimeSpan retryAfter)
    {
        if (retryAfter >= TimeSpan.FromHours(1))
            return $"{Math.Ceiling(retryAfter.TotalHours):N0} hours";
        if (retryAfter >= TimeSpan.FromMinutes(1))
            return $"{Math.Ceiling(retryAfter.TotalMinutes):N0} minutes";

        return $"{Math.Max(1, Math.Ceiling(retryAfter.TotalSeconds)):N0} seconds";
    }

    private Task NotifyContentAdvancedAsync(int previousTotalLines, int updatedLineCount, CancellationToken ct)
        => NotifyClientsOnSessionContextAsync(client => client.HandleSessionContentAdvancedAsync(previousTotalLines, updatedLineCount, ct));

    private bool HasPendingIndexNotification
    {
        get { lock (_pendingUpdateGate) return _pendingIndexNotification != null; }
    }

    private void RecordCommittedIndexUpdate(int? previousTotalLines, LineIndexUpdateResult? updateResult)
    {
        if (updateResult is not { } committed)
            return;
        lock (_pendingUpdateGate)
        {
            if (_pendingIndexNotification is { } pending)
            {
                committed = new(pending.UpdateResult.PreviousLineCount, committed.UpdatedLineCount,
                    pending.UpdateResult.IsGenerationReset || committed.IsGenerationReset);
                previousTotalLines = pending.PreviousTotalLines;
            }
            _pendingIndexNotification = new(previousTotalLines, committed);
        }
    }

    private async Task NotifyCommittedIndexUpdateAsync(int? previousTotalLines, LineIndexUpdateResult? updateResult)
    {
        // The hint was consumed by the successful update, even if publication later fails.
        lock (_pendingUpdateGate)
            _pausedChangeHint = FileChangeHint.None;
        RecordCommittedIndexUpdate(previousTotalLines, updateResult);
        PendingIndexNotification? pending;
        lock (_pendingUpdateGate)
            pending = _pendingIndexNotification;
        await NotifyIndexUpdateAsync(pending?.PreviousTotalLines, pending?.UpdateResult).ConfigureAwait(false);
        lock (_pendingUpdateGate)
            _pendingIndexNotification = null;
    }

    private async Task NotifyIndexUpdateAsync(int? previousTotalLines, LineIndexUpdateResult? updateResult, CancellationToken ct = default)
    {
        if (updateResult == null || _owner.IsShutdownOrDisposed)
            return;

        if (updateResult.Value.IsGenerationReset)
        {
            await NotifyReloadedAsync(ct).ConfigureAwait(false);
            return;
        }

        if (TryGetContentAdvance(previousTotalLines, updateResult.Value.UpdatedLineCount, out var previousTotal, out var updatedTotal))
            await NotifyContentAdvancedAsync(previousTotal, updatedTotal, ct).ConfigureAwait(false);
    }

    private async void OnFileAvailabilityChanged(object? sender, FileAvailabilityChangedEventArgs e)
    {
        if (_owner.IsShutdownOrDisposed ||
            !string.Equals(e.FilePath, _owner.FilePath, StringComparison.OrdinalIgnoreCase) ||
            !TryAcceptAvailabilitySequence(e.Sequence))
        {
            return;
        }

        await _tailUpdateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_owner.IsShutdownOrDisposed || e.Sequence != Volatile.Read(ref _latestAvailabilitySequence))
                return;

            await _owner.PublishFileMissingAsync(!e.IsAvailable).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            _tailUpdateGate.Release();
        }
    }

    private bool TryAcceptAvailabilitySequence(long sequence)
    {
        while (true)
        {
            var current = Volatile.Read(ref _latestAvailabilitySequence);
            if (sequence <= current)
                return false;

            if (Interlocked.CompareExchange(ref _latestAvailabilitySequence, sequence, current) == current)
                return true;
        }
    }

    private static bool TryGetContentAdvance(int? previousTotalLines, int? updatedLineCount, out int previousTotal, out int updatedTotal)
    {
        previousTotal = previousTotalLines ?? 0;
        updatedTotal = updatedLineCount ?? 0;
        return previousTotalLines != null && updatedLineCount != null && updatedTotal > previousTotal;
    }

    private Task NotifyReloadedAsync(CancellationToken ct)
        => NotifyClientsOnSessionContextAsync(client => client.HandleSessionReloadedAsync(ct));

    private void NotifyClients(Action<IFileSessionClient> action)
    {
        foreach (var client in _owner.GetClientSnapshots())
        {
            if (client.IsSessionClientDisposed)
                continue;

            action(client);
        }
    }

    private async Task NotifyClientsAsync(Func<IFileSessionClient, Task> action)
    {
        foreach (var client in _owner.GetClientSnapshots())
        {
            if (client.IsSessionClientDisposed)
                continue;

            await action(client).ConfigureAwait(false);
        }
    }

    private Task NotifyClientsOnSessionContextAsync(Action<IFileSessionClient> action)
        => _owner.InvokeOnSessionContextAsync(() => NotifyClients(action));

    private Task NotifyClientsOnSessionContextAsync(Func<IFileSessionClient, Task> action)
        => _owner.InvokeOnSessionContextAsync(() => NotifyClientsAsync(action));

    private Task PublishSuspendedStateAsync(bool isSuspended)
        => _owner.InvokeOnSessionContextAsync(() =>
            _owner.IsSuspended = isSuspended || !IsTailRequestActive || !_owner.HasVisibleClientsForTailing);

    private Task PublishAutomaticReloadPausedStateAsync(bool isPaused)
        => _owner.InvokeOnSessionContextAsync(() =>
        {
            _owner.IsAutomaticReloadPaused = isPaused;
            if (!isPaused)
            {
                _owner.AutomaticReloadStatusText = null;
                _owner.AutomaticReloadFailureDetail = null;
            }
        });

    private readonly record struct PendingIndexNotification(
        int? PreviousTotalLines,
        LineIndexUpdateResult UpdateResult);

    private bool IsTailRequestActive => Volatile.Read(ref _tailRequestActive) != 0;

    private async Task StartTailRequestAsync(int pollingIntervalMs)
    {
        var baseline = await _owner.ReadTailBaselineAsync(_recoveryLifetime.Token).ConfigureAwait(false);
        if (_owner.IsShutdownOrDisposed || !_owner.HasVisibleClientsForTailing)
            return;
        if (Interlocked.CompareExchange(ref _tailRequestActive, 1, 0) != 0)
            return;

        try
        {
            if (_tailService is IFileTailBaselineService service && baseline is { } committed)
                service.StartTailing(_owner.FilePath, _owner.EffectiveEncoding, committed, pollingIntervalMs);
            else
                _tailService.StartTailing(_owner.FilePath, _owner.EffectiveEncoding, pollingIntervalMs);
            if (_owner.IsShutdownOrDisposed || !_owner.HasVisibleClientsForTailing)
                StopTailRequest();
        }
        catch
        {
            MarkTailRequestInactive();
            throw;
        }
    }

    private void StopTailRequest()
    {
        if (Interlocked.Exchange(ref _tailRequestActive, 0) == 0)
            return;

        _tailService.StopTailing(_owner.FilePath);
    }

    private void MarkTailRequestInactive()
        => Volatile.Write(ref _tailRequestActive, 0);

    private async Task<int> ReadPublishedTotalLinesAsync()
    {
        var totalLines = 0;
        await _owner.InvokeOnSessionContextAsync(() => totalLines = _owner.TotalLines).ConfigureAwait(false);
        return totalLines;
    }
}
