namespace LogReader.App.Services;

using CommunityToolkit.Mvvm.ComponentModel;
using LogReader.App.Helpers;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

internal interface IFileSessionClient
{
    bool IsSessionClientDisposed { get; }

    bool IsSessionClientVisible { get; }

    Task HandleSessionContentAdvancedAsync(int previousTotalLines, int updatedLineCount, CancellationToken ct);

    Task HandleSessionReloadedAsync(CancellationToken ct);

    void SetStatusText(string statusText);
}

internal sealed partial class FileSession : ObservableObject, IDisposable
{
    private readonly ILogReaderService _logReader;
    private readonly IEncodingDetectionService _encodingDetectionService;
    private readonly SemaphoreSlim _lineIndexLock = new(1, 1);
    private readonly LogTailCoordinator _tailCoordinator;
    private readonly object _clientGate = new();
    private readonly List<IFileSessionClient> _clients = new();

    private LineIndex? _lineIndex;
    private CancellationTokenSource? _loadCts;
    private Task? _lineIndexDisposeTask;
    private int _isDisposed;
    private int _shutdownStarted;

    [ObservableProperty]
    private FileEncoding _effectiveEncoding = FileEncoding.Utf8;

    [ObservableProperty]
    private string _encodingStatusText = "Auto -> UTF-8 (fallback)";

    [ObservableProperty]
    private int _totalLines;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasLoadError;

    [ObservableProperty]
    private bool _isSuspended = true;

    [ObservableProperty]
    private int _searchContentVersion;

    [ObservableProperty]
    private string? _lastErrorMessage;

    internal FileSession(
        FileSessionKey key,
        ILogReaderService logReader,
        IFileTailService tailService,
        IEncodingDetectionService encodingDetectionService)
    {
        Key = key;
        _logReader = logReader;
        _encodingDetectionService = encodingDetectionService;
        _tailCoordinator = new LogTailCoordinator(this, tailService);
    }

    public FileSessionKey Key { get; }

    public string FilePath => Key.FilePath;

    public FileEncoding RequestedEncoding => Key.RequestedEncoding;

    internal bool IsShuttingDown => Volatile.Read(ref _shutdownStarted) != 0;

    internal bool IsShutdownOrDisposed => IsShuttingDown || Volatile.Read(ref _isDisposed) != 0;

    internal bool HasNoLineIndex => Volatile.Read(ref _lineIndex) == null;

    internal SemaphoreSlim DebugLineIndexLock => _lineIndexLock;

    internal Task? DebugLineIndexDisposeTask
    {
        get => Volatile.Read(ref _lineIndexDisposeTask);
        set => Volatile.Write(ref _lineIndexDisposeTask, value);
    }

    internal LineIndex? DebugLineIndex => Volatile.Read(ref _lineIndex);

    internal int DebugIsDisposed => Volatile.Read(ref _isDisposed);

    public void EnsureInitialEncodingResolved()
    {
        if (IsShutdownOrDisposed)
            return;

        ApplyEncodingDecision(ResolveEncodingDecision());
    }

    public void AttachClient(IFileSessionClient client)
    {
        lock (_clientGate)
        {
            foreach (var existing in _clients)
            {
                if (ReferenceEquals(existing, client))
                    return;
            }

            _clients.Add(client);
        }
    }

    public void DetachClient(IFileSessionClient client)
    {
        var remainingClientCount = 0;
        var hasVisibleClients = false;
        lock (_clientGate)
        {
            for (var i = _clients.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_clients[i], client))
                    _clients.RemoveAt(i);
            }

