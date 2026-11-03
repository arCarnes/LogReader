namespace LogReader.Core.Tests;

using System.Text.RegularExpressions;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

public sealed partial class HeadlessLogQueryBackendTests
{
    [Theory]
    [InlineData("search", false, 2)]
    [InlineData("search", true, 2)]
    [InlineData("count", false, 2)]
    [InlineData("count", true, 2)]
    [InlineData("monitor-initialize", false, 2)]
    [InlineData("monitor-initialize", true, 2)]
    [InlineData("monitor-poll", false, 2)]
    [InlineData("monitor-poll", true, 2)]
    [InlineData("monitor-initialize", false, 1)]
    [InlineData("monitor-initialize", true, 1)]
    [InlineData("monitor-poll", false, 1)]
    [InlineData("monitor-poll", true, 1)]
    public async Task ScannerLifetime_IncludesBlockedDisposalAndDrainsBeforeResponse(string operation, bool cancel, int globalLimit)
    {
        var paths = new List<string>();
        for (var i = 0; i < 3; i++) paths.Add(await CreateFileAsync($"full-lifetime-{i}.log", "needle\n"));
        var scanner = new FullLifetimeScanner(paths[0]);
        using var backend = CreateBackend(CreateSnapshot(paths.Select((path, i) => ($"file-{i}", path)).ToArray()), scanner: scanner,
            limits: LogQueryEffectiveLimits.Default with { MaximumConcurrentDiskOperations = globalLimit });
        using var cancellation = new CancellationTokenSource();
        string? monitorCursor = null;
        if (operation == "monitor-poll")
        {
            var initial = await backend.MonitorLogsAsync(MonitorStart("needle"));
            Assert.Empty(initial.Errors);
            monitorCursor = initial.Result!.NextCursor;
            Assert.NotNull(monitorCursor);
            Assert.Equal(0, scanner.Live);
            foreach (var path in paths) await File.AppendAllTextAsync(path, "needle appended\n");
        }
        scanner.Arm();
        var bound = operation is "search" or "count" ? 2 : 1;
        Func<Task>? assertResponse = null;
        var responseTask = RunAsync();
        try
        {
            await scanner.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (bound == 2) await scanner.SecondOpened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Source inspection: each real scanner owns one 256 KiB rented buffer until its
            // DisposeAsync returns. Count opens BEFORE calling the real OpenAsync, and retain
            // the count through blocked disposal; this overbounds the complete buffer lifetime.
            Assert.InRange(scanner.Live, 1, bound);
            Assert.Equal(bound, scanner.Peak);
            Assert.Equal(bound, scanner.OpenedSinceArm);
            Assert.False(responseTask.IsCompleted);
            if (cancel)
            {
                cancellation.Cancel();
                Assert.False(responseTask.IsCompleted); // Cancellation cannot return a live buffer early.
            }
        }
        finally
        {
            scanner.Release.TrySetResult();
            await responseTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await assertResponse!();
        Assert.Equal(0, scanner.Live);
        Assert.Equal(scanner.OpenedSinceArm, scanner.DisposedSinceArm);
        Assert.InRange(scanner.Peak, 1, bound);

        async Task RunAsync()
        {
            if (operation == "search")
            {
                var response = await backend.SearchLogsAsync(Search("dashboard", "needle"), cancellation.Token);
                assertResponse = () =>
                {
                    if (cancel) Assert.Equal("request_cancelled", Assert.Single(response.Errors).Code);
                    else
                    {
                        Assert.Empty(response.Errors);
                        Assert.False(response.IsPartial);
                        Assert.True(response.Result!.IsTraversalComplete);
                        Assert.Equal(3, response.Result.MatchingLineCount);
                    }
                    return Task.CompletedTask;
                };
            }
            else if (operation == "count")
            {
                var response = await backend.CountLogsAsync(new()
                { Targets = [new(ConfiguredLogTargetKind.Dashboard, "dashboard")], Query = "needle" }, cancellation.Token);
                assertResponse = () =>
                {
                    if (cancel) Assert.Equal("request_cancelled", Assert.Single(response.Errors).Code);
                    else
                    {
                        Assert.Empty(response.Errors);
                        Assert.False(response.IsPartial);
                        Assert.True(response.Result!.IsComplete);
                        Assert.Equal(3, response.Result.MatchingLineCount);
                    }
                    return Task.CompletedTask;
                };
            }
            else
            {
                var response = await backend.MonitorLogsAsync(monitorCursor == null ? MonitorStart("needle") :
                    new() { Cursor = monitorCursor }, cancellation.Token);
                assertResponse = async () =>
                {
                    if (cancel) Assert.Equal("request_cancelled", Assert.Single(response.Errors).Code);
                    else
                    {
                        Assert.Empty(response.Errors);
                        Assert.False(response.IsPartial);
                        Assert.Equal(3, response.Result!.Files.Length);
                        Assert.Equal(operation == "monitor-poll" ? 3 : 0, response.Result.Files.Sum(file => file.Events.Length));
                        var released = await backend.MonitorLogsAsync(new() { Cursor = response.Result.NextCursor, Release = true });
                        Assert.Equal("released", released.Result!.State);
                    }
                };
            }
        }
    }

    private sealed class FullLifetimeScanner(string gatedPath) : IResumableLogScanner
    {
        private readonly string _gatedPath = gatedPath;
        private readonly IResumableLogScanner _inner = new ResumableLogScanner(new FileEncodingDetectionService());
        private int _armed, _gateUsed, _live, _peak, _opened, _disposed;
        public int Live => Volatile.Read(ref _live);
        public int Peak => Volatile.Read(ref _peak);
        public int OpenedSinceArm => Volatile.Read(ref _opened);
        public int DisposedSinceArm => Volatile.Read(ref _disposed);
        public TaskCompletionSource SecondOpened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm()
        {
            Assert.Equal(0, Live);
            Interlocked.Exchange(ref _peak, 0);
            Interlocked.Exchange(ref _opened, 0);
            Interlocked.Exchange(ref _disposed, 0);
            Volatile.Write(ref _armed, 1);
        }

        public Regex? PrepareRegex(SearchRequest request) => _inner.PrepareRegex(request);

        public async Task<IResumableLogScan> OpenAsync(string path, ResumableFileCheckpoint checkpoint, CancellationToken ct)
        {
            var current = Interlocked.Increment(ref _live);
            int observed;
            do { observed = Volatile.Read(ref _peak); }
            while (current > observed && Interlocked.CompareExchange(ref _peak, current, observed) != observed);
            var armed = Volatile.Read(ref _armed) != 0;
            if (armed && Interlocked.Increment(ref _opened) == 2) SecondOpened.TrySetResult();
            try { return new Scan(this, path, await _inner.OpenAsync(path, checkpoint, ct), armed); }
            catch
            {
                Interlocked.Decrement(ref _live);
                if (armed) Interlocked.Increment(ref _disposed);
                throw;
            }
        }

        private sealed class Scan(FullLifetimeScanner owner, string path, IResumableLogScan inner, bool armed) : IResumableLogScan
        {
            private int _disposed;
            public Task<EvaluatedScanLine?> ReadNextAsync(SearchRequest request, Regex? regex, ScanWorkBudget budget,
                int maximumLineBytes, CancellationToken ct) => inner.ReadNextAsync(request, regex, budget, maximumLineBytes, ct);
            public void ValidateAfterRead() => inner.ValidateAfterRead();
            public async ValueTask DisposeAsync()
            {
                Assert.Equal(0, Interlocked.Exchange(ref _disposed, 1));
                // Hold the configured front slot, rather than whichever worker closes first.
                if (armed && StringComparer.OrdinalIgnoreCase.Equals(path, owner._gatedPath) &&
                    Interlocked.CompareExchange(ref owner._gateUsed, 1, 0) == 0)
                {
                    owner.Disposing.TrySetResult();
                    await owner.Release.Task; // Deliberately uninterruptible, retaining the real buffer.
                }
                try { await inner.DisposeAsync(); }
                finally
                {
                    Interlocked.Decrement(ref owner._live);
                    if (armed) Interlocked.Increment(ref owner._disposed);
                }
            }
        }
    }
}
