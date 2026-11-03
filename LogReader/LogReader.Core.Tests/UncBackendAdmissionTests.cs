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
            scanner: search, cache: cache, pathExists: gate.Probe, encodingDetection: encoding);
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
