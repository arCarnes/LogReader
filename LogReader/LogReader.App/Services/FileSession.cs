namespace LogReader.App.Services;

using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using LogReader.App.Helpers;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

internal interface IFileSessionClient
{
    string? DashboardScopeId => null;
    bool IsSessionClientDisposed { get; }

    bool IsSessionClientVisible { get; }

    int? TailPollingIntervalMs => null;

    Task HandleSessionContentAdvancedAsync(int previousTotalLines, int updatedLineCount, CancellationToken ct);

    Task HandleSessionReloadedAsync(CancellationToken ct);

    void SetStatusText(string statusText);
}

internal readonly record struct LineIndexUpdateResult(
    int PreviousLineCount,
    int UpdatedLineCount,
    bool IsGenerationReset);

internal enum FileLoadPhase { Pending, Resolving, Queued, Indexing, Indexed, Failed }

internal sealed partial class FileSession : ObservableObject, IDisposable
{
    private readonly ILogReaderService _logReader;
    private readonly IEncodingDetectionService _encodingDetectionService;
    private readonly ContextUiDispatcher _uiDispatcher;
    private readonly DashboardLoadScheduler? _loadScheduler;
    private readonly object _interactiveLoadGate = new();
    private Task? _interactiveLoadTask;
    private readonly object _operationGate = new();
    private readonly HashSet<Task> _activeLoadOperations = new();
    private bool _interactiveLoading;
    private TaskCompletionSource<FileEncoding>? _encodingReady;
    private readonly AsyncReadWriteGate _lineIndexGate = new();
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
    private long? _fileSizeBytes;

    [ObservableProperty]
    private DateTime? _lastModifiedLocal;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private FileLoadPhase _loadPhase;

    [ObservableProperty]
    private bool _hasLoadError;

    [ObservableProperty]
    private bool _isSuspended = true;

    [ObservableProperty]
    private bool _isAutomaticReloadPaused;

    [ObservableProperty]
    private string? _automaticReloadStatusText;

    [ObservableProperty]
    private string? _automaticReloadFailureDetail;

    [ObservableProperty]
    private bool _isFileMissing;

    [ObservableProperty]
    private int _searchContentVersion;

    [ObservableProperty]
    private string? _lastErrorMessage;

    internal FileSession(
        FileSessionKey key,
        ILogReaderService logReader,
        IFileTailService tailService,
        IEncodingDetectionService encodingDetectionService,
        IUiDispatcher? uiDispatcher = null,
        TimeProvider? timeProvider = null,
        DashboardLoadScheduler? loadScheduler = null)
    {
        Key = key;
        _logReader = logReader;
        _encodingDetectionService = encodingDetectionService;
        _uiDispatcher = new ContextUiDispatcher(uiDispatcher ?? WpfUiDispatcher.Instance);
        _loadScheduler = loadScheduler;
        _tailCoordinator = new LogTailCoordinator(this, tailService, timeProvider ?? TimeProvider.System);
    }

    public FileSessionKey Key { get; }

    public string FilePath => Key.FilePath;

    public FileEncoding RequestedEncoding => Key.RequestedEncoding;

    internal bool SupportsInteractiveLoading => _logReader is IInteractiveLogReaderService && _loadScheduler != null;

    internal InteractiveIndexProgress IndexProgress { get; private set; }

    internal Task ShutdownTask
    {
        get
        {
            lock (_operationGate)
                return Task.WhenAll(DrainLoadOperationsAsync(_activeLoadOperations.ToArray()),
                    _lineIndexDisposeTask ?? Task.CompletedTask);
        }
    }

    private static Task DrainLoadOperationsAsync(Task[] operations)
        => Task.WhenAll(operations.Select(DrainLoadOperationAsync));

    private static async Task DrainLoadOperationAsync(Task operation)
    {
        try { await operation.ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException) { }
    }

