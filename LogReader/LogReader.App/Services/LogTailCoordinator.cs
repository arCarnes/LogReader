namespace LogReader.App.Services;

using LogReader.Core.Interfaces;
using LogReader.Core.Models;

internal sealed class LogTailCoordinator : IDisposable
{
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

    public LogTailCoordinator(FileSession owner, IFileTailService tailService)
    {
        _owner = owner;
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

            lock (_pendingUpdateGate)
                _pausedChangeHint = FileChangeHint.None;
            await PublishAutomaticReloadPausedStateAsync(false).ConfigureAwait(false);
            StartTailRequest(_tailPollingIntervalMs);
            await PublishSuspendedStateAsync(false).ConfigureAwait(false);

            var previousTotalLines = await ReadPublishedTotalLinesAsync().ConfigureAwait(false);
            var updateResult = await _owner.UpdateLineIndexAsync(CancellationToken.None).ConfigureAwait(false);
            if (_owner.IsShutdownOrDisposed)
            {
                SuspendTailing();
                return;
            }

            await NotifyIndexUpdateAsync(previousTotalLines, updateResult).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (AutomaticReloadBlockedException ex)
        {
            await PauseForAutomaticReloadAsync(ex, FileChangeHint.None).ConfigureAwait(false);
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
        if (_owner.IsSuspended && !IsTailRequestActive)
            return;

        StopTailRequest();
        _ = PublishSuspendedStateAsync(true);
    }

    public void ResumeTailing()
    {
        if (_owner.IsShutdownOrDisposed || _owner.IsAutomaticReloadPaused)
            return;

        _ = ResumeTailingWithCatchUpAsync(_tailPollingIntervalMs);
    }

    public void ApplyVisibleTailingMode(int pollingIntervalMs)
    {
        if (_owner.IsShutdownOrDisposed || _owner.IsAutomaticReloadPaused)
            return;

        _ = ResumeTailingWithCatchUpAsync(pollingIntervalMs);
    }

    public async Task ResumeTailingWithCatchUpAsync(int pollingIntervalMs)
    {
        if (_owner.IsShutdownOrDisposed || _owner.IsAutomaticReloadPaused)
        {
            if (_owner.IsShutdownOrDisposed)
                SuspendTailing();
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
        if (!wasSuspended && _tailPollingIntervalMs == pollingIntervalMs)
            return;

        string? catchUpErrorMessage = null;
        var startedDuringResume = false;
        int? previousTotalLines = null;
        LineIndexUpdateResult? updateResult = null;
        try
        {
            if (wasSuspended)
            {
                StartTailRequest(pollingIntervalMs);
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
                await NotifyIndexUpdateAsync(previousTotalLines, updateResult).ConfigureAwait(false);

                StartTailRequest(pollingIntervalMs);
                _tailPollingIntervalMs = pollingIntervalMs;
                await PublishSuspendedStateAsync(false).ConfigureAwait(false);
            }

            if (startedDuringResume)
                await NotifyIndexUpdateAsync(previousTotalLines, updateResult).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(catchUpErrorMessage))
                await NotifyClientsOnSessionContextAsync(client => client.SetStatusText($"Tail resumed (catch-up skipped): {catchUpErrorMessage}")).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            await PublishSuspendedStateAsync(true).ConfigureAwait(false);
            await NotifyClientsOnSessionContextAsync(client => client.SetStatusText($"Tail error: {ex.Message}")).ConfigureAwait(false);
        }
    }

    public void BeginShutdown()
    {
        ClearPendingTailUpdates();
        StopTailRequest();
        _ = PublishSuspendedStateAsync(true);
    }

    public void Dispose()
    {
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
            if (_owner.IsShutdownOrDisposed || _owner.IsAutomaticReloadPaused)
                return;

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
                    if (_owner.IsShutdownOrDisposed || _owner.IsAutomaticReloadPaused)
                        return;

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

                    await NotifyIndexUpdateAsync(previousTotalLines, updateResult).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
                catch (AutomaticReloadBlockedException ex)
                {
                    await PauseForAutomaticReloadAsync(ex, changeHint).ConfigureAwait(false);
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
                _appendPending = false;
                _pendingChangeHint = FileChangeHint.None;
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
        }
    }

    private async void OnTailError(object? sender, TailErrorEventArgs e)
    {
        if (_owner.IsShutdownOrDisposed || !string.Equals(e.FilePath, _owner.FilePath, StringComparison.OrdinalIgnoreCase))
            return;

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

    public async Task RetryAutomaticTailingAsync()
    {
        if (_owner.IsShutdownOrDisposed || !_owner.IsAutomaticReloadPaused)
            return;

        await _tailUpdateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_owner.IsShutdownOrDisposed || !_owner.IsAutomaticReloadPaused)
                return;

            FileChangeHint changeHint;
            lock (_pendingUpdateGate)
                changeHint = _pausedChangeHint;

            await _owner.ResetAutomaticReloadDelayAsync().ConfigureAwait(false);
            var previousTotalLines = await ReadPublishedTotalLinesAsync().ConfigureAwait(false);
            var updateResult = await _owner.UpdateLineIndexAsync(
                CancellationToken.None,
                changeHint).ConfigureAwait(false);
            if (_owner.IsShutdownOrDisposed)
                return;

            lock (_pendingUpdateGate)
                _pausedChangeHint = FileChangeHint.None;
            await PublishAutomaticReloadPausedStateAsync(false).ConfigureAwait(false);
            await NotifyIndexUpdateAsync(previousTotalLines, updateResult).ConfigureAwait(false);

            if (_owner.HasVisibleClientsForTailing)
            {
                StartTailRequest(_tailPollingIntervalMs);
                await PublishSuspendedStateAsync(false).ConfigureAwait(false);
            }

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
            await NotifyClientsOnSessionContextAsync(
                client => client.SetStatusText($"Retry failed: {ex.Message}")).ConfigureAwait(false);
        }
        finally
        {
            _tailUpdateGate.Release();
        }
    }

