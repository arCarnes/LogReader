namespace LogReader.App.Services;

using System.Windows;
using System.Windows.Threading;
using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;

internal sealed class LogTailCoordinator : IDisposable
{
    private readonly LogTabViewModel _owner;
    private readonly IFileTailService _tailService;

    private int _tailPollingIntervalMs = 250;

    public LogTailCoordinator(LogTabViewModel owner, IFileTailService tailService)
    {
        _owner = owner;
        _tailService = tailService;
        _tailService.LinesAppended += OnLinesAppended;
        _tailService.FileRotated += OnFileRotated;
        _tailService.TailError += OnTailError;
    }

    public void StartLoadedTailing()
    {
        _tailService.StartTailing(_owner.FilePath, _owner.EffectiveEncoding, _tailPollingIntervalMs);
        _owner.IsSuspended = false;
    }

    public void StopForReload()
    {
        _tailService.StopTailing(_owner.FilePath);
    }

    public void SuspendTailing()
    {
        if (_owner.IsSuspended)
            return;

        _tailService.StopTailing(_owner.FilePath);
        _owner.IsSuspended = true;
    }

    public void ResumeTailing()
    {
        if (_owner.IsShutdownOrDisposed)
            return;

        _ = ResumeTailingWithCatchUpAsync(_tailPollingIntervalMs);
    }

    public void ApplyVisibleTailingMode(int pollingIntervalMs)
    {
        if (_owner.IsShutdownOrDisposed)
            return;

        _ = ResumeTailingWithCatchUpAsync(pollingIntervalMs);
    }

