namespace LogReader.Tests;

using System.Collections.Concurrent;
using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public sealed class SearchTailMonitoringSessionTests
{
    [Fact]
    public async Task CoalescedSignalsAndSignalAtSettlement_UseOneWorkerAndCommitEveryRange()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            using var tab = await CreateTabAsync();
            tab.TotalLines = 1;
            var started = NewSignal();
            var release = NewSignal();
            var scanned = new ConcurrentQueue<(long? Start, long? End)>();
            var active = 0;
            var maxActive = 0;
            var reader = new Search(async request =>
            {
                var count = Interlocked.Increment(ref active);
                Interlocked.Exchange(ref maxActive, Math.Max(Volatile.Read(ref maxActive), count));
                scanned.Enqueue((request.StartLineNumber, request.EndLineNumber));
                try
                {
                    if (request.StartLineNumber == 1)
                    {
                        started.TrySetResult();
                        await release.Task;
                    }
                    return Result(tab.FilePath, request);
                }
                finally { Interlocked.Decrement(ref active); }
            });
            var sink = new Sink();
            var complete = NewSignal();
            sink.OnSettled = () =>
            {
                if (sink.LastEvaluated == 11) tab.TotalLines = 12; // Signal at the drain-to-park boundary.
                else if (sink.LastEvaluated == 12) complete.TrySetResult();
            };
            var session = new SearchTailMonitoringSession(reader, WpfUiDispatcher.Instance);
            session.Start([Tracker(tab)], sink);
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                for (var i = 2; i <= 11; i++) tab.TotalLines = i;
                release.TrySetResult();
                await complete.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(new[] { (1L, 1L), (2L, 11L), (12L, 12L) }, scanned.Select(range => (range.Start!.Value, range.End!.Value)));
                Assert.Equal(1, maxActive);
                Assert.Equal(1, session.ActiveWorkerCount);
                await session.Retire().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, session.ActiveWorkerCount);
                Assert.Equal(0, session.TrackerCount);
                var scans = scanned.Count;
                tab.TotalLines++;
                Assert.Equal(scans, scanned.Count);
            }
            finally
            {
                release.TrySetResult();
                await session.Retire().WaitAsync(TimeSpan.FromSeconds(5));
                tab.Dispose();
                await tab.ActiveSession.ShutdownTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
        });
    }

    [Fact]
    public async Task Retirement_RetainsHeldRetryAndTokenSourceUntilTheWorkerDrains()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            using var tab = await CreateTabAsync();
            tab.TotalLines = 1;
            var retryStarted = NewSignal();
            var releaseRetry = NewSignal();
            var sink = new Sink();
            var session = new SearchTailMonitoringSession(new Search(_ => Task.FromResult(new SearchResult { FilePath = tab.FilePath })),
                WpfUiDispatcher.Instance, retryDelayAsync: async ct =>
                {
                    retryStarted.TrySetResult();
                    await releaseRetry.Task; // Deliberately prove ownership independent of cancellation.
                    Assert.NotNull(ct.WaitHandle);
                });
            session.Start([Tracker(tab)], sink);
            try
            {
                await retryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                sink.IsCurrent = false;
                var retired = session.Retire();
                Assert.False(retired.IsCompleted);
                Assert.Same(retired, session.Retire());
                Assert.Equal(0, session.TrackerCount);
                releaseRetry.TrySetResult();
                await retired.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, session.ActiveWorkerCount);
                Assert.Empty(sink.Results);
            }
            finally
            {
                releaseRetry.TrySetResult();
                await session.Retire().WaitAsync(TimeSpan.FromSeconds(5));
                tab.Dispose();
                await tab.ActiveSession.ShutdownTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
        });
    }

    [Fact]
    public async Task UnexpectedWorkerFailure_IsPublishedAndRetainedAtCompletion()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            using var tab = await CreateTabAsync();
            tab.TotalLines = 1;
            var failure = new InvalidOperationException("Synthetic monitor failure");
            var sink = new Sink();
            var session = new SearchTailMonitoringSession(new Search(_ => throw failure), WpfUiDispatcher.Instance);
            session.Start([Tracker(tab)], sink);
            try
            {
                await sink.FailedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Same(failure, sink.Error);
                Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => session.Retire()));
                Assert.True(session.Completion.IsFaulted);
                Assert.Equal(0, session.ActiveWorkerCount);
            }
            finally
            {
                try { await session.Retire().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (InvalidOperationException ex) when (ReferenceEquals(ex, failure)) { }
                tab.Dispose();
                await tab.ActiveSession.ShutdownTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
        });
    }

    [Fact]
    public async Task QueuedPublication_RechecksRetirementWhenItReachesTheUi()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            using var tab = await CreateTabAsync();
            tab.TotalLines = 1;
            var dispatcher = new HeldPublicationDispatcher();
            var sink = new Sink();
            var session = new SearchTailMonitoringSession(new Search(request => Task.FromResult(Result(tab.FilePath, request))), dispatcher);
            session.Start([Tracker(tab)], sink);
            try
            {
                await dispatcher.Queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
                sink.IsCurrent = false;
                var retired = session.Retire();
                Assert.False(retired.IsCompleted);
                dispatcher.Release.TrySetResult();
                await retired.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Empty(sink.Results);
                Assert.Equal(0, session.ActiveWorkerCount);
            }
            finally
            {
                dispatcher.Release.TrySetResult();
                await session.Retire().WaitAsync(TimeSpan.FromSeconds(5));
                tab.Dispose();
                await tab.ActiveSession.ShutdownTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
        });
    }

    [Fact]
    public async Task ThrowingCancellationCallback_DoesNotLoseInitialDrainOrItsFailureEvidence()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var release = NewSignal();
            var failure = new InvalidOperationException("Synthetic cancellation callback failure");
            var session = new SearchTailMonitoringSession(new StubSearchService(), WpfUiDispatcher.Instance, hasInitialExecution: true);
            using var registration = session.Token.Register(() => throw failure);
            var initial = session.RunInitialAsync(async () =>
            {
                await release.Task;
                Assert.NotNull(session.Token.WaitHandle);
            });
            try
            {
                var retired = session.Retire();
                Assert.False(retired.IsCompleted);
                release.TrySetResult();
                await initial.WaitAsync(TimeSpan.FromSeconds(5));
                var error = await Assert.ThrowsAsync<AggregateException>(() => retired);
                Assert.Contains(failure, error.Flatten().InnerExceptions);
            }
            finally
            {
                release.TrySetResult();
                await initial.WaitAsync(TimeSpan.FromSeconds(5));
                try { await session.Retire().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (AggregateException ex) when (ex.Flatten().InnerExceptions.Contains(failure)) { }
            }
        });
    }

    [Fact]
    public async Task ExplicitMonitoring_TruncationStopsTrackerWithoutHistoricalRescan()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            using var tab = await CreateTabAsync();
            tab.TotalLines = 4;
            var sink = new Sink();
            var session = new SearchTailMonitoringSession(new Search(_ => throw new InvalidOperationException("Unexpected rescan")), WpfUiDispatcher.Instance);
            session.Start([Tracker(tab) with { SnapshotLine = 5, IsMonitoringTracker = true }], sink);
            try
            {
                await sink.StoppedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, session.TrackerCount);
                Assert.Empty(sink.Results);
                Assert.Contains("truncated or rotated", sink.StoppedText);
            }
            finally
            {
                await session.Retire().WaitAsync(TimeSpan.FromSeconds(5));
                tab.Dispose();
                await tab.ActiveSession.ShutdownTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
        });
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<LogTabViewModel> CreateTabAsync()
    {
        var tab = new LogTabViewModel("monitor", @"C:\fixture\monitor.log", new StubLogReaderService(),
            new StubFileTailService(), new StubEncodingDetectionService(), new AppSettings());
        await tab.LoadAsync();
        return tab;
    }

    private static SearchTailMonitoringTarget Tracker(LogTabViewModel tab) => new()
    {
        FilePath = tab.FilePath, Tab = tab, Encoding = tab.EffectiveEncoding, RequestedEncoding = tab.Encoding,
        ExpectedGenerationToken = tab.CurrentGenerationToken, SearchContentVersion = tab.SearchContentVersion
    };

    private static SearchResult Result(string path, SearchRequest request)
        => new() { FilePath = path, EvaluatedThroughLine = request.EndLineNumber };

    private sealed class Search(Func<SearchRequest, Task<SearchResult>> search) : ISearchService
    {
        public Task<SearchResult> SearchFileAsync(string path, SearchRequest request, FileEncoding encoding, CancellationToken ct = default) => search(request);
        public Task<SearchResult> SearchFileRangeAsync(string path, SearchRequest request, FileEncoding encoding,
            Func<int, int, FileEncoding, CancellationToken, Task<IReadOnlyList<string>>> read, CancellationToken ct = default) => search(request);
        public Task<IReadOnlyList<SearchResult>> SearchFilesAsync(SearchRequest request, IDictionary<string, FileEncoding> encodings, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class Sink : ISearchTailMonitoringSink
    {
        public bool IsCurrent { get; set; } = true;
        public List<SearchResult> Results { get; } = new();
        public Action? OnSettled { get; set; }
        public long LastEvaluated { get; private set; }
        public Exception? Error { get; private set; }
        public TaskCompletionSource StoppedSignal { get; } = NewSignal();
        public string? StoppedText { get; private set; }
        public TaskCompletionSource FailedSignal { get; } = NewSignal();
        public LogFilterSession.FilterSnapshot? GetFilterSnapshot(string path) => null;
        public SearchRequest CreateRequest(string path, long startLine, long endLine)
            => new() { Query = "captured", FilePaths = [path], StartLineNumber = startLine, EndLineNumber = endLine, MaxHitsPerFile = 10_000, MaxRetainedLineTextLength = 8_192 };
        public void ApplyResult(SearchResult result, SearchTailResultTarget tracker)
        {
            Assert.True(WpfUiDispatcher.Instance.CheckAccess());
            Results.Add(result);
            LastEvaluated = result.EvaluatedThroughLine ?? LastEvaluated;
        }
        public void ClearResult(string path) => Results.Clear();
        public void MonitoringStopped(string statusText, int remainingFiles)
        {
            Assert.True(WpfUiDispatcher.Instance.CheckAccess());
            StoppedText = statusText;
            StoppedSignal.TrySetResult();
        }
        public void Settled() => OnSettled?.Invoke();
        public void Failed(Exception error) { Error = error; FailedSignal.TrySetResult(); }
    }

    private sealed class HeldPublicationDispatcher : IUiDispatcher
    {
        public TaskCompletionSource Queued { get; } = NewSignal();
        public TaskCompletionSource Release { get; } = NewSignal();
        public bool CheckAccess() => WpfUiDispatcher.Instance.CheckAccess();
        public async Task InvokeAsync(Action action)
        {
            Queued.TrySetResult();
            await Release.Task;
            await WpfUiDispatcher.Instance.InvokeAsync(action);
        }
        public Task InvokeAsync(Func<Task> action) => WpfUiDispatcher.Instance.InvokeAsync(action);
    }
}
