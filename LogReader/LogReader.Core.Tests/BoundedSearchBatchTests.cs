namespace LogReader.Core.Tests;

using System.Collections.Concurrent;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

public class BoundedSearchBatchTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    public enum BatchApi { Dictionary, Encoding, Partial }

    public static IEnumerable<object[]> Cases =>
        from concrete in new[] { false, true }
        from api in Enum.GetValues<BatchApi>()
        select new object[] { concrete, api };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EmptyCanceledBatch_DoesNotInvokeCallbacks(bool concrete, BatchApi api)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = CreateService(concrete, (_, _, _, _) => throw new InvalidOperationException("search"));
        var result = await RunAsync(service, api, Request(), 2,
            (_, _) => throw new InvalidOperationException("admission"),
            _ => throw new InvalidOperationException("encoding"), ct: cancellation.Token);

        Assert.Empty(result.Results);
        Assert.False(result.WasCancelled);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task InvalidArguments_PreserveParameterNames(bool concrete, BatchApi api)
    {
        var service = CreateService(concrete, (_, _, _, _) => throw new InvalidOperationException());
        var request = Request("one");
        Assert.Equal("request", (await Assert.ThrowsAsync<ArgumentNullException>(() =>
            RunAsync(service, api, null!, 1, Admit))).ParamName);
        Assert.Equal("acquireOperationAsync", (await Assert.ThrowsAsync<ArgumentNullException>(() =>
            RunAsync(service, api, request, 1, null!))).ParamName);
        if (api == BatchApi.Dictionary)
        {
            Assert.Equal("fileEncodings", (await Assert.ThrowsAsync<ArgumentNullException>(() =>
                service.SearchFilesBoundedAsync(request, null!, 1, Admit))).ParamName);
        }
        else
        {
            Assert.Equal("resolveEncoding", (await Assert.ThrowsAsync<ArgumentNullException>(() =>
                RunAsync(service, api, request, 1, Admit, null!))).ParamName);
        }
        foreach (var limit in new[] { 0, -1 })
        {
            Assert.Equal("maximumConcurrency", (await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                RunAsync(service, api, request, limit, Admit))).ParamName);
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task WorkersAndLeases_RespectConcurrencyAndDrain(bool concrete, BatchApi api)
    {
        foreach (var maximumConcurrency in new[] { 1, 2, 99 })
        {
            var ready = Signal();
            var release = Signal();
            var started = 0;
            var activeLeases = 0;
            var disposedLeases = 0;
            var expectedWorkers = Math.Min(maximumConcurrency, 4);
            var service = CreateService(concrete, async (path, _, _, _) =>
            {
                if (Interlocked.Increment(ref started) == expectedWorkers)
                    ready.TrySetResult();
                await release.Task;
                return new SearchResult { FilePath = path };
            });
            var task = RunAsync(service, api, Request("one", "two", "three", "four"), maximumConcurrency,
                (_, _) =>
                {
                    Interlocked.Increment(ref activeLeases);
                    return new ValueTask<IDisposable>(new Lease(() =>
                    {
                        Interlocked.Decrement(ref activeLeases);
                        Interlocked.Increment(ref disposedLeases);
                    }));
                });
            try
            {
                await ready.Task.WaitAsync(WaitLimit);
                Assert.Equal(expectedWorkers, Volatile.Read(ref started));
                Assert.Equal(expectedWorkers, Volatile.Read(ref activeLeases));
                Assert.False(task.IsCompleted);
            }
            finally
            {
                release.TrySetResult();
                await task.WaitAsync(WaitLimit);
            }
            Assert.Equal(4, Volatile.Read(ref started));
            Assert.Equal(4, Volatile.Read(ref disposedLeases));
            Assert.Equal(0, Volatile.Read(ref activeLeases));
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ReverseCompletion_PreservesInputSlots(bool concrete, BatchApi api)
    {
        var paths = new[] { "one", "two", "three" };
        var releases = paths.ToDictionary(path => path, _ => Signal());
        var completed = paths.ToDictionary(path => path, _ => Signal());
        var ready = Signal();
        var started = 0;
        var service = CreateService(concrete, async (path, _, _, _) =>
        {
            if (Interlocked.Increment(ref started) == paths.Length)
                ready.TrySetResult();
            await releases[path].Task;
            completed[path].TrySetResult();
            return new SearchResult { FilePath = path };
        });
        var task = RunAsync(service, api, Request(paths), 3, Admit);
        try
        {
            await ready.Task.WaitAsync(WaitLimit);
            foreach (var path in paths.Reverse())
            {
                releases[path].TrySetResult();
                await completed[path].Task.WaitAsync(WaitLimit);
            }
            var result = await task.WaitAsync(WaitLimit);
            Assert.Equal(paths, result.Results.Select(result => result!.FilePath));
        }
        finally
        {
            foreach (var release in releases.Values)
                release.TrySetResult();
            await task.WaitAsync(WaitLimit);
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task SchedulingAndDuplicatePaths_PreserveExistingWorkOrder(bool concrete, BatchApi api)
    {
        var paths = new[] { @"\\host-a\share\one", @"\\host-a\share\two", @"\\host-b\share\three", @"C:\logs\four" };
        var started = new List<string>();
        var service = CreateService(concrete, (path, _, _, _) =>
        {
            started.Add(path);
            return Task.FromResult(new SearchResult { FilePath = path });
        });
        var result = await RunAsync(service, api, Request(paths), 1, Admit);
        var interleaved = api == BatchApi.Encoding || api == BatchApi.Partial && concrete;
        Assert.Equal(interleaved ? new[] { paths[0], paths[2], paths[3], paths[1] } : paths, started);
        Assert.Equal(paths, result.Results.Select(result => result!.FilePath));

        started.Clear();
        result = await RunAsync(service, api, Request("duplicate", "duplicate"), 1, Admit);
        Assert.Equal(new[] { "duplicate", "duplicate" }, started);
        Assert.Equal(2, result.Results.Count);
        Assert.NotSame(result.Results[0], result.Results[1]);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EncodingResolutionAndAnnotation_OccurInsideLease(bool concrete, BatchApi api)
    {
        var leasedPaths = new ConcurrentDictionary<string, byte>();
        var resolved = 0;
        var service = CreateService(concrete, (path, _, encoding, _) =>
        {
            Assert.True(leasedPaths.ContainsKey(path));
            Assert.Equal(api == BatchApi.Dictionary
                ? path == "configured" ? FileEncoding.Utf16 : FileEncoding.Utf8
                : FileEncoding.Utf16Be, encoding);
            return Task.FromResult(new SearchResult { FilePath = path, ResolvedEncoding = FileEncoding.Ansi });
        });
        var result = await RunAsync(service, api, Request("configured", "fallback"), 2,
            (path, _) =>
            {
                Assert.True(leasedPaths.TryAdd(path, 0));
                return new ValueTask<IDisposable>(new Lease(() => Assert.True(leasedPaths.TryRemove(path, out var removed))));
            }, path =>
            {
                Assert.True(leasedPaths.ContainsKey(path));
                Interlocked.Increment(ref resolved);
                return FileEncoding.Utf16Be;
            }, new Dictionary<string, FileEncoding> { ["configured"] = FileEncoding.Utf16 });

        Assert.Empty(leasedPaths);
        Assert.Equal(api == BatchApi.Dictionary ? 0 : 2, resolved);
        Assert.All(result.Results, result => Assert.Equal(
            api == BatchApi.Dictionary ? FileEncoding.Ansi : FileEncoding.Utf16Be, result!.ResolvedEncoding));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task CancellationBeforeAdmission_PreservesReturnContract(bool concrete, BatchApi api)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var admissions = 0;
        var service = CreateService(concrete, (_, _, _, _) => throw new InvalidOperationException("search"));
        var task = RunAsync(service, api, Request("one", "two"), 1, (_, ct) =>
        {
            Interlocked.Increment(ref admissions);
            ct.ThrowIfCancellationRequested();
            return Admit("", ct);
        }, ct: cancellation.Token);
        if (api == BatchApi.Partial)
        {
            var result = await task;
            Assert.True(result.WasCancelled);
            Assert.All(result.Results, result => Assert.Null(result));
            Assert.Equal(0, admissions);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.Equal(1, admissions);
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task CallbackHandledCancellation_RetainsReturnedResult(bool concrete, BatchApi api)
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var service = CreateService(concrete, (path, _, _, _) =>
        {
            Interlocked.Increment(ref calls);
            cancellation.Cancel();
            return Task.FromResult(new SearchResult { FilePath = path, WasCancelled = true });
        });
        var result = await RunAsync(service, api, Request("one", "two"), 1, Admit, ct: cancellation.Token);
        Assert.Equal(api == BatchApi.Partial ? 1 : 2, calls);
        Assert.Equal(api == BatchApi.Partial, result.WasCancelled);
        Assert.True(result.Results[0]!.WasCancelled);
        if (api == BatchApi.Partial)
            Assert.Null(result.Results[1]);
        else
            Assert.NotNull(result.Results[1]);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ReturnedCancellationFlag_DoesNotStopOtherWork(bool concrete, BatchApi api)
    {
        var service = CreateService(concrete, (path, _, _, _) => Task.FromResult(
            new SearchResult { FilePath = path, WasCancelled = true }));
        var result = await RunAsync(service, api, Request("one", "two"), 1, Admit);
        Assert.All(result.Results, result => Assert.NotNull(result));
        Assert.Equal(api == BatchApi.Partial, result.WasCancelled);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task CancellationDuringAdmissionOrSearch_ReleasesLeasesAndRetainsSlots(bool concrete, BatchApi api)
    {
        foreach (var duringAdmission in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            var entered = Signal();
            var disposed = 0;
            var service = CreateService(concrete, async (path, _, _, ct) =>
            {
                if (path == "two" && !duringAdmission)
                {
                    entered.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                }
                return new SearchResult { FilePath = path };
            });
            var task = RunAsync(service, api, Request("one", "two", "three"), 1, async (path, ct) =>
            {
                if (path == "two" && duringAdmission)
                {
                    entered.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                }
                return new Lease(() => Interlocked.Increment(ref disposed));
            }, ct: cancellation.Token);
            try
            {
                await entered.Task.WaitAsync(WaitLimit);
            }
            finally
            {
                cancellation.Cancel();
                if (api == BatchApi.Partial)
                {
                    var result = await task.WaitAsync(WaitLimit);
                    Assert.True(result.WasCancelled);
                    Assert.Equal("one", result.Results[0]!.FilePath);
                    Assert.Null(result.Results[1]);
                    Assert.Null(result.Results[2]);
                }
                else
                {
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(WaitLimit));
                }
            }
            Assert.Equal(duringAdmission ? 1 : 2, disposed);
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Failures_PropagateAfterOtherWorkersDrain(bool concrete, BatchApi api)
    {
        foreach (var failureStage in new[] { "admission", "search", "foreignCancellation", "encoding" })
        {
            if (failureStage == "encoding" && api == BatchApi.Dictionary)
                continue;
            var ready = Signal();
            var release = Signal();
            var failed = Signal();
            var disposed = 0;
            Exception expected = failureStage == "foreignCancellation"
                ? new OperationCanceledException("Unrelated cancellation")
                : new InvalidOperationException(failureStage);
            var service = CreateService(concrete, async (path, _, _, _) =>
            {
                if (path == "one")
                {
                    ready.TrySetResult();
                    await release.Task;
                }
                else if (failureStage is "search" or "foreignCancellation")
                {
                    failed.TrySetResult();
                    throw expected;
                }
                return new SearchResult { FilePath = path };
            });
            var task = RunAsync(service, api, Request("one", "two"), 2, (path, _) =>
            {
                if (path == "two" && failureStage == "admission")
                {
                    failed.TrySetResult();
                    throw expected;
                }
                return new ValueTask<IDisposable>(new Lease(() => Interlocked.Increment(ref disposed)));
            }, path =>
            {
                if (path == "two" && failureStage == "encoding")
                {
                    failed.TrySetResult();
                    throw expected;
                }
                return FileEncoding.Utf8;
            });
            try
            {
                await ready.Task.WaitAsync(WaitLimit);
                await failed.Task.WaitAsync(WaitLimit);
                Assert.False(task.IsCompleted);
            }
            finally
            {
                release.TrySetResult();
                var actual = await Record.ExceptionAsync(() => task.WaitAsync(WaitLimit));
                Assert.Same(expected, actual);
            }
            Assert.Equal(failureStage == "admission" ? 1 : 2, disposed);
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task SynchronousSearchFailure_ReleasesLease(bool concrete, BatchApi api)
    {
        var expected = new InvalidOperationException("Synchronous search failure");
        var disposed = 0;
        var service = CreateService(concrete, (_, _, _, _) => throw expected);
        var actual = await Record.ExceptionAsync(() => RunAsync(service, api, Request("one"), 1,
            (_, _) => new ValueTask<IDisposable>(new Lease(() => Interlocked.Increment(ref disposed)))));
        Assert.Same(expected, actual);
        Assert.Equal(1, disposed);
    }

    [Theory]
    [InlineData(BatchApi.Dictionary)]
    [InlineData(BatchApi.Encoding)]
    [InlineData(BatchApi.Partial)]
    public async Task ConcreteBatch_PreparesOneRegexAndSkipsEmptyAndTimeOnlyMatchers(BatchApi api)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WeezTailBatchTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var paths = new[] { Path.Combine(directory, "one.log"), Path.Combine(directory, "two.log") };
            foreach (var path in paths)
                await File.WriteAllTextAsync(path, "12:30:00 needle\n12:31:00 other\n");
            var prepared = 0;
            ISearchService service = new SearchService((pattern, caseSensitive) =>
            {
                Interlocked.Increment(ref prepared);
                return RegexPatternFactory.Create(pattern, caseSensitive);
            });
            await RunAsync(service, api, new SearchRequest { Query = "needle", IsRegex = true }, 2, Admit);
            Assert.Equal(0, prepared);
            var request = Request(paths);
            request.IsRegex = true;
            var result = await RunAsync(service, api, request, 2, Admit);
            Assert.Equal(1, prepared);
            Assert.All(result.Results, result =>
            {
                Assert.Null(result!.Error);
                Assert.Single(result.Hits);
            });
            request.Query = "";
            request.Usage = SearchRequestUsage.FilterApply;
            request.FromTimestamp = "12:00:00";
            request.ToTimestamp = "13:00:00";
            result = await RunAsync(service, api, request, 2, Admit);
            Assert.Equal(1, prepared);
            Assert.All(result.Results, result =>
            {
                Assert.Null(result!.Error);
                Assert.Equal(2, result.Hits.Count);
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static SearchRequest Request(params string[] paths) => new() { Query = "needle", FilePaths = paths.ToList() };

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static ValueTask<IDisposable> Admit(string path, CancellationToken ct) => new(new Lease(() => { }));

    private static ISearchService CreateService(bool concrete,
        Func<string, SearchRequest, FileEncoding, CancellationToken, Task<SearchResult>> search)
        => concrete ? new SearchService(search) : new DefaultSearchService(search);

    private static Task<BoundedSearchBatchResult> RunAsync(ISearchService service, BatchApi api,
        SearchRequest request, int concurrency, Func<string, CancellationToken, ValueTask<IDisposable>> admission,
        CancellationToken ct = default)
        => RunAsync(service, api, request, concurrency, admission, _ => FileEncoding.Utf8, ct: ct);

    private static async Task<BoundedSearchBatchResult> RunAsync(ISearchService service, BatchApi api,
        SearchRequest request, int concurrency, Func<string, CancellationToken, ValueTask<IDisposable>> admission,
        Func<string, FileEncoding> resolveEncoding, IDictionary<string, FileEncoding>? encodings = null,
        CancellationToken ct = default)
    {
        if (api == BatchApi.Partial)
            return await service.SearchFilesBoundedWithEncodingPartialAsync(request, concurrency, resolveEncoding, admission, ct);
        var results = api == BatchApi.Dictionary
            ? await service.SearchFilesBoundedAsync(request, encodings ?? new Dictionary<string, FileEncoding>(), concurrency, admission, ct)
            : await service.SearchFilesBoundedWithEncodingAsync(request, concurrency, resolveEncoding, admission, ct);
        return new BoundedSearchBatchResult(results, false);
    }

    private sealed class Lease(Action dispose) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            Assert.Equal(0, Interlocked.Exchange(ref _disposed, 1));
            dispose();
        }
    }

    private sealed class DefaultSearchService(
        Func<string, SearchRequest, FileEncoding, CancellationToken, Task<SearchResult>> search) : ISearchService
    {
        public Task<SearchResult> SearchFileAsync(string path, SearchRequest request, FileEncoding encoding, CancellationToken ct = default)
            => search(path, request, encoding, ct);

        public Task<SearchResult> SearchFileRangeAsync(string path, SearchRequest request, FileEncoding encoding,
            Func<int, int, FileEncoding, CancellationToken, Task<IReadOnlyList<string>>> readLinesAsync, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SearchResult>> SearchFilesAsync(SearchRequest request,
            IDictionary<string, FileEncoding> encodings, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