    public async Task ResumeTailingWithCatchUpAsync(int pollingIntervalMs)
    {
        if (_owner.IsShutdownOrDisposed)
        {
            SuspendTailing();
            return;
        }

        if (_owner.HasNoLineIndex || _owner.IsLoading)
            return;

        pollingIntervalMs = Math.Max(100, pollingIntervalMs);
        var wasSuspended = _owner.IsSuspended;
        var navigateTargetBeforeResume = _owner.NavigateToLineNumber;
        if (!wasSuspended && _tailPollingIntervalMs == pollingIntervalMs)
            return;

        string? catchUpErrorMessage = null;
        var startedDuringResume = false;
        try
        {
            if (wasSuspended)
            {
                _tailService.StartTailing(_owner.FilePath, _owner.EffectiveEncoding, pollingIntervalMs);
                _tailPollingIntervalMs = pollingIntervalMs;
                _owner.IsSuspended = false;
                startedDuringResume = true;

                var updatedLineCount = await _owner.UpdateLineIndexLineCountAsync(CancellationToken.None);
                if (updatedLineCount != null)
                {
                    if (_owner.IsShutdownOrDisposed)
                    {
                        SuspendTailing();
                        return;
                    }

                    var previousTotalLines = _owner.TotalLines;
                    _owner.TotalLines = updatedLineCount.Value;
                    if (_owner.IsFilterActive)
                        await _owner.ApplyTailFilterForAppendedLinesAsync(updatedLineCount.Value, CancellationToken.None);

                    _owner.StatusText = _owner.IsFilterActive
                        ? _owner.ActiveFilterStatusText ?? $"Filter active: {_owner.FilteredLineCount:N0} matching lines."
                        : $"{_owner.TotalLines:N0} lines";

                    if (_owner.AutoScrollEnabled && !_owner.IsFilterActive)
                    {
                        var updatedInPlace = await _owner.TryAppendTailLinesToViewportAsync(previousTotalLines, _owner.TotalLines, CancellationToken.None);
                        if (!updatedInPlace)
                        {
                            var viewportApplied = await _owner.LoadViewportAsync(
                                Math.Max(0, _owner.TotalLines - _owner.ViewportLineCount),
                                _owner.ViewportLineCount);
                            if (!viewportApplied)
                                return;
                        }

                    }
                }
            }
            else
            {
                _tailService.StopTailing(_owner.FilePath);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            catchUpErrorMessage = ex.Message;
        }

        if (_owner.IsShutdownOrDisposed)
        {
            SuspendTailing();
            return;
        }

        try
        {
            if (!startedDuringResume)
            {
                _tailService.StartTailing(_owner.FilePath, _owner.EffectiveEncoding, pollingIntervalMs);
                _tailPollingIntervalMs = pollingIntervalMs;
                _owner.IsSuspended = false;
            }

            if (!string.IsNullOrWhiteSpace(catchUpErrorMessage))
                _owner.StatusText = $"Tail resumed (catch-up skipped): {catchUpErrorMessage}";
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _owner.IsSuspended = true;
            _owner.StatusText = $"Tail error: {ex.Message}";
        }
    }

    public void BeginShutdown()
    {
        _tailService.StopTailing(_owner.FilePath);
        _owner.IsSuspended = true;
    }

    public void Dispose()
    {
        _tailService.LinesAppended -= OnLinesAppended;
        _tailService.FileRotated -= OnFileRotated;
        _tailService.TailError -= OnTailError;
    }

    private async void OnLinesAppended(object? sender, TailEventArgs e)
    {
        if (_owner.IsShutdownOrDisposed || !string.Equals(e.FilePath, _owner.FilePath, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            var updatedLineCount = await _owner.UpdateLineIndexLineCountAsync(CancellationToken.None);
            if (updatedLineCount == null || _owner.IsShutdownOrDisposed)
                return;

            await InvokeOnOwnerUiAsync(async () =>
            {
                if (_owner.IsShutdownOrDisposed)
                    return;

                var previousTotalLines = _owner.TotalLines;
                _owner.TotalLines = updatedLineCount.Value;
                if (_owner.IsFilterActive)
                {
                    await _owner.ApplyTailFilterForAppendedLinesAsync(updatedLineCount.Value, CancellationToken.None);
                    _owner.StatusText = _owner.ActiveFilterStatusText ?? $"Filter active: {_owner.FilteredLineCount:N0} matching lines.";
                    return;
                }

                _owner.StatusText = $"{_owner.TotalLines:N0} lines";
                if (!_owner.AutoScrollEnabled)
                    return;

                var updatedInPlace = await _owner.TryAppendTailLinesToViewportAsync(previousTotalLines, _owner.TotalLines, CancellationToken.None);
                if (!updatedInPlace)
                {
                    var viewportApplied = await _owner.LoadViewportAsync(
                        Math.Max(0, _owner.TotalLines - _owner.ViewportLineCount),
                        _owner.ViewportLineCount);
                    if (!viewportApplied)
                        return;
                }
            });
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            await ApplyStatusOnUiAsync($"Tail error: {ex.Message}");
        }
    }

    private async void OnFileRotated(object? sender, FileRotatedEventArgs e)
    {
        if (_owner.IsShutdownOrDisposed || !string.Equals(e.FilePath, _owner.FilePath, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            await InvokeOnOwnerUiAsync(async () =>
            {
                if (_owner.IsShutdownOrDisposed)
                    return;

                _owner.StatusText = "File rotated, reloading...";
                if (_owner.IsFilterActive)
                    _owner.ResetFilterForRotation();

                await _owner.ResetLineIndexAsync();
                await _owner.LoadAsync();
            });
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            await ApplyStatusOnUiAsync($"Tail error: {ex.Message}");
        }
    }

    private async void OnTailError(object? sender, TailErrorEventArgs e)
    {
        if (_owner.IsShutdownOrDisposed || !string.Equals(e.FilePath, _owner.FilePath, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            await InvokeOnOwnerUiAsync(() =>
            {
                if (_owner.IsShutdownOrDisposed)
                    return;

                _owner.IsSuspended = true;
                _owner.StatusText = $"Tailing stopped: {e.ErrorMessage}";
            });
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            await ApplyStatusOnUiAsync($"Tail error: {ex.Message}");
        }
    }

    private static Task InvokeOnOwnerUiAsync(Func<Task> action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            return action();

        return dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task.Unwrap();
    }

    private static Task InvokeOnOwnerUiAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task;
    }

    private Task ApplyStatusOnUiAsync(string statusText)
        => InvokeOnOwnerUiAsync(() =>
        {
            if (_owner.IsShutdownOrDisposed)
                return;

            _owner.StatusText = statusText;
        });
}
