namespace LogReader.Tests;

using LogReader.App.Services;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public sealed class DashboardPlainReaderAdmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManyFullBuilds_ShareGlobalCapacityAcrossRootsAndDrainBeforeReleasingIt(bool capabilityFallback)
    {
        await WpfTestHost.RunAsync(async () =>
        {
            using var scheduler = new DashboardLoadScheduler();
            scheduler.SetForeground("A");
            var reader = new HeldReader(expectedStarts: 24);
            using var tail = new StubFileTailService();
            var sessions = Enumerable.Range(0, 30).Select(i => new FileSession(
                new FileSessionKey($@"\\synthetic-{i}\share\held.log", FileEncoding.Utf8),
                capabilityFallback ? new NonResumableReader(reader) : reader, tail,
                new StubEncodingDetectionService(), loadScheduler: scheduler)).ToArray();
            foreach (var session in sessions) session.AttachClient(new Client());
            var loads = sessions.Select(session => session.EnsureInteractiveLoadAsync()).ToArray();
            Task<DashboardLoadScheduler.Lease>? next = null;
            try
            {
                await reader.AllStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(24, reader.BuildCount);
                Assert.Equal(24, reader.ActiveCount);
                foreach (var session in sessions) session.BeginShutdown();
                next = scheduler.AcquireAsync(@"\\synthetic-next\share\next.log", () => ["B"], false, default);
                Assert.False(next.IsCompleted);
                Assert.Contains(sessions, session => !session.ShutdownTask.IsCompleted);
                reader.Release.TrySetResult();
                await Task.WhenAll(loads.Concat(sessions.Select(session => session.ShutdownTask))).WaitAsync(TimeSpan.FromSeconds(5));
                using var admitted = await next.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(24, reader.BuildCount);
                Assert.Equal(0, reader.ActiveCount);
            }
            finally
            {
                reader.Release.TrySetResult();
                foreach (var session in sessions) session.BeginShutdown();
                await Task.WhenAll(loads.Concat(sessions.Select(session => session.ShutdownTask))).WaitAsync(TimeSpan.FromSeconds(5));
                if (next != null) (await next.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
                foreach (var session in sessions) session.Dispose();
            }
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FullBuild_RetainsRootAdmissionAndShutdownUntilReaderReturns(bool unc, bool capabilityFallback)
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var path = unc ? @"\\synthetic-host\share\held.log" : @"C:\fixture\held.log";
            using var scheduler = new DashboardLoadScheduler();
            scheduler.SetForeground("A");
            using var setupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var otherBuild = await scheduler.AcquireAsync(path, () => ["A"], false, setupDeadline.Token);
            using var thirdUncSlot = unc ? await scheduler.AcquireAsync(path, () => ["A"], false, setupDeadline.Token) : null;
            using var fourthUncSlot = unc ? await scheduler.AcquireAsync(path, () => ["A"], false, setupDeadline.Token) : null;
            var reader = new HeldReader();
            using var tail = new StubFileTailService();
            using var session = new FileSession(new FileSessionKey(path, FileEncoding.Utf8),
                capabilityFallback ? new NonResumableReader(reader) : reader, tail,
                new StubEncodingDetectionService(), loadScheduler: scheduler);
            session.AttachClient(new Client());
            var load = session.EnsureInteractiveLoadAsync();
            Task<DashboardLoadScheduler.Lease>? next = null;
            try
            {
                await reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(FileLoadPhase.Indexing, session.LoadPhase);
                Assert.Equal(default, session.IndexProgress);
                next = scheduler.AcquireAsync(path, () => ["B"], false, default);
                Assert.False(next.IsCompleted);
                session.BeginShutdown();
                Assert.False(session.ShutdownTask.IsCompleted);
                Assert.False(next.IsCompleted);
                reader.Release.TrySetResult();
                await Task.WhenAll(load, session.ShutdownTask).WaitAsync(TimeSpan.FromSeconds(5));
                using var admitted = await next.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, reader.BuildCount);
                Assert.True(session.HasNoLineIndex);
            }
            finally
            {
                reader.Release.TrySetResult();
                session.BeginShutdown();
                await Task.WhenAll(load, session.ShutdownTask).WaitAsync(TimeSpan.FromSeconds(5));
                if (next != null) (await next.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
            }
        });
    }

    private sealed class Client : IFileSessionClient
    {
        public string DashboardScopeId => "A";
        public bool IsSessionClientDisposed => false;
        public bool IsSessionClientVisible => false;
        public Task HandleSessionContentAdvancedAsync(int previous, int updated, CancellationToken ct) => Task.CompletedTask;
        public Task HandleSessionReloadedAsync(CancellationToken ct) => Task.CompletedTask;
        public void SetStatusText(string text) { }
    }

    private sealed class HeldReader(int expectedStarts = 1) : ILogReaderService
    {
        private int _buildCount;
        private int _activeCount;
        public int BuildCount => Volatile.Read(ref _buildCount);
        public int ActiveCount => Volatile.Read(ref _activeCount);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<LineIndex> BuildIndexAsync(string path, FileEncoding encoding, CancellationToken ct = default)
        {
            var started = Interlocked.Increment(ref _buildCount);
            Interlocked.Increment(ref _activeCount);
            Started.TrySetResult();
            if (started == expectedStarts) AllStarted.TrySetResult();
            try
            {
                await Release.Task; // Deliberately ignores cancellation, like an outstanding OS read.
                return new LineIndex { FilePath = path };
            }
            finally { Interlocked.Decrement(ref _activeCount); }
        }
        public Task<LineIndex> UpdateIndexAsync(string path, LineIndex index, FileEncoding encoding, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ReadLinesAsync(string path, LineIndex index, int start, int count, FileEncoding encoding, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<string> ReadLineAsync(string path, LineIndex index, int line, FileEncoding encoding, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class NonResumableReader(HeldReader reader) : ILogReaderService, IInteractiveLogReaderService
    {
        public Task<IInteractiveIndexBuild> CreateIndexBuildAsync(string path, FileEncoding encoding, CancellationToken ct)
            => Task.FromResult<IInteractiveIndexBuild>(new Build());
        public Task<RecentLogPreview> ReadRecentPreviewAsync(string path, FileEncoding encoding, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<LineIndex> BuildIndexAsync(string path, FileEncoding encoding, CancellationToken ct = default)
            => reader.BuildIndexAsync(path, encoding, ct);
        public Task<LineIndex> UpdateIndexAsync(string path, LineIndex index, FileEncoding encoding, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ReadLinesAsync(string path, LineIndex index, int start, int count, FileEncoding encoding, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<string> ReadLineAsync(string path, LineIndex index, int line, FileEncoding encoding, CancellationToken ct = default)
            => throw new NotSupportedException();
        private sealed class Build : IInteractiveIndexBuild
        {
            public bool CanResume => false;
            public InteractiveIndexProgress Progress => default;
            public ValueTask<bool> AdvanceAsync(int bytes, Func<bool> yield, CancellationToken ct) => throw new NotSupportedException();
            public ValueTask SuspendAsync() => ValueTask.CompletedTask;
            public LineIndex TakeCompletedIndex() => throw new NotSupportedException();
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
