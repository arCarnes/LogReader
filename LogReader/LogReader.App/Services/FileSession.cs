namespace LogReader.App.Services;

using System.Windows;
using System.Windows.Threading;
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
    private readonly AsyncReadWriteGate _lineIndexGate = new();
    private readonly LogTailCoordinator _tailCoordinator;
    private readonly object _clientGate = new();
    private readonly List<IFileSessionClient> _clients = new();
    private readonly SynchronizationContext? _sessionContext = NormalizeSynchronizationContext(SynchronizationContext.Current);

    private LineIndex? _lineIndex;
    private CancellationTokenSource? _loadCts;
    private Task? _lineIndexDisposeTask;
    private SynchronizationContext? _capturedSessionContext;
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

    internal SemaphoreSlim DebugLineIndexLock => _lineIndexGate.WriteLock;

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

        TryCaptureSessionContext();

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        var cts = new CancellationTokenSource();
        _loadCts = cts;

        await PublishLoadStartedAsync().ConfigureAwait(false);

        LineIndex? retiredIndex = null;
        LineIndex? newIndex = null;
        try
        {
            var encodingDecision = await ResolveEffectiveEncodingAsync(cts.Token).ConfigureAwait(false);
            var resolvedEncoding = encodingDecision.ResolvedEncoding;
            await PublishEncodingDecisionAsync(encodingDecision).ConfigureAwait(false);

            using (await _lineIndexGate.EnterWriteAsync(cts.Token).ConfigureAwait(false))
            {
                retiredIndex = _lineIndex;
                _lineIndex = null;
            }

            retiredIndex?.Dispose();
            retiredIndex = null;

            newIndex = await BuildIndexOffUiAsync(resolvedEncoding, cts.Token).ConfigureAwait(false);
            var totalLines = newIndex.LineCount;
            using (await _lineIndexGate.EnterWriteAsync(cts.Token).ConfigureAwait(false))
            {
                _lineIndex = newIndex;
            }

            newIndex = null;
            await PublishTotalLinesAsync(totalLines).ConfigureAwait(false);

            if (IsShutdownOrDisposed)
            {
                await InvokeOnSessionContextAsync(() => IsSuspended = true).ConfigureAwait(false);
                return;
            }

            _tailCoordinator.StartLoadedTailing();
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

    internal async Task<int?> UpdateLineIndexLineCountAsync(CancellationToken ct)
    {
        int? updatedLineCount;
        using (await _lineIndexGate.EnterWriteAsync(ct).ConfigureAwait(false))
        {
            if (_lineIndex == null)
                return null;

            var encoding = EffectiveEncoding;
            _lineIndex = await UpdateIndexOffUiAsync(_lineIndex, encoding, ct).ConfigureAwait(false);
            updatedLineCount = _lineIndex.LineCount;
        }

        if (updatedLineCount != null)
            await PublishTotalLinesAsync(updatedLineCount.Value).ConfigureAwait(false);

        return updatedLineCount;
    }

    internal async Task ResetLineIndexAsync()
    {
        LineIndex? retiredIndex;
        using (await _lineIndexGate.EnterWriteAsync(CancellationToken.None).ConfigureAwait(false))
        {
            retiredIndex = _lineIndex;
            _lineIndex = null;
        }

        retiredIndex?.Dispose();
        await PublishSearchContentVersionIncrementAsync().ConfigureAwait(false);
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
    {
        ArgumentNullException.ThrowIfNull(action);

        var sessionContext = _capturedSessionContext ?? _sessionContext;
        if (sessionContext != null)
        {
            if (SynchronizationContext.Current == sessionContext)
            {
                action();
                return Task.CompletedTask;
            }

            try
            {
                sessionContext.Send(static state =>
                {
                    var callback = (Action)state!;
                    callback();
                }, action);
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task;
    }

    internal Task InvokeOnSessionContextAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var sessionContext = _capturedSessionContext ?? _sessionContext;
        if (sessionContext != null)
        {
            if (SynchronizationContext.Current == sessionContext)
                return action();

            try
            {
                Task? task = null;
                sessionContext.Send(static state =>
                {
                    var callbackState = ((Func<Task> Callback, Action<Task> Publish))state!;
                    callbackState.Publish(callbackState.Callback());
                }, (action, (Action<Task>)(createdTask => task = createdTask)));
                return task ?? Task.CompletedTask;
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            return action();

        return dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task.Unwrap();
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

    private void TryCaptureSessionContext()
    {
        var currentContext = NormalizeSynchronizationContext(SynchronizationContext.Current);
        if (currentContext == null)
            return;

        Interlocked.CompareExchange(ref _capturedSessionContext, currentContext, null);
    }

    private EncodingHelper.EncodingDecision ResolveEncodingDecision()
        => RequestedEncoding == FileEncoding.Auto
            ? _encodingDetectionService.ResolveEncodingDecision(FilePath, RequestedEncoding)
            : EncodingHelper.ResolveManualEncodingDecision(RequestedEncoding);

    private async Task<EncodingHelper.EncodingDecision> ResolveEffectiveEncodingAsync(CancellationToken ct)
    {
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
            await _logReader.BuildIndexAsync(FilePath, encoding, ct).ConfigureAwait(false), ct);

    private Task<LineIndex> UpdateIndexOffUiAsync(LineIndex lineIndex, FileEncoding encoding, CancellationToken ct)
        => Task.Run(async () =>
            await _logReader.UpdateIndexAsync(FilePath, lineIndex, encoding, ct).ConfigureAwait(false), ct);

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
        using (await _lineIndexGate.EnterWriteAsync(CancellationToken.None).ConfigureAwait(false))
        {
            _lineIndex?.Dispose();
            _lineIndex = null;
        }
    }

    private Task PublishLoadStartedAsync()
        => InvokeOnSessionContextAsync(() =>
        {
            IsLoading = true;
            HasLoadError = false;
            LastErrorMessage = null;
        });

    private Task PublishLoadCompletedAsync()
        => InvokeOnSessionContextAsync(() => IsLoading = false);

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

    private Task PublishSearchContentVersionIncrementAsync()
        => InvokeOnSessionContextAsync(() => SearchContentVersion++);

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

    private static SynchronizationContext? NormalizeSynchronizationContext(SynchronizationContext? context)
    {
        if (context == null)
            return null;

        var assemblyName = context.GetType().Assembly.GetName().Name;
        if (!string.IsNullOrWhiteSpace(assemblyName) &&
            assemblyName.StartsWith("xunit", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return context;
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
