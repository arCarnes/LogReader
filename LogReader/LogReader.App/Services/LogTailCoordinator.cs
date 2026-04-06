namespace LogReader.App.Services;

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
        _ = PublishSuspendedStateAsync(false);
    }

    public void SuspendTailing()
    {
        if (_owner.IsSuspended)
            return;

        _tailService.StopTailing(_owner.FilePath);
        _ = PublishSuspendedStateAsync(true);
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
                startedDuringResume = true;
                await PublishSuspendedStateAsync(false).ConfigureAwait(false);

                previousTotalLines = await ReadPublishedTotalLinesAsync().ConfigureAwait(false);
                updatedLineCount = await _owner.UpdateLineIndexLineCountAsync(CancellationToken.None).ConfigureAwait(false);
                if (updatedLineCount != null && _owner.IsShutdownOrDisposed)
                {
                    SuspendTailing();
                    return;
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
                await PublishSuspendedStateAsync(false).ConfigureAwait(false);
            }

            if (previousTotalLines != null && updatedLineCount != null)
                await NotifyContentAdvancedAsync(previousTotalLines.Value, updatedLineCount.Value, CancellationToken.None).ConfigureAwait(false);

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
        _tailService.StopTailing(_owner.FilePath);
        _ = PublishSuspendedStateAsync(true);
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
            var previousTotalLines = await ReadPublishedTotalLinesAsync().ConfigureAwait(false);
            var updatedLineCount = await _owner.UpdateLineIndexLineCountAsync(CancellationToken.None).ConfigureAwait(false);
            if (updatedLineCount == null || _owner.IsShutdownOrDisposed)
                return;

            await NotifyContentAdvancedAsync(previousTotalLines, updatedLineCount.Value, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            await NotifyClientsOnSessionContextAsync(client => client.SetStatusText($"Tail error: {ex.Message}")).ConfigureAwait(false);
        }
    }

    private async void OnFileRotated(object? sender, FileRotatedEventArgs e)
    {
        if (_owner.IsShutdownOrDisposed || !string.Equals(e.FilePath, _owner.FilePath, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            await NotifyClientsOnSessionContextAsync(client => client.SetStatusText("File rotated, reloading...")).ConfigureAwait(false);
            await _owner.ResetLineIndexAsync().ConfigureAwait(false);
            await _owner.LoadAsync().ConfigureAwait(false);
            if (_owner.HasLoadError)
            {
                if (!string.IsNullOrWhiteSpace(_owner.LastErrorMessage))
                    await NotifyClientsOnSessionContextAsync(client => client.SetStatusText($"Error: {_owner.LastErrorMessage}")).ConfigureAwait(false);
                return;
            }

            await NotifyReloadedAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            await NotifyClientsOnSessionContextAsync(client => client.SetStatusText($"Tail error: {ex.Message}")).ConfigureAwait(false);
        }
    }

    private async void OnTailError(object? sender, TailErrorEventArgs e)
    {
        if (_owner.IsShutdownOrDisposed || !string.Equals(e.FilePath, _owner.FilePath, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            await _owner.InvokeOnSessionContextAsync(() =>
            {
                if (_owner.IsShutdownOrDisposed)
                    return;

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

    private Task NotifyContentAdvancedAsync(int previousTotalLines, int updatedLineCount, CancellationToken ct)
        => NotifyClientsOnSessionContextAsync(client => client.HandleSessionContentAdvancedAsync(previousTotalLines, updatedLineCount, ct));

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

    private async Task<int> ReadPublishedTotalLinesAsync()
    {
        var totalLines = 0;
        await _owner.InvokeOnSessionContextAsync(() => totalLines = _owner.TotalLines).ConfigureAwait(false);
        return totalLines;
    }
}
