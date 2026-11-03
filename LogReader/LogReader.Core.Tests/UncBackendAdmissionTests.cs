namespace LogReader.Core.Tests;

using LogReader.Core.Models;

public sealed partial class HeadlessLogQueryBackendTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SearchLogs_CancelledActiveAndQueuedProbesRetainAdmissionAndLifetimeUntilReturn(bool lateFault)
    {
        var gate = new AdmissionWorkGate(1, lateFault);
        var cache = CreateCache();
        var encoding = new CountingEncodingDetectionService(FileEncoding.Utf8);
        var search = new ScriptedLogScanner((path, _, _, _) => Task.FromResult(Result(path, "hit")));
        using var backend = CreateBackend(CreateSnapshot(
            ("one", @"\\host\share\one"), ("two", @"\\HOST\SHARE\two"), ("third", @"\\host\share\third")),
            scanner: search, cache: cache, pathExists: gate.Probe, encodingDetection: encoding,
            limits: LogQueryEffectiveLimits.Default with { MaximumConcurrentDiskOperations = 1 });
        using var cancellation = new CancellationTokenSource();
        var requests = new[] { "one", "two" }.Select(id => Task.Run(() => backend.SearchLogsAsync(
            Search(id, "needle", ConfiguredLogTargetKind.LogFile), cancellation.Token))).ToArray();
        Task<LogOperationEnvelope<LogSearchResult>>? third = null;
        try
        {
            await gate.Started.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            var responses = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(responses, response => Assert.Equal("request_cancelled", Assert.Single(response.Errors).Code));
            Assert.Equal(1, gate.Active);
            third = backend.SearchLogsAsync(Search("third", "needle", ConfiguredLogTargetKind.LogFile, timeoutMilliseconds: 100));
            var blocked = await third.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("deadline_exceeded", Assert.Single(blocked.Errors).Code);
            Assert.Equal(1, gate.Calls);
            Assert.Equal(0, search.CallCount);
            Assert.Equal(0, encoding.AutomaticResolutionCount);
            backend.Dispose();
            using (cache.AcquireSession(Path.Combine(_testDirectory, "still-owned.log"))) { }
        }
        finally
        {
            cancellation.Cancel();
            gate.Release();
            await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));
            if (third != null) await third.WaitAsync(TimeSpan.FromSeconds(5));
            backend.Dispose();
            await AssertCacheDisposedAsync(cache);
        }
        Assert.Equal(0, gate.Active);
        Assert.Equal(1, gate.Peak);
    }

    [Fact]
    public async Task SearchLogs_TwoRequestsShareFourUncSlotsAndRetainTwoHeavyRequests()
    {
        var uncPaths = Enumerable.Range(0, 4).Select(i => $@"\\host\share\file-{i}").ToArray();
        var gate = new AdmissionWorkGate(4);
        var thirdStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const string thirdPath = @"\\other-host\share\third";
        var scanner = new ScriptedLogScanner((path, _, _, ct) =>
        {
            if (path != thirdPath) return gate.ScanAsync(path, ct);
            thirdStarted.TrySetResult();
            return Task.FromResult(Result(path, "third"));
        });
        using var backend = CreateBackend(CreateSnapshot(uncPaths.Select((path, i) => ($"file-{i}", path))
            .Append(("third", thirdPath)).ToArray()), scanner: scanner, pathExists: _ => false,
            encodingDetection: new CountingEncodingDetectionService(FileEncoding.Utf8));
        var query = new LogSearchQuery
        {
            Targets = Enumerable.Range(0, 4).Select(i => new ConfiguredLogTarget(ConfiguredLogTargetKind.LogFile, $"file-{i}")).ToArray(),
            Query = "needle"
        };
        var requests = new[] { backend.SearchLogsAsync(query), backend.SearchLogsAsync(query) };
        Task<LogOperationEnvelope<LogSearchResult>>? third = null;
        try
        {
            await gate.Started.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(4, gate.Active);
            third = backend.SearchLogsAsync(Search("third", "needle", ConfiguredLogTargetKind.LogFile));
            Assert.False(thirdStarted.Task.IsCompleted);
            Assert.Equal(8, LogQueryEffectiveLimits.Default.MaximumConcurrentDiskOperations);
        }
        finally
        {
            gate.Release();
            await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));
            if (third != null) await third.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.All(await Task.WhenAll(requests), response =>
        {
            Assert.Empty(response.Errors);
            Assert.Equal(uncPaths.Length, response.Result!.Files.Length);
            Assert.Equal(2, response.Result.Statistics.PeakConcurrentUncOperations);
        });
        Assert.True(thirdStarted.Task.IsCompletedSuccessfully);
        Assert.Equal(4, gate.Peak);
        Assert.Equal(0, gate.Active);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledDefaultUncProbes_RetainFourHostSlotsUntilOsWorkReturns(bool lateFault)
    {
        const string blockedPath = @"\\host\share\blocked";
        using var entered = new SemaphoreSlim(0);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var cache = CreateCache();
        var scanner = new ScriptedLogScanner((path, _, _, _) => Task.FromResult(Result(path, "hit")));
        using var backend = CreateBackend(CreateSnapshot(("blocked", blockedPath), ("other", @"\\other-host\share\file")),
            scanner: scanner, cache: cache, encodingDetection: new CountingEncodingDetectionService(FileEncoding.Utf8),
            pathExists: path =>
            {
                if (path != blockedPath) return false;
                Interlocked.Increment(ref calls);
                entered.Release();
                release.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                if (lateFault) throw new IOException("Controlled late probe failure.");
                return false;
            });
        using var cleanupCancellation = new CancellationTokenSource();
        var requests = new List<Task<LogOperationEnvelope<LogSearchResult>>>();
        try
        {
            for (var i = 0; i < 4; i++)
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cleanupCancellation.Token);
                var request = Task.Run(() => backend.SearchLogsAsync(Search("blocked", "needle", ConfiguredLogTargetKind.LogFile), cancellation.Token));
                requests.Add(request);
                Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(5)));
                cancellation.Cancel();
                Assert.Equal("request_cancelled", Assert.Single((await request.WaitAsync(TimeSpan.FromSeconds(5))).Errors).Code);
            }
            var queued = backend.SearchLogsAsync(Search("blocked", "needle", ConfiguredLogTargetKind.LogFile, timeoutMilliseconds: 100), cleanupCancellation.Token);
            requests.Add(queued);
            Assert.Equal("deadline_exceeded", Assert.Single((await queued.WaitAsync(TimeSpan.FromSeconds(5))).Errors).Code);
            Assert.Equal(4, Volatile.Read(ref calls));
            Assert.Equal(0, scanner.CallCount);
            var other = backend.SearchLogsAsync(Search("other", "needle", ConfiguredLogTargetKind.LogFile), cleanupCancellation.Token);
            requests.Add(other);
            Assert.Empty((await other.WaitAsync(TimeSpan.FromSeconds(5))).Errors);
            backend.Dispose();
            using (cache.AcquireSession(Path.Combine(_testDirectory, "still-owned-four.log"))) { }
        }
        finally
        {
            cleanupCancellation.Cancel();
            release.TrySetResult();
            await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));
            backend.Dispose();
            await AssertCacheDisposedAsync(cache);
        }
    }

    private sealed class AdmissionWorkGate(int expected, bool lateFault = false)
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        private int _peak;
        private int _calls;
        internal Task Started => _started.Task;
        internal int Active { get { lock (_gate) return _active; } }
        internal int Peak { get { lock (_gate) return _peak; } }
        internal int Calls { get { lock (_gate) return _calls; } }
        private void Enter()
        {
            lock (_gate)
            {
                _calls++;
                _active++;
                _peak = Math.Max(_peak, _active);
                if (_active == expected) _started.TrySetResult();
            }
        }
        private void Exit() { lock (_gate) _active--; }
        internal void Release() => _release.TrySetResult();
        internal async Task<SearchResult> ScanAsync(string path, CancellationToken ct)
        {
            Enter();
            try
            {
                await _release.Task.WaitAsync(ct);
                return Result(path, "hit");
            }
            finally { Exit(); }
        }
        internal bool Probe(string path)
        {
            Enter();
            try
            {
                _release.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                if (lateFault) throw new IOException("Controlled late probe failure.");
                return false;
            }
            finally { Exit(); }
        }
    }
}
