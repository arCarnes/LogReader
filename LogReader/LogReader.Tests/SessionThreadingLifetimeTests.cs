namespace LogReader.Tests;

using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

public class SessionThreadingLifetimeTests
{
    [Fact]
    public async Task UpdateLineIndexLineCountAsync_WaitsForOutstandingReadLease()
    {
        var reader = new MutableLogReaderService(Enumerable.Range(1, 3).Select(i => $"Line {i}"));
        using var tab = CreateTab(reader);
        await tab.LoadAsync();

        var leaseEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readLeaseTask = Task.Run(() => tab.WithLineIndexLeaseAsync(
            async (_, _, ct) =>
            {
                leaseEntered.TrySetResult(true);
                await releaseLease.Task.WaitAsync(ct);
            },
            CancellationToken.None));

        await leaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        reader.AppendLine("Line 4");
        var updateTask = tab.UpdateLineIndexLineCountAsync(CancellationToken.None);

        Assert.NotSame(updateTask, await Task.WhenAny(updateTask, Task.Delay(100)));

        releaseLease.TrySetResult(true);
        await readLeaseTask.WaitAsync(TimeSpan.FromSeconds(5));

        var updatedLineCount = await updateTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, updatedLineCount);
        Assert.Equal(4, tab.TotalLines);
    }

    [Fact]
    public async Task ResetLineIndexAsync_WaitsForOutstandingReadLeaseBeforeClearingIndex()
    {
        var reader = new MutableLogReaderService(Enumerable.Range(1, 3).Select(i => $"Line {i}"));
        using var tab = CreateTab(reader);
        await tab.LoadAsync();

        var leaseEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readLeaseTask = Task.Run(() => tab.WithLineIndexLeaseAsync(
            async (_, _, ct) =>
            {
                leaseEntered.TrySetResult(true);
                await releaseLease.Task.WaitAsync(ct);
            },
            CancellationToken.None));

        await leaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var resetTask = tab.ResetLineIndexAsync();
        Assert.NotSame(resetTask, await Task.WhenAny(resetTask, Task.Delay(100)));

        releaseLease.TrySetResult(true);
        await readLeaseTask.WaitAsync(TimeSpan.FromSeconds(5));
        await resetTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(tab.ActiveSession.HasNoLineIndex);
        Assert.Equal(1, tab.SearchContentVersion);
    }

    [Fact]
    public async Task FileRotated_ReloadWaitsForOutstandingReadLeaseBeforePublishingNewState()
    {
        var reader = new MutableLogReaderService(Enumerable.Range(1, 3).Select(i => $"Line {i}"));
        var tailService = new StubFileTailService();
        using var tab = CreateTab(reader, tailService: tailService);
        await tab.LoadAsync();

        var leaseEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readLeaseTask = Task.Run(() => tab.WithLineIndexLeaseAsync(
            async (_, _, ct) =>
            {
                leaseEntered.TrySetResult(true);
                await releaseLease.Task.WaitAsync(ct);
            },
            CancellationToken.None));

        await leaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        reader.ReplaceLines(Enumerable.Range(1, 5).Select(i => $"Rotated {i}"));
        await Task.Run(() => tailService.RaiseFileRotated(tab.FilePath));

        await Task.Delay(100);
        Assert.Equal(3, tab.TotalLines);

        releaseLease.TrySetResult(true);
        await readLeaseTask.WaitAsync(TimeSpan.FromSeconds(5));

        await WaitForAsync(() => tab.TotalLines == 5 && tab.VisibleLines.LastOrDefault()?.LineNumber == 5);
        Assert.Equal("Rotated 5", tab.VisibleLines.Last().Text);
    }

    [Fact]
    public async Task Dispose_WaitsForOutstandingReadLeaseBeforeCleanupCompletes()
    {
        var reader = new MutableLogReaderService(Enumerable.Range(1, 3).Select(i => $"Line {i}"));
        var tab = CreateTab(reader);
        await tab.LoadAsync();

        var session = tab.ActiveSession;
        var leaseEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readLeaseTask = Task.Run(() => tab.WithLineIndexLeaseAsync(
            async (_, _, ct) =>
            {
                leaseEntered.TrySetResult(true);
                await releaseLease.Task.WaitAsync(ct);
            },
            CancellationToken.None));

        await leaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        tab.Dispose();

        var disposeTask = session.DebugLineIndexDisposeTask;
        Assert.NotNull(disposeTask);
        Assert.False(disposeTask!.IsCompleted);

        releaseLease.TrySetResult(true);
        await readLeaseTask.WaitAsync(TimeSpan.FromSeconds(5));
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(session.DebugLineIndex);
    }

    [Fact]
    public async Task LinesAppended_PublishesViewportAndStatusOnCapturedSynchronizationContext()
    {
        await SingleThreadSynchronizationContext.RunAsync(async () =>
        {
            var originThreadId = Environment.CurrentManagedThreadId;
            var reader = new MutableLogReaderService(Enumerable.Range(1, 60).Select(i => $"Line {i}"));
            var tailService = new StubFileTailService();
            using var tab = CreateTab(reader, tailService: tailService);
            await tab.LoadAsync();

            var propertyThreads = new ConcurrentBag<int>();
            var collectionThreads = new ConcurrentBag<int>();
            tab.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(LogTabViewModel.TotalLines) or nameof(LogTabViewModel.StatusText))
                    propertyThreads.Add(Environment.CurrentManagedThreadId);
            };
            tab.VisibleLines.CollectionChanged += (_, _) => collectionThreads.Add(Environment.CurrentManagedThreadId);

            reader.AppendLine("Line 61");
            await Task.Run(() => tailService.RaiseLinesAppended(tab.FilePath));

            await WaitForAsync(() => tab.TotalLines == 61 && tab.VisibleLines.LastOrDefault()?.LineNumber == 61);

            Assert.NotEmpty(propertyThreads);
            Assert.NotEmpty(collectionThreads);
            Assert.All(propertyThreads, threadId => Assert.Equal(originThreadId, threadId));
            Assert.All(collectionThreads, threadId => Assert.Equal(originThreadId, threadId));
        });
    }

    [Fact]
    public async Task FileRotated_PublishesReloadedViewportOnCapturedSynchronizationContext()
    {
        await SingleThreadSynchronizationContext.RunAsync(async () =>
        {
            var originThreadId = Environment.CurrentManagedThreadId;
            var reader = new MutableLogReaderService(Enumerable.Range(1, 3).Select(i => $"Line {i}"));
            var tailService = new StubFileTailService();
            using var tab = CreateTab(reader, tailService: tailService);
            await tab.LoadAsync();

            var propertyThreads = new ConcurrentBag<int>();
            var collectionThreads = new ConcurrentBag<int>();
            tab.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(LogTabViewModel.TotalLines) or nameof(LogTabViewModel.StatusText))
                    propertyThreads.Add(Environment.CurrentManagedThreadId);
            };
            tab.VisibleLines.CollectionChanged += (_, _) => collectionThreads.Add(Environment.CurrentManagedThreadId);

            reader.ReplaceLines(Enumerable.Range(1, 5).Select(i => $"Rotated {i}"));
            await Task.Run(() => tailService.RaiseFileRotated(tab.FilePath));

            await WaitForAsync(() => tab.TotalLines == 5 && tab.VisibleLines.LastOrDefault()?.Text == "Rotated 5");

            Assert.NotEmpty(propertyThreads);
            Assert.NotEmpty(collectionThreads);
            Assert.All(propertyThreads, threadId => Assert.Equal(originThreadId, threadId));
            Assert.All(collectionThreads, threadId => Assert.Equal(originThreadId, threadId));
        });
    }

    [Fact]
    public async Task TailError_PublishesSuspendedStateOnCapturedSynchronizationContext()
    {
        await SingleThreadSynchronizationContext.RunAsync(async () =>
        {
            var originThreadId = Environment.CurrentManagedThreadId;
            var tailService = new StubFileTailService();
            using var tab = CreateTab(new MutableLogReaderService(Enumerable.Range(1, 3).Select(i => $"Line {i}")), tailService: tailService);
            await tab.LoadAsync();

            var propertyThreads = new ConcurrentBag<int>();
            tab.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(LogTabViewModel.IsSuspended) or nameof(LogTabViewModel.StatusText))
                    propertyThreads.Add(Environment.CurrentManagedThreadId);
            };

            await Task.Run(() => tailService.RaiseTailError(tab.FilePath, "worker failure"));

            await WaitForAsync(() => tab.IsSuspended && tab.StatusText == "Tailing stopped: worker failure");

            Assert.NotEmpty(propertyThreads);
            Assert.All(propertyThreads, threadId => Assert.Equal(originThreadId, threadId));
        });
    }

    [Fact]
    public async Task LinesAppended_WithActiveFilter_PublishesFilteredViewportAndStatusOnCapturedSynchronizationContext()
    {
        await SingleThreadSynchronizationContext.RunAsync(async () =>
        {
            var originThreadId = Environment.CurrentManagedThreadId;
            var reader = new MutableLogReaderService(new[]
            {
                "INFO first",
                "ERROR second"
            });
            var tailService = new StubFileTailService();
            using var tab = CreateTab(reader, tailService: tailService);
            await tab.LoadAsync();
            await tab.ApplyFilterAsync(
                matchingLineNumbers: new[] { 2 },
                statusText: "Filter active: 1 matching lines.",
                filterRequest: new SearchRequest
                {
                    Query = "ERROR",
                    FilePaths = new List<string> { tab.FilePath },
                    SourceMode = SearchRequestSourceMode.SnapshotAndTail
                });

            var propertyThreads = new ConcurrentBag<int>();
            var collectionThreads = new ConcurrentBag<int>();
            tab.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(LogTabViewModel.StatusText) or nameof(LogTabViewModel.FilteredLineCount))
                    propertyThreads.Add(Environment.CurrentManagedThreadId);
            };
            tab.VisibleLines.CollectionChanged += (_, _) => collectionThreads.Add(Environment.CurrentManagedThreadId);

            reader.AppendLine("ERROR third");
            await Task.Run(() => tailService.RaiseLinesAppended(tab.FilePath));

            await WaitForAsync(() =>
                tab.FilteredLineCount == 2 &&
                tab.VisibleLines.LastOrDefault()?.LineNumber == 3 &&
                tab.StatusText == "Filter active (tailing): 2 matching lines.");

            Assert.NotEmpty(propertyThreads);
            Assert.NotEmpty(collectionThreads);
            Assert.All(propertyThreads, threadId => Assert.Equal(originThreadId, threadId));
            Assert.All(collectionThreads, threadId => Assert.Equal(originThreadId, threadId));
        });
    }

    [Fact]
    public async Task SwitchingLoadedTabBackToAuto_DoesNotRunDetectionSynchronouslyOnCallerThread()
    {
        var detectionService = new BlockingEncodingDetectionService();
        using var tab = new LogTabViewModel(
            "tab-auto",
            @"C:\test\encoding.log",
            new MutableLogReaderService(Enumerable.Range(1, 3).Select(i => $"Line {i}")),
            new StubFileTailService(),
            detectionService,
            new AppSettings(),
            skipInitialEncodingResolution: true,
            sessionRegistry: null,
            initialEncoding: FileEncoding.Utf16,
            scopeDashboardId: null);

        await tab.LoadAsync();

        detectionService.BlockAutoResolution();

        var reloadCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LogTabViewModel.IsLoading) && !tab.IsLoading)
                reloadCompleted.TrySetResult(true);
        };

        var setterThreadId = Environment.CurrentManagedThreadId;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        tab.Encoding = FileEncoding.Auto;
        stopwatch.Stop();

        Assert.InRange(stopwatch.ElapsedMilliseconds, 0, 250);
        Assert.Equal("Auto (UTF-8)", tab.SelectedEncodingDisplayLabel);
        Assert.Equal("Auto -> UTF-8 (fallback)", tab.EncodingStatusText);

        await detectionService.AutoResolveStarted.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(setterThreadId, detectionService.ResolveThreadIds);

        detectionService.ReleaseAutoResolution();
        await reloadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(FileEncoding.Auto, tab.Encoding);
        Assert.Equal(FileEncoding.Utf8, tab.EffectiveEncoding);
    }

    [Fact]
    public async Task FilterSession_ViewportSnapshotCache_ReusesUntilFilterMutates()
    {
        var filterSession = new LogFilterSession();
        filterSession.ApplyFilter(
            matchingLineNumbers: new[] { 2 },
            statusText: "Filter active: 1 matching lines.",
            filterRequest: new SearchRequest
            {
                Query = "ERROR",
                FilePaths = new List<string> { @"C:\test\file.log" },
                SourceMode = SearchRequestSourceMode.SnapshotAndTail
            },
            hasParseableTimestamps: false,
            totalLines: 2);

        var snapshot1 = filterSession.ViewportFilteredLineNumbersSnapshot;
        var snapshot2 = filterSession.ViewportFilteredLineNumbersSnapshot;

        Assert.NotNull(snapshot1);
        Assert.Same(snapshot1, snapshot2);

        var updated = await filterSession.ProcessAppendedLinesAsync(
            updatedLineCount: 3,
            lineIndex: new LineIndex { FilePath = @"C:\test\file.log", FileSize = 300 },
            effectiveEncoding: FileEncoding.Utf8,
            readLinesAsync: (_, _, _, _, _) => Task.FromResult<IReadOnlyList<string>>(new[] { "ERROR third" }),
            ct: CancellationToken.None);

        Assert.True(updated.HasChanges);

        var snapshot3 = filterSession.ViewportFilteredLineNumbersSnapshot;
        Assert.NotNull(snapshot3);
        Assert.NotSame(snapshot1, snapshot3);
        Assert.Equal(new[] { 2, 3 }, snapshot3);
    }

    private static LogTabViewModel CreateTab(
        ILogReaderService logReader,
        IFileTailService? tailService = null,
        IEncodingDetectionService? encodingDetectionService = null)
        => new(
            "test-id",
            @"C:\test\file.log",
            logReader,
            tailService ?? new StubFileTailService(),
            encodingDetectionService ?? new FileEncodingDetectionService(),
            new AppSettings());

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var timeoutAt = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeoutAt)
                throw new TimeoutException("Condition was not met within the allotted time.");

            await Task.Delay(25);
        }
    }

    private sealed class MutableLogReaderService : ILogReaderService
    {
        private readonly object _gate = new();
        private List<string> _lines;

        public MutableLogReaderService(IEnumerable<string> initialLines)
        {
            _lines = initialLines.ToList();
        }

        public void AppendLine(string line)
        {
            lock (_gate)
                _lines.Add(line);
        }

        public void ReplaceLines(IEnumerable<string> lines)
        {
            lock (_gate)
                _lines = lines.ToList();
        }

        public Task<LineIndex> BuildIndexAsync(string filePath, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(CreateIndex(filePath));

        public Task<LineIndex> UpdateIndexAsync(string filePath, LineIndex existingIndex, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(CreateIndex(filePath));

        public Task<IReadOnlyList<string>> ReadLinesAsync(
            string filePath,
            LineIndex index,
            int startLine,
            int count,
            FileEncoding encoding,
            CancellationToken ct = default)
        {
            List<string> snapshot;
            lock (_gate)
                snapshot = _lines.ToList();

            var boundedStart = Math.Max(0, startLine);
            var boundedCount = Math.Max(0, Math.Min(count, snapshot.Count - boundedStart));
            return Task.FromResult<IReadOnlyList<string>>(snapshot.Skip(boundedStart).Take(boundedCount).ToList());
        }

        public Task<string> ReadLineAsync(string filePath, LineIndex index, int lineNumber, FileEncoding encoding, CancellationToken ct = default)
        {
            List<string> snapshot;
            lock (_gate)
                snapshot = _lines.ToList();

            if (lineNumber < 0 || lineNumber >= snapshot.Count)
                return Task.FromResult(string.Empty);

            return Task.FromResult(snapshot[lineNumber]);
        }

        private LineIndex CreateIndex(string filePath)
        {
            List<string> snapshot;
            lock (_gate)
                snapshot = _lines.ToList();

            var index = new LineIndex
            {
                FilePath = filePath,
                FileSize = snapshot.Count * 100
            };

            for (var i = 0; i < snapshot.Count; i++)
                index.LineOffsets.Add(i * 100L);

            return index;
        }
    }

    private sealed class BlockingEncodingDetectionService : IEncodingDetectionService
    {
        private readonly TaskCompletionSource<bool> _autoResolveStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool> _releaseAutoResolution = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _blockAutoResolution;

        public Task AutoResolveStarted => _autoResolveStarted.Task;

        public ConcurrentBag<int> ResolveThreadIds { get; } = new();

        public void BlockAutoResolution() => _blockAutoResolution = true;

        public void ReleaseAutoResolution() => _releaseAutoResolution.TrySetResult(true);

        public FileEncoding DetectFileEncoding(string filePath, FileEncoding fallback = FileEncoding.Utf8)
            => FileEncoding.Utf8;

        public EncodingHelper.EncodingDecision ResolveEncodingDecision(string filePath, FileEncoding selectedEncoding)
        {
            ResolveThreadIds.Add(Environment.CurrentManagedThreadId);

            if (selectedEncoding != FileEncoding.Auto)
                return EncodingHelper.ResolveManualEncodingDecision(selectedEncoding);

            if (_blockAutoResolution)
            {
                _autoResolveStarted.TrySetResult(true);
                _releaseAutoResolution.Task.GetAwaiter().GetResult();
            }

            return new EncodingHelper.EncodingDecision(
                FileEncoding.Auto,
                FileEncoding.Utf8,
                "Auto -> UTF-8");
        }
    }

    private sealed class SingleThreadSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread _thread;
        private Func<Task>? _asyncAction;

        private SingleThreadSynchronizationContext()
        {
            _thread = new Thread(RunOnCurrentThread)
            {
                IsBackground = true,
                Name = nameof(SingleThreadSynchronizationContext)
            };
            _thread.SetApartmentState(ApartmentState.STA);
        }

        public static async Task RunAsync(Func<Task> asyncAction)
        {
            using var context = new SingleThreadSynchronizationContext
            {
                _asyncAction = asyncAction
            };
            context._thread.Start();
            await context._completion.Task;
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            _queue.Add((d, state));
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (Thread.CurrentThread == _thread)
            {
                d(state);
                return;
            }

            using var signal = new ManualResetEventSlim();
            Exception? exception = null;
            Post(_ =>
            {
                try
                {
                    d(state);
                }
                catch (Exception ex)
                {
                    exception = ex;
                }
                finally
                {
                    signal.Set();
                }
            }, null);

            signal.Wait();
            if (exception != null)
                ExceptionDispatchInfo.Capture(exception).Throw();
        }

        public void Dispose()
        {
            CompleteQueue();
            if (_thread.IsAlive)
                _thread.Join(TimeSpan.FromSeconds(5));
        }

        private void RunOnCurrentThread()
        {
            var previousContext = Current;
            SetSynchronizationContext(this);
            try
            {
                Task asyncTask;
                try
                {
                    asyncTask = _asyncAction!();
                }
                catch (Exception ex)
                {
                    _completion.TrySetException(ex);
                    return;
                }

                asyncTask.ContinueWith(
                    static (task, state) =>
                    {
                        var context = (SingleThreadSynchronizationContext)state!;
                        if (task.IsFaulted)
                            context._completion.TrySetException(task.Exception!.InnerExceptions);
                        else if (task.IsCanceled)
                            context._completion.TrySetCanceled();
                        else
                            context._completion.TrySetResult();

                        context.CompleteQueue();
                    },
                    this,
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);

                foreach (var workItem in _queue.GetConsumingEnumerable())
                    workItem.Callback(workItem.State);
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
                CompleteQueue();
            }
            finally
            {
                SetSynchronizationContext(previousContext);
            }
        }

        private void CompleteQueue()
        {
            if (!_queue.IsAddingCompleted)
                _queue.CompleteAdding();
        }
    }
}
