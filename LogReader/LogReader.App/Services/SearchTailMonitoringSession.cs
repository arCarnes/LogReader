namespace LogReader.App.Services;

using System.ComponentModel;
using System.IO;
using LogReader.App.ViewModels;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

/// <summary>The presentation boundary for one captured monitoring execution.</summary>
internal interface ISearchTailMonitoringSink
{
    bool IsCurrent { get; }
    LogFilterSession.FilterSnapshot? GetFilterSnapshot(string path);
    SearchRequest CreateRequest(string path, long startLine, long endLine);
    void ApplyResult(SearchResult result, SearchTailResultTarget target);
    void ClearResult(string path);
    void MonitoringStopped(string statusText, int remainingFiles);
    void Settled();
    void Failed(Exception error);
}

/// <summary>Owns subscriptions, coalesced workers, initial execution and retirement for a search.</summary>
internal sealed class SearchTailMonitoringSession
{
    private static readonly TimeSpan TailRetryDelay = TimeSpan.FromMilliseconds(300);
    private const int DisplaySearchMaxHitsPerFile = 10_000;
    private const int TailSearchRangeChunkLineCount = 2_000;
    private readonly ISearchService _searchService;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly Func<CancellationToken, Task> _retryDelayAsync;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, TailSearchTracker> _trackers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TailSearchTracker> _allTrackers = new();
    private readonly List<Task> _workers = new();
    private readonly TaskCompletionSource _initial = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ISearchTailMonitoringSink? _sink;
    private bool _retired;
    private int _activeWorkers;

    internal SearchTailMonitoringSession(ISearchService searchService, IUiDispatcher uiDispatcher, bool hasInitialExecution = false,
        Func<CancellationToken, Task>? retryDelayAsync = null)
    {
        _searchService = searchService;
        _uiDispatcher = uiDispatcher;
        _retryDelayAsync = retryDelayAsync ?? (ct => Task.Delay(TailRetryDelay, ct));
        Token = _cancellation.Token;
        if (!hasInitialExecution) _initial.TrySetResult();
    }

    internal CancellationToken Token { get; }
    internal bool IsCancellationRequested => Token.IsCancellationRequested;
    internal Task Completion => _completion.Task;
    internal int ActiveWorkerCount => Volatile.Read(ref _activeWorkers);
    internal int TrackerCount { get { lock (_gate) return _trackers.Count; } }
    private bool IsActive => !Token.IsCancellationRequested && _sink?.IsCurrent == true;

    internal async Task RunInitialAsync(Func<Task> execute)
    {
        try
        {
            await execute();
            _initial.TrySetResult();
        }
        catch (OperationCanceledException) when (Token.IsCancellationRequested)
        {
            _initial.TrySetResult();
        }
        catch (Exception ex)
        {
            _initial.TrySetException(ex);
            _ = _initial.Task.Exception;
            throw;
        }
    }

    internal void Start(IReadOnlyList<SearchTailMonitoringTarget> targets, ISearchTailMonitoringSink sink)
    {
        var reserved = new List<(TailSearchTracker Tracker, TaskCompletionSource Completion)>();
        lock (_gate)
        {
            if (_retired) return;
            if (_sink != null) throw new InvalidOperationException("Monitoring has already started for this execution.");
            _sink = sink;
            foreach (var target in targets)
            {
                var tracker = new TailSearchTracker
                {
                    FilePath = target.FilePath,
                    Tab = target.Tab,
                    Encoding = target.Encoding,
                    RequestedEncoding = target.RequestedEncoding,
                    SnapshotLine = target.SnapshotLine,
                    LastProcessedLine = target.SnapshotLine,
                    RetainedHitCount = target.RetainedHitCount,
                    SearchContentVersion = target.SearchContentVersion,
                    ExpectedGenerationToken = target.ExpectedGenerationToken,
                    IsMonitoringTracker = target.IsMonitoringTracker
                };
                _trackers.Add(tracker.FilePath, tracker);
                _allTrackers.Add(tracker);
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _workers.Add(completion.Task); // Register every worker before any callback can execute.
                reserved.Add((tracker, completion));
                tracker.PropertyChangedHandler = (_, args) => OnPropertyChanged(tracker, args);
                tracker.Tab.PropertyChanged += tracker.PropertyChangedHandler;
                Signal(tracker);
            }
        }
        foreach (var (tracker, completion) in reserved)
            _ = RunWorkerAsync(tracker, completion);
    }