            remainingClientCount = _clients.Count;
            hasVisibleClients = HasVisibleClientsUnsafe();
        }

        if (remainingClientCount == 0)
        {
            if (!IsShutdownOrDisposed)
                PauseWithoutClients();
            return;
        }

        if (!hasVisibleClients && !IsShutdownOrDisposed)
            _tailCoordinator.SuspendTailing();
    }

    public Task ResumeTailingWithCatchUpAsync(int pollingIntervalMs)
        => _tailCoordinator.ResumeTailingWithCatchUpAsync(pollingIntervalMs);

    public void ResumeTailing()
        => _tailCoordinator.ResumeTailing();

    public void SuspendTailing()
        => _tailCoordinator.SuspendTailing();

    public void SuspendTailingIfNoVisibleClients()
    {
        if (HasVisibleClients())
            return;

        _tailCoordinator.SuspendTailing();
    }

    public void ApplyVisibleTailingMode(int pollingIntervalMs)
        => _tailCoordinator.ApplyVisibleTailingMode(pollingIntervalMs);

    private bool HasVisibleClients()
    {
        lock (_clientGate)
            return HasVisibleClientsUnsafe();
    }

    private bool HasVisibleClientsUnsafe()
    {
        foreach (var client in _clients)
        {
            if (client.IsSessionClientDisposed || !client.IsSessionClientVisible)
                continue;

            return true;
        }

        return false;
    }

    public async Task LoadAsync()
    {
        if (IsShutdownOrDisposed)
            return;

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        var cts = new CancellationTokenSource();
        _loadCts = cts;

        IsLoading = true;
        HasLoadError = false;
        LastErrorMessage = null;

        try
        {
            ApplyEncodingDecision(await ResolveEffectiveEncodingAsync(cts.Token));

            LineIndex? oldIndex;
            await _lineIndexLock.WaitAsync(cts.Token);
            try
            {
                oldIndex = _lineIndex;
                _lineIndex = null;
            }
            finally
            {
                _lineIndexLock.Release();
            }

            oldIndex?.Dispose();

            var newIndex = await BuildIndexOffUiAsync(EffectiveEncoding, cts.Token);
            await _lineIndexLock.WaitAsync(cts.Token);
            try
            {
                _lineIndex = newIndex;
                TotalLines = newIndex.LineCount;
            }
            finally
            {
                _lineIndexLock.Release();
            }

            if (IsShutdownOrDisposed)
            {
                IsSuspended = true;
                return;
            }

            _tailCoordinator.StartLoadedTailing();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            LastErrorMessage = ex.Message;
            HasLoadError = true;
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                _loadCts = null;
                IsLoading = false;
            }
        }
    }

    internal Task<IReadOnlyList<string>> ReadLinesOffUiAsync(
        LineIndex lineIndex,
        int startLine,
        int count,
        FileEncoding encoding,
        CancellationToken ct)
        => Task.Run(async () =>
            await _logReader.ReadLinesAsync(FilePath, lineIndex, startLine, count, encoding, ct).ConfigureAwait(false), ct);

    internal Task<string> ReadLineOffUiAsync(
        LineIndex lineIndex,
        int lineNumber,
        FileEncoding encoding,
        CancellationToken ct)
        => Task.Run(async () =>
            await _logReader.ReadLineAsync(FilePath, lineIndex, lineNumber, encoding, ct).ConfigureAwait(false), ct);

    internal async Task<LineIndex?> GetLineIndexSnapshotAsync(CancellationToken ct = default)
    {
        await _lineIndexLock.WaitAsync(ct);
        try
        {
            return _lineIndex;
        }
        finally
        {
            _lineIndexLock.Release();
        }
    }

    internal async Task<int?> UpdateLineIndexLineCountAsync(CancellationToken ct)
    {
        await _lineIndexLock.WaitAsync(ct);
        try
        {
            if (_lineIndex == null)
                return null;

            _lineIndex = await UpdateIndexOffUiAsync(_lineIndex, EffectiveEncoding, ct);
            return _lineIndex.LineCount;
        }
        finally
        {
            _lineIndexLock.Release();
        }
    }

    internal async Task ResetLineIndexAsync()
    {
        await _lineIndexLock.WaitAsync();
        try
        {
            _lineIndex?.Dispose();
            _lineIndex = null;
            SearchContentVersion++;
        }
        finally
        {
            _lineIndexLock.Release();
        }
    }

    internal void SetTotalLinesForTesting(int value)
        => TotalLines = value;

    internal void BeginShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
            return;

        _loadCts?.Cancel();
        _tailCoordinator.BeginShutdown();
        IsLoading = false;
    }

    internal IReadOnlyList<IFileSessionClient> GetClientSnapshots()
    {
        lock (_clientGate)
            return _clients.ToArray();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            return;

        BeginShutdown();
        _tailCoordinator.Dispose();
        _loadCts?.Dispose();
        _ = EnsureLineIndexDisposedTask();
    }

    private EncodingHelper.EncodingDecision ResolveEncodingDecision()
        => RequestedEncoding == FileEncoding.Auto
            ? _encodingDetectionService.ResolveEncodingDecision(FilePath, RequestedEncoding)
            : EncodingHelper.ResolveManualEncodingDecision(RequestedEncoding);

    private async Task<EncodingHelper.EncodingDecision> ResolveEffectiveEncodingAsync(CancellationToken ct)
    {
        return RequestedEncoding == FileEncoding.Auto
            ? await Task.Run(() => _encodingDetectionService.ResolveEncodingDecision(FilePath, RequestedEncoding)).WaitAsync(ct)
            : EncodingHelper.ResolveManualEncodingDecision(RequestedEncoding);
    }

    private void ApplyEncodingDecision(EncodingHelper.EncodingDecision decision)
    {
        EffectiveEncoding = decision.ResolvedEncoding;
        EncodingStatusText = decision.StatusText;
    }

    private Task<LineIndex> BuildIndexOffUiAsync(FileEncoding encoding, CancellationToken ct)
        => Task.Run(async () =>
            await _logReader.BuildIndexAsync(FilePath, encoding, ct).ConfigureAwait(false), ct);

    private Task<LineIndex> UpdateIndexOffUiAsync(LineIndex lineIndex, FileEncoding encoding, CancellationToken ct)
        => Task.Run(async () =>
            await _logReader.UpdateIndexAsync(FilePath, lineIndex, encoding, ct).ConfigureAwait(false), ct);

    private void PauseWithoutClients()
    {
        _loadCts?.Cancel();
        _tailCoordinator.SuspendTailing();
        IsLoading = false;
    }

    private Task EnsureLineIndexDisposedTask()
    {
        var existingTask = Volatile.Read(ref _lineIndexDisposeTask);
        if (existingTask != null)
            return existingTask;

        var createdTask = DisposeLineIndexAsync();
        var publishedTask = Interlocked.CompareExchange(ref _lineIndexDisposeTask, createdTask, null) ?? createdTask;
        if (ReferenceEquals(publishedTask, createdTask))
            ObserveBackgroundTask(createdTask);

        return publishedTask;
    }

    private void DisposeLineIndexUnsafe()
    {
        _lineIndex?.Dispose();
        _lineIndex = null;
    }

    private async Task DisposeLineIndexAsync()
    {
        var lockTaken = false;
        try
        {
            await _lineIndexLock.WaitAsync().ConfigureAwait(false);
            lockTaken = true;
            DisposeLineIndexUnsafe();
        }
        catch (ObjectDisposedException) { }
        finally
        {
            if (lockTaken)
                _lineIndexLock.Release();
        }
    }

    private static void ObserveBackgroundTask(Task task)
    {
        if (task.IsCompleted)
            return;

        _ = task.ContinueWith(
            static completedTask => _ = completedTask.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
