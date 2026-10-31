namespace LogReader.App.Services;

internal sealed class TabMemberRefreshScheduler
{
    private static readonly Task RejectedTask = Task.FromCanceled(new CancellationToken(canceled: true));
    private readonly object _gate = new();
    private readonly Func<TabMemberRefreshRequest, CancellationToken, Task> _executeAsync;
    private readonly CancellationTokenSource _shutdownCts = new();
    private RefreshBatch? _runningBatch;
    private RefreshBatch? _pendingBatch;
    private Task? _shutdownTask;
    private bool _isShuttingDown;

    public TabMemberRefreshScheduler(Func<TabMemberRefreshRequest, CancellationToken, Task> executeAsync)
    {
        ArgumentNullException.ThrowIfNull(executeAsync);
        _executeAsync = executeAsync;
    }

    public Task Queue(TabMemberRefreshRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        RefreshBatch? batchToStart = null;
        Task completionTask;
        lock (_gate)
        {
            if (_isShuttingDown)
                return RejectedTask;

            if (_runningBatch == null)
            {
                batchToStart = new RefreshBatch(request);
                _runningBatch = batchToStart;
                completionTask = batchToStart.CompletionTask;
            }
            else if (_pendingBatch == null)
            {
                _pendingBatch = new RefreshBatch(request);
                completionTask = _pendingBatch.CompletionTask;
            }
            else
            {
                _pendingBatch.Merge(request);
                completionTask = _pendingBatch.CompletionTask;
            }
        }

        if (batchToStart != null)
            StartBatch(batchToStart);

        return completionTask;
    }

    internal Task ShutdownTask
    {
        get
        {
            lock (_gate)
                return _shutdownTask ?? DrainAsync(_runningBatch?.CompletionTask ?? Task.CompletedTask);
        }
    }

    private async Task DrainAsync(Task running)
    {
        try { await running.ConfigureAwait(false); }
        catch (OperationCanceledException) when (_isShuttingDown) { }
    }

    public void Shutdown()
    {
        RefreshBatch? pendingBatch;
        lock (_gate)
        {
            if (_isShuttingDown)
                return;

            _isShuttingDown = true;
            _shutdownTask = DrainAsync(_runningBatch?.CompletionTask ?? Task.CompletedTask);
            ObserveBatchTask(_shutdownTask);
            pendingBatch = _pendingBatch;
            _pendingBatch = null;
        }

        try { _shutdownCts.Cancel(); }
        finally { pendingBatch?.Cancel(_shutdownCts.Token); }
    }

    private void StartBatch(RefreshBatch batch)
    {
        ObserveBatchTask(batch.CompletionTask);
        _ = RunBatchAsync(batch);
    }

    private async Task RunBatchAsync(RefreshBatch batch)
    {
        try
        {
            _shutdownCts.Token.ThrowIfCancellationRequested();
            await _executeAsync(batch.CreateRequest(), _shutdownCts.Token);
            _shutdownCts.Token.ThrowIfCancellationRequested();
            batch.Complete();
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
            batch.Cancel(_shutdownCts.Token);
        }
        catch (Exception ex)
        {
            batch.Fail(ex);
        }
        finally
        {
            RefreshBatch? batchToStart = null;
            lock (_gate)
            {
                if (ReferenceEquals(_runningBatch, batch))
                {
                    _runningBatch = null;
                    if (!_isShuttingDown && _pendingBatch != null)
                    {
                        batchToStart = _pendingBatch;
                        _pendingBatch = null;
                        _runningBatch = batchToStart;
                    }
                }
            }

            if (batchToStart != null)
                StartBatch(batchToStart);
        }
    }

    private static void ObserveBatchTask(Task task)
    {
        _ = task.ContinueWith(
            completedTask => _ = completedTask.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private sealed class RefreshBatch
    {
        private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<string, string> _changedFilePaths = new(StringComparer.Ordinal);
        private readonly HashSet<string> _dashboardIds = new(StringComparer.Ordinal);

        public RefreshBatch(TabMemberRefreshRequest request)
        {
            RequiresFullRefresh = request.RequiresFullRefresh;
            if (request.DashboardIds != null)
                _dashboardIds.UnionWith(request.DashboardIds);
            if (!RequiresFullRefresh)
                MergeFilePaths(request.ChangedFilePaths);
        }

        public bool RequiresFullRefresh { get; private set; }

        public Task CompletionTask => _completion.Task;

        public void Merge(TabMemberRefreshRequest request)
        {
            if (request.DashboardIds != null)
                _dashboardIds.UnionWith(request.DashboardIds);
            if (RequiresFullRefresh)
                return;

            if (request.RequiresFullRefresh)
            {
                RequiresFullRefresh = true;
                _changedFilePaths.Clear();
                return;
            }

            MergeFilePaths(request.ChangedFilePaths);
        }

        public TabMemberRefreshRequest CreateRequest()
            => new(RequiresFullRefresh, _changedFilePaths, _dashboardIds);

        public void Complete()
            => _completion.TrySetResult(true);

        public void Cancel(CancellationToken cancellationToken)
            => _completion.TrySetCanceled(cancellationToken);

        public void Fail(Exception exception)
            => _completion.TrySetException(exception);

        private void MergeFilePaths(IReadOnlyDictionary<string, string> changedFilePaths)
        {
            foreach (var (fileId, filePath) in changedFilePaths)
                _changedFilePaths[fileId] = filePath;
        }
    }
}