    private T TrackLoadOperation<T>(T task) where T : Task
    {
        lock (_operationGate)
            _activeLoadOperations.Add(task);
        _ = task.ContinueWith(completed =>
        {
            lock (_operationGate)
                _activeLoadOperations.Remove(completed);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    internal Task EnsureInteractiveLoadAsync()
    {
        lock (_interactiveLoadGate)
        {
            if (_interactiveLoadTask is { IsCompleted: false } && _loadCts?.IsCancellationRequested != true)
                return _interactiveLoadTask;
            _interactiveLoading = true;
            _interactiveLoadTask = LoadAsync();
            ObserveBackgroundTask(_interactiveLoadTask);
            return _interactiveLoadTask;
        }
    }

    private IReadOnlyList<string> GetDashboardScopes()
    {
        lock (_clientGate)
            return _clients.Where(client => !client.IsSessionClientDisposed && client.DashboardScopeId != null)
                .Select(client => client.DashboardScopeId!).Distinct(StringComparer.Ordinal).ToArray();
    }

    internal Task<RecentLogPreview?> ReadRecentPreviewAsync(CancellationToken ct)
        => TrackLoadOperation(ReadRecentPreviewCoreAsync(ct));

    private async Task<RecentLogPreview?> ReadRecentPreviewCoreAsync(CancellationToken ct)
    {
        if (!SupportsInteractiveLoading)
            return null;
        FileEncoding encoding;
        if (_encodingReady is { } ready && IsLoading)
        {
            await ready.Task.WaitAsync(ct).ConfigureAwait(false);
            encoding = EffectiveEncoding;
        }
        else
            encoding = (await ResolveEffectiveEncodingAsync(ct, preview: true).ConfigureAwait(false)).ResolvedEncoding;
        return await Task.Run(async () =>
        {
            using var lease = await _loadScheduler!.AcquireAsync(FilePath, GetDashboardScopes, preview: true, ct).ConfigureAwait(false);
            return await ((IInteractiveLogReaderService)_logReader).ReadRecentPreviewAsync(FilePath, encoding, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    internal async Task<TResult> RunInteractiveReadAsync<TResult>(Func<Task<TResult>> action, CancellationToken ct)
    {
        if (_loadScheduler == null)
            return await action();
        using var lease = await _loadScheduler.AcquireAsync(FilePath, GetDashboardScopes, preview: false, ct);
        return await action();
    }

    internal Task SetDashboardLoadFailureAsync(string message) => PublishLoadFailureAsync(message);

    internal bool IsShuttingDown => Volatile.Read(ref _shutdownStarted) != 0;

    internal bool IsShutdownOrDisposed => IsShuttingDown || Volatile.Read(ref _isDisposed) != 0;

    internal bool HasNoLineIndex => Volatile.Read(ref _lineIndex) == null;

    internal bool HasClientsForTailing => HasTailingClients();

    internal SemaphoreSlim DebugLineIndexLock => _lineIndexGate.WriteLock;

    internal Task? DebugLineIndexDisposeTask
    {
        get => Volatile.Read(ref _lineIndexDisposeTask);
        set => Volatile.Write(ref _lineIndexDisposeTask, value);
    }

    internal LineIndex? DebugLineIndex => Volatile.Read(ref _lineIndex);

    internal FileGenerationToken CurrentGenerationToken
        => Volatile.Read(ref _lineIndex)?.GenerationToken ?? FileGenerationToken.Unknown;

    internal int DebugIsDisposed => Volatile.Read(ref _isDisposed);

    public void EnsureInitialEncodingResolved()
    {
        if (IsShutdownOrDisposed)
            return;

        TryCaptureSessionContext();
        ApplyEncodingDecision(ResolveEncodingDecision());
    }

    internal void SeedPendingEncodingDisplay(EncodingHelper.EncodingDecision decision)
    {
        if (!HasNoLineIndex || IsLoading)
            return;

        ApplyEncodingDecision(decision);
    }

    public void AttachClient(IFileSessionClient client)
    {
        TryCaptureSessionContext();

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
        var hasTailingClients = false;
        lock (_clientGate)
        {
            for (var i = _clients.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_clients[i], client))
                    _clients.RemoveAt(i);
            }

            remainingClientCount = _clients.Count;
            hasTailingClients = HasTailingClientsUnsafe();
        }

        if (remainingClientCount == 0)
        {
            if (!IsShutdownOrDisposed)
                PauseWithoutClients();
            return;
        }

        if (!hasTailingClients && !IsShutdownOrDisposed)
            _tailCoordinator.SuspendTailing();
        else if (!IsShutdownOrDisposed)
            RefreshTailingPolicy();
    }

    public Task ResumeTailingWithCatchUpAsync(int pollingIntervalMs)
        => _tailCoordinator.ResumeTailingWithCatchUpAsync(pollingIntervalMs);

    public void ResumeTailing()
        => _tailCoordinator.ResumeTailing();

    public Task RetryAutomaticTailingAsync()
        => _tailCoordinator.RetryAutomaticTailingAsync();

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

    internal void RefreshTailingPolicy()
    {
        if (!HasTailingClients())
            _tailCoordinator.SuspendTailing();
        else
            _tailCoordinator.ResumeTailing();
    }

    internal int? GetClientTailPollingIntervalMs()
    {
        lock (_clientGate)
        {
            int? interval = null;
            foreach (var client in _clients)
            {
                if (client.IsSessionClientDisposed || client.TailPollingIntervalMs is not { } requested)
                    continue;
                interval = interval is { } current ? Math.Min(current, requested) : requested;
            }
            return interval;
        }
    }

    private bool HasTailingClients()
    {
        lock (_clientGate)
            return HasTailingClientsUnsafe();
    }

    private bool HasTailingClientsUnsafe()
        => _clients.Any(client => !client.IsSessionClientDisposed &&
            (client.IsSessionClientVisible || client.TailPollingIntervalMs != null));

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

    public Task LoadAsync(bool startLoadedTailing = true)
    {
        var task = TrackLoadOperation(RunLoadAsync(startLoadedTailing));
        ObserveBackgroundTask(task);
        return task;
    }

    private async Task RunLoadAsync(bool startLoadedTailing)
    {
        if (IsShutdownOrDisposed)
            return;

        TryCaptureSessionContext();

        _loadCts?.Cancel();
        // The superseded load still reads its token after asynchronous publication.
        // Its own finally block owns disposal once that load has finished.
        var cts = new CancellationTokenSource();
        var loadToken = cts.Token;
        _loadCts = cts;
        var encodingReady = new TaskCompletionSource<FileEncoding>(TaskCreationOptions.RunContinuationsAsynchronously);
        _encodingReady = encodingReady;

        await PublishLoadStartedAsync().ConfigureAwait(false);

        LineIndex? retiredIndex = null;
        LineIndex? newIndex = null;
        var priorGenerationToken = CurrentGenerationToken;
        try
        {
            var encodingDecision = await ResolveEffectiveEncodingAsync(loadToken).ConfigureAwait(false);
            var resolvedEncoding = encodingDecision.ResolvedEncoding;
            await PublishEncodingDecisionAsync(encodingDecision).ConfigureAwait(false);
            encodingReady.TrySetResult(resolvedEncoding);
            await InvokeOnSessionContextAsync(() => LoadPhase = FileLoadPhase.Queued).ConfigureAwait(false);

            using (await _lineIndexGate.EnterWriteAsync(loadToken).ConfigureAwait(false))
            {
                retiredIndex = _lineIndex;
                _lineIndex = null;
            }

            await PublishCurrentGenerationTokenChangedAsync(
                priorGenerationToken,
                FileGenerationToken.Unknown).ConfigureAwait(false);

            retiredIndex?.Dispose();
            retiredIndex = null;

            newIndex = await BuildIndexOffUiAsync(resolvedEncoding, loadToken).ConfigureAwait(false);
            loadToken.ThrowIfCancellationRequested();
            var totalLines = newIndex.LineCount;
            var fileSizeBytes = newIndex.FileSize;
            var lastModifiedLocal = GetLastModifiedLocal(FilePath);
            using (await _lineIndexGate.EnterWriteAsync(loadToken).ConfigureAwait(false))
            {
                _lineIndex = newIndex;
            }

            await PublishCurrentGenerationTokenChangedAsync(
                FileGenerationToken.Unknown,
                newIndex.GenerationToken).ConfigureAwait(false);

            newIndex = null;
            await PublishTotalLinesAsync(totalLines).ConfigureAwait(false);
            await PublishFileMetadataAsync(fileSizeBytes, lastModifiedLocal).ConfigureAwait(false);
            await PublishFileMissingAsync(false).ConfigureAwait(false);

            if (IsShutdownOrDisposed)
            {
                await InvokeOnSessionContextAsync(() => IsSuspended = true).ConfigureAwait(false);
                return;
            }

            if (startLoadedTailing)
                await _tailCoordinator.StartLoadedTailingAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            newIndex?.Dispose();
            return;
        }
        catch (Exception ex)
        {
            newIndex?.Dispose();
            await PublishLoadFailureAsync(ex.Message).ConfigureAwait(false);
        }
        finally
        {
            encodingReady.TrySetCanceled();
            if (ReferenceEquals(_loadCts, cts))
            {
                _loadCts = null;
                await PublishLoadCompletedAsync().ConfigureAwait(false);
            }

            cts.Dispose();
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

    internal async Task<TResult?> WithLineIndexLeaseAsync<TResult>(
        Func<LineIndex, FileEncoding, CancellationToken, Task<TResult>> action,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        using var lease = await AcquireLineIndexLeaseAsync(ct).ConfigureAwait(false);
        if (lease == null)
            return default;

        return await action(lease.LineIndex, lease.Encoding, ct).ConfigureAwait(false);
    }

    internal async Task<bool> WithLineIndexLeaseAsync(
        Func<LineIndex, FileEncoding, CancellationToken, Task> action,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        using var lease = await AcquireLineIndexLeaseAsync(ct).ConfigureAwait(false);
        if (lease == null)
            return false;

        await action(lease.LineIndex, lease.Encoding, ct).ConfigureAwait(false);
        return true;
    }

    internal Task<FileTailBaseline?> ReadTailBaselineAsync(CancellationToken ct)
        => WithLineIndexLeaseAsync<FileTailBaseline?>(
            (index, _, _) => Task.FromResult<FileTailBaseline?>(new(index.FileSize, index.GenerationToken)), ct);

    internal async Task<int?> UpdateLineIndexLineCountAsync(CancellationToken ct)
        => (await UpdateLineIndexAsync(ct).ConfigureAwait(false))?.UpdatedLineCount;

    internal async Task<LineIndexUpdateResult?> UpdateLineIndexAsync(
        CancellationToken ct,
        FileChangeHint changeHint = FileChangeHint.None)
    {
        int updatedLineCount;
        int previousLineCount;
        var isGenerationReset = false;
        long? fileSizeBytes = null;
        DateTime? lastModifiedLocal = null;
        LineIndex? retiredIndex = null;
        var previousGenerationToken = FileGenerationToken.Unknown;
        var updatedGenerationToken = FileGenerationToken.Unknown;
        using (await _lineIndexGate.EnterWriteAsync(ct).ConfigureAwait(false))
        {
            if (_lineIndex == null)
                return null;

            var encoding = EffectiveEncoding;
            var existingIndex = _lineIndex;
            previousGenerationToken = existingIndex.GenerationToken;
            previousLineCount = existingIndex.LineCount;
            var updatedIndex = await UpdateIndexOffUiAsync(
                existingIndex,
                encoding,
                changeHint,
                ct).ConfigureAwait(false);
            isGenerationReset = !ReferenceEquals(existingIndex, updatedIndex) && updatedIndex.ReplacesPriorGeneration;
            if (!ReferenceEquals(existingIndex, updatedIndex))
            {
                retiredIndex = existingIndex;
            }

            _lineIndex = updatedIndex;
            updatedGenerationToken = updatedIndex.GenerationToken;
            updatedLineCount = updatedIndex.LineCount;
            fileSizeBytes = updatedIndex.FileSize;
            lastModifiedLocal = GetLastModifiedLocal(FilePath);
        }

        retiredIndex?.Dispose();

        await PublishCurrentGenerationTokenChangedAsync(
            previousGenerationToken,
            updatedGenerationToken).ConfigureAwait(false);
        await PublishFileMetadataAsync(fileSizeBytes.Value, lastModifiedLocal).ConfigureAwait(false);
        if (isGenerationReset)
        {
            await PublishSearchContentVersionIncrementAsync().ConfigureAwait(false);
            await PublishTotalLinesAsync(updatedLineCount).ConfigureAwait(false);
            return new LineIndexUpdateResult(previousLineCount, updatedLineCount, true);
        }

        if (updatedLineCount == previousLineCount)
            return null;

        await PublishTotalLinesAsync(updatedLineCount).ConfigureAwait(false);
        return new LineIndexUpdateResult(previousLineCount, updatedLineCount, false);
    }

    internal async Task ResetLineIndexAsync()
    {
        LineIndex? retiredIndex;
        var previousGenerationToken = FileGenerationToken.Unknown;
        using (await _lineIndexGate.EnterWriteAsync(CancellationToken.None).ConfigureAwait(false))
        {
            retiredIndex = _lineIndex;
            previousGenerationToken = retiredIndex?.GenerationToken ?? FileGenerationToken.Unknown;
            _lineIndex = null;
        }

        retiredIndex?.Dispose();
        await PublishCurrentGenerationTokenChangedAsync(
            previousGenerationToken,
            FileGenerationToken.Unknown).ConfigureAwait(false);
        await PublishSearchContentVersionIncrementAsync().ConfigureAwait(false);
    }

    internal async Task ResetAutomaticReloadDelayAsync()
    {
        using (await _lineIndexGate.EnterWriteAsync(CancellationToken.None).ConfigureAwait(false))
            _lineIndex?.ResetAutomaticReloadDelay();
    }

    internal void SetTotalLinesForTesting(int value)
        => TotalLines = value;

    internal void BeginShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
            return;

        _loadCts?.Cancel();
        _tailCoordinator.BeginShutdown();
        _ = InvokeOnSessionContextAsync(() => IsLoading = false);
    }

    internal IReadOnlyList<IFileSessionClient> GetClientSnapshots()
    {
        lock (_clientGate)
            return _clients.ToArray();
    }

    internal Task InvokeOnSessionContextAsync(Action action)
        => _uiDispatcher.InvokeAsync(action);

    internal Task InvokeOnSessionContextAsync(Func<Task> action)
        => _uiDispatcher.InvokeAsync(action);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            return;

        BeginShutdown();
        _tailCoordinator.Dispose();
        _ = EnsureLineIndexDisposedTask();
    }

    private void TryCaptureSessionContext() => _uiDispatcher.CaptureCurrentContextIfUnset();

    private EncodingHelper.EncodingDecision ResolveEncodingDecision()
        => RequestedEncoding == FileEncoding.Auto
            ? _encodingDetectionService.ResolveEncodingDecision(FilePath, RequestedEncoding)
            : EncodingHelper.ResolveManualEncodingDecision(RequestedEncoding);

    private async Task<EncodingHelper.EncodingDecision> ResolveEffectiveEncodingAsync(CancellationToken ct, bool preview = false)
    {
        if (RequestedEncoding == FileEncoding.Auto && SupportsInteractiveLoading && GetDashboardScopes().Count > 0)
        {
            using var lease = await _loadScheduler!.AcquireAsync(FilePath, GetDashboardScopes, preview, ct).ConfigureAwait(false);
            // The synchronous detector may be inside an OS read. Retain admission until it returns.
            var decision = await Task.Run(() => _encodingDetectionService.ResolveEncodingDecision(FilePath, RequestedEncoding)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return decision;
        }
        return RequestedEncoding == FileEncoding.Auto
            ? await Task.Run(() => _encodingDetectionService.ResolveEncodingDecision(FilePath, RequestedEncoding)).WaitAsync(ct).ConfigureAwait(false)
            : EncodingHelper.ResolveManualEncodingDecision(RequestedEncoding);
    }

    private void ApplyEncodingDecision(EncodingHelper.EncodingDecision decision)
    {
        EffectiveEncoding = decision.ResolvedEncoding;
        EncodingStatusText = decision.StatusText;
    }

    private Task<LineIndex> BuildIndexOffUiAsync(FileEncoding encoding, CancellationToken ct)
        => Task.Run(async () =>
        {
            if (!_interactiveLoading || !SupportsInteractiveLoading)
                return await _logReader.BuildIndexAsync(FilePath, encoding, ct).ConfigureAwait(false);
            IInteractiveIndexBuild? build = null;
            var lastPublished = 0L;
            var lastAttempt = 0;
            var encodingChanges = 0;
            try
            {
                while (true)
                {
                    bool completed;
                    var created = false;
                    var fallback = false;
                    using (var lease = await _loadScheduler!.AcquireAsync(FilePath, GetDashboardScopes, preview: false, ct).ConfigureAwait(false))
                    {
                        if (build == null)
                        {
                            build = await ((IInteractiveLogReaderService)_logReader).CreateIndexBuildAsync(FilePath, encoding, ct).ConfigureAwait(false);
                            created = true;
                            if (!build.CanResume)
                            {
                                await build.DisposeAsync().ConfigureAwait(false);
                                build = null;
                                fallback = true;
                            }
                        }
                        completed = !created && await build!.AdvanceAsync(InteractiveLogLoadLimits.ScanQuantumBytes, lease.ShouldYield, ct).ConfigureAwait(false);
                        if (!completed && build != null)
                            await build.SuspendAsync().ConfigureAwait(false);
                    }
                    if (fallback)
                    {
                        await InvokeOnSessionContextAsync(() => LoadPhase = FileLoadPhase.Indexing).ConfigureAwait(false);
                        using var lease = await _loadScheduler!.AcquireAsync(FilePath, GetDashboardScopes, preview: false, ct).ConfigureAwait(false);
                        return await _logReader.BuildIndexAsync(FilePath, encoding, ct).ConfigureAwait(false);
                    }
                    var progress = build!.Progress;
                    if (progress.Attempt > lastAttempt && lastAttempt > 0 && RequestedEncoding == FileEncoding.Auto)
                    {
                        var decision = await ResolveEffectiveEncodingAsync(ct).ConfigureAwait(false);
                        if (decision.ResolvedEncoding != encoding)
                        {
                            if (++encodingChanges >= 2)
                                throw new IOException("The file encoding changed repeatedly while indexing.");
                            encoding = decision.ResolvedEncoding;
                            await build.DisposeAsync().ConfigureAwait(false);
                            build = null;
                            await PublishEncodingDecisionAsync(decision).ConfigureAwait(false);
                            lastAttempt = 0;
                            continue;
                        }
                    }
                    var now = Environment.TickCount64;
                    if (completed || progress.Attempt != lastAttempt || now - lastPublished >= InteractiveLogLoadLimits.ProgressIntervalMilliseconds)
                    {
                        await InvokeOnSessionContextAsync(() =>
                        {
                            if (!ct.IsCancellationRequested && !IsShutdownOrDisposed)
                            {
                                IndexProgress = progress;
                                LoadPhase = FileLoadPhase.Indexing;
                                OnPropertyChanged(nameof(IndexProgress));
                            }
                        }).ConfigureAwait(false);
                        lastPublished = now;
                        lastAttempt = progress.Attempt;
                    }
                    if (completed)
                        return build.TakeCompletedIndex();
                }
            }
            finally
            {
                if (build != null)
                    await build.DisposeAsync().ConfigureAwait(false);
            }
        }, ct);

    private Task<LineIndex> UpdateIndexOffUiAsync(
        LineIndex lineIndex,
        FileEncoding encoding,
        FileChangeHint changeHint,
        CancellationToken ct)
        => Task.Run(async () =>
            await _logReader.UpdateIndexAsync(
                FilePath,
                lineIndex,
                encoding,
                changeHint,
                ct).ConfigureAwait(false), ct);

    private void PauseWithoutClients()
    {
        _loadCts?.Cancel();
        _tailCoordinator.SuspendTailing();
        _ = InvokeOnSessionContextAsync(() => IsLoading = false);
    }

    private async Task<LineIndexLease?> AcquireLineIndexLeaseAsync(CancellationToken ct)
    {
        var releaser = await _lineIndexGate.EnterReadAsync(ct).ConfigureAwait(false);
        try
        {
            var lineIndex = Volatile.Read(ref _lineIndex);
            if (lineIndex == null)
            {
                releaser.Dispose();
                return null;
            }

            return new LineIndexLease(releaser, lineIndex, EffectiveEncoding);
        }
        catch
        {
            releaser.Dispose();
            throw;
        }
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

    private async Task DisposeLineIndexAsync()
    {
        var previousGenerationToken = FileGenerationToken.Unknown;
        using (await _lineIndexGate.EnterWriteAsync(CancellationToken.None).ConfigureAwait(false))
        {
            previousGenerationToken = _lineIndex?.GenerationToken ?? FileGenerationToken.Unknown;
            _lineIndex?.Dispose();
            _lineIndex = null;
        }

        await PublishCurrentGenerationTokenChangedAsync(
            previousGenerationToken,
            FileGenerationToken.Unknown).ConfigureAwait(false);
    }

    private Task PublishLoadStartedAsync()
        => InvokeOnSessionContextAsync(() =>
        {
            IsLoading = true;
            LoadPhase = FileLoadPhase.Resolving;
            IndexProgress = default;
            OnPropertyChanged(nameof(IndexProgress));
            HasLoadError = false;
            LastErrorMessage = null;
        });

    private Task PublishLoadCompletedAsync()
        => InvokeOnSessionContextAsync(() =>
        {
            IsLoading = false;
            LoadPhase = HasLoadError ? FileLoadPhase.Failed : HasNoLineIndex ? FileLoadPhase.Pending : FileLoadPhase.Indexed;
        });

    private Task PublishLoadFailureAsync(string errorMessage)
        => InvokeOnSessionContextAsync(() =>
        {
            LastErrorMessage = errorMessage;
            HasLoadError = true;
        });

    private Task PublishEncodingDecisionAsync(EncodingHelper.EncodingDecision decision)
        => InvokeOnSessionContextAsync(() => ApplyEncodingDecision(decision));

    private Task PublishTotalLinesAsync(int totalLines)
        => InvokeOnSessionContextAsync(() => TotalLines = totalLines);

    private Task PublishFileMetadataAsync(long fileSizeBytes, DateTime? lastModifiedLocal)
        => InvokeOnSessionContextAsync(() =>
        {
            FileSizeBytes = fileSizeBytes;
            LastModifiedLocal = lastModifiedLocal;
        });

    internal Task PublishFileMissingAsync(bool isFileMissing)
        => InvokeOnSessionContextAsync(() => IsFileMissing = isFileMissing);

    private Task PublishSearchContentVersionIncrementAsync()
        => InvokeOnSessionContextAsync(() => SearchContentVersion++);

    private Task PublishCurrentGenerationTokenChangedAsync(
        FileGenerationToken previousToken,
        FileGenerationToken currentToken)
    {
        return previousToken == currentToken
            ? Task.CompletedTask
            : InvokeOnSessionContextAsync(() => OnPropertyChanged(nameof(CurrentGenerationToken)));
    }

    private static DateTime? GetLastModifiedLocal(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return null;

            return File.GetLastWriteTime(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void ObserveBackgroundTask(Task task)
    {
        if (task.IsCompleted)
        {
            if (task.IsFaulted) _ = task.Exception;
            return;
        }

        _ = task.ContinueWith(
            static completedTask => _ = completedTask.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private sealed class LineIndexLease : IDisposable
    {
        private AsyncReadWriteGate.Releaser _releaser;

        public LineIndexLease(AsyncReadWriteGate.Releaser releaser, LineIndex lineIndex, FileEncoding encoding)
        {
            _releaser = releaser;
            LineIndex = lineIndex;
            Encoding = encoding;
        }

        public LineIndex LineIndex { get; }

        public FileEncoding Encoding { get; }

        public void Dispose()
        {
            _releaser.Dispose();
            _releaser = default;
        }
    }

    private sealed class AsyncReadWriteGate
    {
        private readonly SemaphoreSlim _readerMutex = new(1, 1);
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private int _readerCount;

        public SemaphoreSlim WriteLock => _writeLock;

        public async Task<Releaser> EnterReadAsync(CancellationToken ct)
        {
            await _readerMutex.WaitAsync(ct).ConfigureAwait(false);
            var writerLockHeld = false;
            try
            {
                if (_readerCount == 0)
                {
                    await _writeLock.WaitAsync(ct).ConfigureAwait(false);
                    writerLockHeld = true;
                }

                _readerCount++;
                writerLockHeld = false;
                return new Releaser(this, isRead: true);
            }
            catch
            {
                if (writerLockHeld)
                    _writeLock.Release();

                throw;
            }
            finally
            {
                _readerMutex.Release();
            }
        }

        public async Task<Releaser> EnterWriteAsync(CancellationToken ct)
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            return new Releaser(this, isRead: false);
        }

        private void ExitRead()
        {
            _readerMutex.Wait();
            try
            {
                _readerCount--;
                if (_readerCount == 0)
                    _writeLock.Release();
            }
            finally
            {
                _readerMutex.Release();
            }
        }

        private void ExitWrite() => _writeLock.Release();

        internal struct Releaser : IDisposable
        {
            private readonly AsyncReadWriteGate? _owner;
            private readonly bool _isRead;

            public Releaser(AsyncReadWriteGate owner, bool isRead)
            {
                _owner = owner;
                _isRead = isRead;
            }

            public void Dispose()
            {
                if (_owner == null)
                    return;

                if (_isRead)
                    _owner.ExitRead();
                else
                    _owner.ExitWrite();
            }
        }
    }
}