    private void OnPropertyChanged(TailSearchTracker tracker, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(LogTabViewModel.TotalLines) or nameof(LogTabViewModel.SearchContentVersion) or
            nameof(LogTabViewModel.Encoding) or nameof(LogTabViewModel.EffectiveEncoding) or
            nameof(LogTabViewModel.CurrentGenerationToken) or nameof(LogTabViewModel.IsFilterActive))
            Signal(tracker);
    }

    private void Signal(TailSearchTracker tracker)
    {
        lock (_gate)
        {
            if (_retired || tracker.IsStopped) return;
            Interlocked.Increment(ref tracker.PendingSignalVersion);
            if (tracker.Signal.CurrentCount == 0) tracker.Signal.Release();
        }
    }

    private async Task RunWorkerAsync(TailSearchTracker tracker, TaskCompletionSource completion)
    {
        Interlocked.Increment(ref _activeWorkers);
        Exception? failure = null;
        try
        {
            await _uiDispatcher.InvokeAsync(async () =>
            {
                while (IsActive && !tracker.IsStopped)
                {
                    await tracker.Signal.WaitAsync(Token);
                    while (IsActive && !tracker.IsStopped)
                    {
                        int version;
                        lock (_gate)
                        {
                            tracker.Signal.Wait(0); // Consume signals already represented by this version.
                            version = Volatile.Read(ref tracker.PendingSignalVersion);
                        }
                        var outcome = await ProcessTailTrackerAsync(tracker);
                        if (outcome == TailTrackerProcessOutcome.RetryPendingRange)
                        {
                            if (Volatile.Read(ref tracker.PendingSignalVersion) == version)
                                await _retryDelayAsync(Token);
                            continue;
                        }
                        if (outcome == TailTrackerProcessOutcome.ContinuePendingRange) continue;
                        if (!tracker.IsStopped) await PublishAsync(() => _sink!.Settled());
                        if (Volatile.Read(ref tracker.PendingSignalVersion) == version) break;
                    }
                }
            });
        }
        catch (OperationCanceledException) when (Token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            failure = ex;
            try { await PublishAsync(() => _sink!.Failed(ex)); }
            catch (Exception publicationError) { failure = new AggregateException(ex, publicationError); }
        }
        finally
        {
            Interlocked.Decrement(ref _activeWorkers);
            if (failure == null) completion.TrySetResult();
            else completion.TrySetException(failure);
        }
    }

    internal Task Retire()
    {
        Task[] work;
        lock (_gate)
        {
            if (_retired) return Completion;
            _retired = true; // Close admission and unsubscribe before requesting cancellation.
            foreach (var tracker in _allTrackers)
            {
                if (tracker.PropertyChangedHandler != null)
                    tracker.Tab.PropertyChanged -= tracker.PropertyChangedHandler;
                tracker.IsStopped = true;
            }
            _trackers.Clear();
            work = _workers.Append(_initial.Task).ToArray();
        }
        Exception? cancellationError = null;
        try { _cancellation.Cancel(); }
        catch (Exception ex) { cancellationError = ex; }
        _ = CompleteRetirementAsync(work, cancellationError);
        return Completion;
    }

    private async Task CompleteRetirementAsync(Task[] work, Exception? cancellationError)
    {
        var failures = new List<Exception>();
        if (cancellationError != null) failures.Add(cancellationError);
        var drain = Task.WhenAll(work);
        try { await drain.ConfigureAwait(false); }
        catch (Exception ex)
        {
            if (drain.Exception != null) failures.AddRange(drain.Exception.InnerExceptions);
            else failures.Add(ex);
        }
        finally
        {
            foreach (var tracker in _allTrackers) tracker.Signal.Dispose();
            // A faulted completion is retained by the panel; keep its evidence without
            // retaining the retired tabs, worker closures or presentation sink as well.
            _allTrackers.Clear();
            _workers.Clear();
            _sink = null;
            _cancellation.Dispose();
        }
        if (failures.Count == 0) _completion.TrySetResult();
        else _completion.TrySetException(failures.Count == 1 ? failures[0] : new AggregateException(failures));
    }

    private async Task PublishAsync(Action publish)
    {
        if (!IsActive) return;
        await _uiDispatcher.InvokeAsync(() =>
        {
            if (IsActive) publish(); // Recheck after a queued UI callback reaches its owner.
        });
    }

    private Task ApplyResultAsync(SearchResult result, TailSearchTracker tracker)
        => PublishAsync(() => _sink!.ApplyResult(result, new SearchTailResultTarget(
            tracker.FilePath, tracker.Tab, tracker.Encoding, tracker.LastProcessedLine, tracker.SearchContentVersion,
            new FileScanGenerationEvidence(tracker.ExpectedGenerationToken,
                tracker.ExpectedGenerationToken.IsKnown ? FileGenerationCorrelation.Current : FileGenerationCorrelation.Unknown))));

    private Task ResetTrackerAsync(TailSearchTracker tracker)
        => PublishAsync(() =>
        {
            tracker.SnapshotLine = 0;
            tracker.LastProcessedLine = 0;
            tracker.RetainedHitCount = 0;
            tracker.SearchContentVersion = tracker.Tab.SearchContentVersion;
            tracker.Encoding = tracker.Tab.EffectiveEncoding;
            tracker.RequestedEncoding = tracker.Tab.Encoding;
            tracker.ExpectedGenerationToken = tracker.Tab.CurrentGenerationToken;
            _sink!.ClearResult(tracker.FilePath);
        });

    private Task StopMonitoringForTrackerAsync(TailSearchTracker tracker, string statusText)
        => PublishAsync(() =>
        {
            int remaining;
            lock (_gate)
            {
                if (!_trackers.Remove(tracker.FilePath)) return;
                tracker.IsStopped = true;
                if (tracker.PropertyChangedHandler != null)
                    tracker.Tab.PropertyChanged -= tracker.PropertyChangedHandler;
                remaining = _trackers.Values.Count(item => item.IsMonitoringTracker);
            }
            _sink!.MonitoringStopped(statusText, remaining);
        });

    private async Task<TailTrackerProcessOutcome> ProcessTailTrackerAsync(
        TailSearchTracker tracker)
    {
        var ct = Token;
        if (!IsActive || ct.IsCancellationRequested)
            return TailTrackerProcessOutcome.NoWork;

        if (HasTailTrackerContinuityChanged(tracker))
        {
            if (tracker.IsMonitoringTracker)
            {
                await StopMonitoringForTrackerAsync(
                    tracker,
                    $"Monitoring stopped for {Path.GetFileName(tracker.FilePath)} because the file content or encoding changed.");
                return TailTrackerProcessOutcome.NoWork;
            }

            await ResetTrackerAsync(tracker);
        }

        if (tracker.SearchContentVersion != tracker.Tab.SearchContentVersion)
        {
            if (tracker.IsMonitoringTracker)
            {
                await StopMonitoringForTrackerAsync(
                    tracker,
                    $"Monitoring stopped for {Path.GetFileName(tracker.FilePath)} because the file content changed.");
                return TailTrackerProcessOutcome.NoWork;
            }

            await ResetTrackerAsync(tracker);
        }

        var currentTotalLines = Math.Max(0, tracker.Tab.TotalLines);
        if (currentTotalLines < tracker.LastProcessedLine)
        {
            if (tracker.IsMonitoringTracker)
            {
                await StopMonitoringForTrackerAsync(
                    tracker,
                    $"Monitoring stopped for {Path.GetFileName(tracker.FilePath)} because the file was truncated or rotated.");
                return TailTrackerProcessOutcome.NoWork;
            }

            await ResetTrackerAsync(tracker);
            currentTotalLines = Math.Max(0, tracker.Tab.TotalLines);
        }

        if (currentTotalLines <= tracker.LastProcessedLine)
            return TailTrackerProcessOutcome.NoWork;

        var expectedContentVersion = tracker.SearchContentVersion;
        var startLine = tracker.LastProcessedLine + 1;
        var searchEndLine = Math.Min(currentTotalLines, tracker.LastProcessedLine + TailSearchRangeChunkLineCount);
        var remainingHitBudget = DisplaySearchMaxHitsPerFile - tracker.RetainedHitCount;
        if (remainingHitBudget <= 0)
        {
            tracker.LastProcessedLine = searchEndLine;
            await ApplyResultAsync(CreateCappedTailResult(tracker.FilePath), tracker);
            return searchEndLine < currentTotalLines
                ? TailTrackerProcessOutcome.ContinuePendingRange
                : TailTrackerProcessOutcome.Success;
        }

        var filterSnapshot = _sink!.GetFilterSnapshot(tracker.FilePath);
        if (filterSnapshot is { LastEvaluatedLine: { } filterEvaluatedLine } &&
            filterEvaluatedLine < searchEndLine &&
            filterSnapshot.FilterRequest?.SourceMode != SearchRequestSourceMode.DiskSnapshot)
        {
            if (!filterSnapshot.IsTailEvaluationPaused)
                return TailTrackerProcessOutcome.RetryPendingRange;

            if (filterEvaluatedLine < startLine)
                return TailTrackerProcessOutcome.NoWork;

            searchEndLine = filterEvaluatedLine;
        }

        var request = _sink!.CreateRequest(tracker.FilePath, startLine, searchEndLine);
        request.MaxHitsPerFile = remainingHitBudget;
        SearchResult result;
        try
        {
            result = await Task.Run(async () =>
            {
                return await tracker.Tab.WithLineIndexLeaseAsync(
                    (lineIndex, effectiveEncoding, innerCt) => _searchService.SearchFileRangeAsync(
                        tracker.FilePath,
                        request,
                        effectiveEncoding,
                        (rangeStartLine, rangeCount, rangeEncoding, rangeCt) =>
                            tracker.Tab.ReadLinesOffUiAsync(lineIndex, rangeStartLine, rangeCount, rangeEncoding, rangeCt),
                        innerCt),
                    ct).ConfigureAwait(false)
                    ?? await _searchService.SearchFileAsync(tracker.FilePath, request, tracker.Encoding, ct).ConfigureAwait(false);
            }, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result = new SearchResult { FilePath = tracker.FilePath, Error = ex.Message };
        }
        if (!IsActive || ct.IsCancellationRequested)
            return TailTrackerProcessOutcome.NoWork;

        if (tracker.Tab.SearchContentVersion != expectedContentVersion ||
            tracker.SearchContentVersion != expectedContentVersion ||
            HasTailTrackerContinuityChanged(tracker))
        {
            if (tracker.IsMonitoringTracker && HasTailTrackerContinuityChanged(tracker))
            {
                await StopMonitoringForTrackerAsync(
                    tracker,
                    $"Monitoring stopped for {Path.GetFileName(tracker.FilePath)} because the file content or encoding changed.");
            }

            return TailTrackerProcessOutcome.NoWork;
        }

        result.GenerationEvidence = CreateTailResultGenerationEvidence(result.GenerationEvidence, tracker);
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            await ApplyResultAsync(result, tracker);
            return TailTrackerProcessOutcome.RetryPendingRange;
        }

        var evaluatedThroughLine = Math.Clamp(
            result.EvaluatedThroughLine ?? startLine - 1,
            startLine - 1,
            searchEndLine);
        if (evaluatedThroughLine < startLine)
            return TailTrackerProcessOutcome.RetryPendingRange;

        result = EnforceTailHitBudget(result, remainingHitBudget);
        tracker.RetainedHitCount += result.Hits.Count;
        if (tracker.RetainedHitCount >= DisplaySearchMaxHitsPerFile)
            result.HitLimitExceeded = true;

        tracker.LastProcessedLine = evaluatedThroughLine;
        await ApplyResultAsync(result, tracker);
        if (evaluatedThroughLine < searchEndLine)
            return TailTrackerProcessOutcome.RetryPendingRange;

        return evaluatedThroughLine < currentTotalLines
            ? TailTrackerProcessOutcome.ContinuePendingRange
            : TailTrackerProcessOutcome.Success;
    }

    private static bool HasTailTrackerContinuityChanged(TailSearchTracker tracker)
    {
        if (tracker.RequestedEncoding != tracker.Tab.Encoding ||
            tracker.Encoding != tracker.Tab.EffectiveEncoding)
        {
            return true;
        }

        return tracker.ExpectedGenerationToken.IsKnown &&
               tracker.Tab.CurrentGenerationToken.IsKnown &&
               tracker.ExpectedGenerationToken != tracker.Tab.CurrentGenerationToken;
    }

    private static SearchResult CreateCappedTailResult(string filePath)
        => new()
        {
            FilePath = filePath,
            HitLimitExceeded = true
        };

    private static SearchResult EnforceTailHitBudget(SearchResult result, int remainingHitBudget)
    {
        if (remainingHitBudget < 0)
            remainingHitBudget = 0;

        if (result.Hits.Count <= remainingHitBudget)
            return result;

        result.Hits = result.Hits.Take(remainingHitBudget).ToList();
        result.HitLimitExceeded = true;
        return result;
    }

    private static FileScanGenerationEvidence CreateTailResultGenerationEvidence(
        FileScanGenerationEvidence resultEvidence,
        TailSearchTracker tracker)
    {
        if (resultEvidence.Correlation == FileGenerationCorrelation.Stale ||
            resultEvidence.Token.IsKnown &&
            tracker.ExpectedGenerationToken.IsKnown &&
            resultEvidence.Token != tracker.ExpectedGenerationToken)
        {
            return resultEvidence with { Correlation = FileGenerationCorrelation.Stale };
        }

        if (tracker.ExpectedGenerationToken.IsKnown)
        {
            return new FileScanGenerationEvidence(
                tracker.ExpectedGenerationToken,
                FileGenerationCorrelation.Current);
        }

        return resultEvidence.Token.IsKnown
            ? resultEvidence with { Correlation = FileGenerationCorrelation.Unknown }
            : FileScanGenerationEvidence.Unknown;
    }

    private enum TailTrackerProcessOutcome
    {
        NoWork,
        Success,
        ContinuePendingRange,
        RetryPendingRange
    }


    private sealed class TailSearchTracker
    {
        public string FilePath { get; init; } = string.Empty;
        public FileEncoding Encoding { get; set; }
        public FileEncoding RequestedEncoding { get; set; }
        public FileGenerationToken ExpectedGenerationToken { get; set; }
        public LogTabViewModel Tab { get; init; } = null!;
        public long SnapshotLine { get; set; }
        public long LastProcessedLine { get; set; }
        public int RetainedHitCount { get; set; }
        public int SearchContentVersion { get; set; }
        public bool IsMonitoringTracker { get; init; }
        public PropertyChangedEventHandler? PropertyChangedHandler { get; set; }
        public int PendingSignalVersion;
        internal SemaphoreSlim Signal { get; } = new(0, 1);
        internal bool IsStopped;
    }
}

internal sealed record SearchTailMonitoringTarget
{
    public string FilePath { get; init; } = string.Empty;
    public LogTabViewModel Tab { get; init; } = null!;
    public FileEncoding Encoding { get; init; }
    public FileEncoding RequestedEncoding { get; init; }
    public FileGenerationToken ExpectedGenerationToken { get; init; }
    public long SnapshotLine { get; init; }
    public int RetainedHitCount { get; init; }
    public int SearchContentVersion { get; init; }
    public bool IsMonitoringTracker { get; init; }
}

internal sealed record SearchTailResultTarget(
    string FilePath,
    LogTabViewModel Tab,
    FileEncoding Encoding,
    long SearchBoundaryLine,
    int SearchContentVersion,
    FileScanGenerationEvidence GenerationEvidence);
