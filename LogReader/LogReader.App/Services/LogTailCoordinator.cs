namespace LogReader.App.Services;

using System.Windows;
using System.Windows.Threading;
using LogReader.Core.Interfaces;

internal sealed class LogTailCoordinator : IDisposable
{
    private readonly FileSession _owner;
    private readonly IFileTailService _tailService;

    private int _tailPollingIntervalMs = 250;

    public LogTailCoordinator(FileSession owner, IFileTailService tailService)
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
        if (!wasSuspended && _tailPollingIntervalMs == pollingIntervalMs)
            return;

        string? catchUpErrorMessage = null;
        var startedDuringResume = false;
        int? previousTotalLines = null;
        int? updatedLineCount = null;
        try
        {
            if (wasSuspended)
            {
                _tailService.StartTailing(_owner.FilePath, _owner.EffectiveEncoding, pollingIntervalMs);
                _tailPollingIntervalMs = pollingIntervalMs;
                _owner.IsSuspended = false;
                startedDuringResume = true;

                updatedLineCount = await _owner.UpdateLineIndexLineCountAsync(CancellationToken.None);
                if (updatedLineCount != null)
                {
                    if (_owner.IsShutdownOrDisposed)
                    {
                        SuspendTailing();
                        return;
                    }

                    previousTotalLines = _owner.TotalLines;
                    _owner.TotalLines = updatedLineCount.Value;
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

            if (previousTotalLines != null && updatedLineCount != null)
                await NotifyContentAdvancedAsync(previousTotalLines.Value, updatedLineCount.Value, CancellationToken.None);

            if (!string.IsNullOrWhiteSpace(catchUpErrorMessage))
                NotifyClients(client => client.SetStatusText($"Tail resumed (catch-up skipped): {catchUpErrorMessage}"));
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _owner.IsSuspended = true;
            NotifyClients(client => client.SetStatusText($"Tail error: {ex.Message}"));
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

            var previousTotalLines = _owner.TotalLines;
            _owner.TotalLines = updatedLineCount.Value;
            await NotifyContentAdvancedAsync(previousTotalLines, updatedLineCount.Value, CancellationToken.None);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            NotifyClients(client => client.SetStatusText($"Tail error: {ex.Message}"));
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

                NotifyClients(client => client.SetStatusText("File rotated, reloading..."));
                await _owner.ResetLineIndexAsync();
                await _owner.LoadAsync();
                if (_owner.HasLoadError)
                {
                    if (!string.IsNullOrWhiteSpace(_owner.LastErrorMessage))
                        NotifyClients(client => client.SetStatusText($"Error: {_owner.LastErrorMessage}"));
                    return;
                }

                await NotifyReloadedAsync(CancellationToken.None);
            });
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            NotifyClients(client => client.SetStatusText($"Tail error: {ex.Message}"));
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
                NotifyClients(client => client.SetStatusText($"Tailing stopped: {e.ErrorMessage}"));
            });
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            NotifyClients(client => client.SetStatusText($"Tail error: {ex.Message}"));
        }
    }

    private async Task NotifyContentAdvancedAsync(int previousTotalLines, int updatedLineCount, CancellationToken ct)
    {
        await InvokeOnOwnerUiAsync(() => NotifyClientsAsync(client => client.HandleSessionContentAdvancedAsync(previousTotalLines, updatedLineCount, ct)));
    }

    private async Task NotifyReloadedAsync(CancellationToken ct)
    {
        await InvokeOnOwnerUiAsync(() => NotifyClientsAsync(client => client.HandleSessionReloadedAsync(ct)));
    }

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

            await action(client);
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
}