    private async Task PauseForAutomaticReloadAsync(
        AutomaticReloadBlockedException exception,
        FileChangeHint changeHint)
    {
        lock (_pendingUpdateGate)
        {
            if (GetChangeHintPriority(changeHint) >
                GetChangeHintPriority(_pausedChangeHint))
            {
                _pausedChangeHint = changeHint;
            }

            _appendPending = false;
            _pendingChangeHint = FileChangeHint.None;
        }

        StopTailRequest();
        await _owner.InvokeOnSessionContextAsync(() =>
        {
            if (_owner.IsShutdownOrDisposed)
                return;

            _owner.IsAutomaticReloadPaused = true;
            _owner.IsSuspended = true;
            var status = exception.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero
                ? $"Automatic tailing paused to prevent repeated full-file reads. Retry in about {FormatRetryDelay(retryAfter)}."
                : "Automatic tailing paused because file metadata is unstable. Retry when the file is stable.";
            NotifyClients(client => client.SetStatusText(status));
        }).ConfigureAwait(false);
    }

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

    private async Task NotifyIndexUpdateAsync(int? previousTotalLines, LineIndexUpdateResult? updateResult)
    {
        if (updateResult == null || _owner.IsShutdownOrDisposed)
            return;

        if (updateResult.Value.IsGenerationReset)
        {
            await NotifyReloadedAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (TryGetContentAdvance(previousTotalLines, updateResult.Value.UpdatedLineCount, out var previousTotal, out var updatedTotal))
            await NotifyContentAdvancedAsync(previousTotal, updatedTotal, CancellationToken.None).ConfigureAwait(false);
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
        => _owner.InvokeOnSessionContextAsync(() => _owner.IsSuspended = isSuspended);

    private Task PublishAutomaticReloadPausedStateAsync(bool isPaused)
        => _owner.InvokeOnSessionContextAsync(() => _owner.IsAutomaticReloadPaused = isPaused);

    private bool IsTailRequestActive => Volatile.Read(ref _tailRequestActive) != 0;

    private void StartTailRequest(int pollingIntervalMs)
    {
        if (Interlocked.CompareExchange(ref _tailRequestActive, 1, 0) != 0)
            return;

        try
        {
            _tailService.StartTailing(_owner.FilePath, _owner.EffectiveEncoding, pollingIntervalMs);
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
