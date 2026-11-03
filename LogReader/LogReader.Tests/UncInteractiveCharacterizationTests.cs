namespace LogReader.Tests;

using System.IO;
using LogReader.App.Services;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

public sealed class UncInteractiveCharacterizationTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    public async Task InteractiveOwnership_KeepsAdmissionUntilSuspendOrUnknownBuildDisposalReturns(
        bool unknownIdentity, bool cancelDuringSuspend, bool failAdvance)
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "WeezTail-interactive-owner-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(root);
            using var paths = LogReader.Core.AppPaths.BeginTestScope(rootPath: root, localCacheDirectory: Path.Combine(root, "Cache"));
            var path = Path.Combine(root, "fixture.log");
            await File.WriteAllTextAsync(path, "seed\n");
            var reader = new ControlledReader(unknownIdentity, failAdvance: failAdvance);
            using var scheduler = new DashboardLoadScheduler();
            scheduler.SetForeground("scope");
            using var probeCancellation = new CancellationTokenSource();
            var blocker = await scheduler.AcquireAsync(path, () => ["scope"], false, default);
            var session = new FileSession(new FileSessionKey(path, FileEncoding.Utf8), reader,
                new NoPollingTailService(), new FileEncodingDetectionService(), loadScheduler: scheduler);
            session.AttachClient(new ScopeClient());
            Task? load = null;
            Task<DashboardLoadScheduler.Lease>? probe = null;
            DashboardLoadScheduler.Lease? probeLease = null;
            try
            {
                load = session.EnsureInteractiveLoadAsync();
                await (unknownIdentity || failAdvance ? reader.DisposalStarted.Task : reader.SuspensionStarted.Task)
                    .WaitAsync(TimeSpan.FromSeconds(5));
                probe = scheduler.AcquireAsync(path, () => ["scope"], preview: true, probeCancellation.Token);
                Assert.False(probe.IsCompleted);
                Assert.True(reader.HandleOpen);
                if (cancelDuringSuspend)
                {
                    session.Dispose();
                    Assert.False(session.ShutdownTask.IsCompleted);
                    Assert.False(probe.IsCompleted);
                }
                reader.ReleaseOwnedOperation.TrySetResult();
                probeLease = await probe.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(reader.HandleOpen);
                if (unknownIdentity)
                {
                    Assert.Equal(1, reader.DisposeCount);
                    Assert.Equal(0, reader.AdvanceCount);
                    Assert.Equal(0, reader.FallbackCount);
                }
                else if (!cancelDuringSuspend && !failAdvance)
                    Assert.False(load.IsCompleted);
                probeLease.Dispose();
                probeLease = null;
                await load.WaitAsync(TimeSpan.FromSeconds(5));
                await reader.BuildDisposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, reader.DisposeCount);
                if (cancelDuringSuspend)
                {
                    Assert.Null(session.DebugLineIndex);
                    Assert.Equal(0, reader.TakeCount);
                }
                else if (failAdvance)
                {
                    Assert.True(session.HasLoadError);
                    Assert.Null(session.DebugLineIndex);
                    Assert.Equal(0, reader.TakeCount);
                }
                else
                {
                    Assert.False(session.HasLoadError);
                    Assert.Equal(1, session.TotalLines);
                    Assert.Equal(unknownIdentity ? 1 : 0, reader.FallbackCount);
                    Assert.Equal(unknownIdentity ? 0 : 1, reader.TakeCount);
                }
            }
            finally
            {
                probeCancellation.Cancel();
                reader.ReleaseOwnedOperation.TrySetResult();
                probeLease?.Dispose();
                if (probe != null)
                {
                    try { (await probe).Dispose(); }
                    catch (OperationCanceledException) when (probeCancellation.IsCancellationRequested) { }
                }
                session.Dispose();
                blocker.Dispose();
                try { await WpfTestHost.DrainOwnedWorkAsync(Task.WhenAll(load ?? Task.CompletedTask, session.ShutdownTask)); }
                finally { paths.Dispose(); }
                Directory.Delete(root, recursive: true);
            }
        });
    }

    private sealed class ControlledReader(bool unknownIdentity, bool failAdvance = false) : ILogReaderService, IInteractiveLogReaderService
    {
        private readonly ChunkedLogReaderService _inner = new();
        private int _handleOpen;
        private int _advanceCount;
        private int _disposeCount;
        private int _fallbackCount;
        private int _takeCount;
        public bool HandleOpen => Volatile.Read(ref _handleOpen) != 0;
        public int AdvanceCount => Volatile.Read(ref _advanceCount);
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public int FallbackCount => Volatile.Read(ref _fallbackCount);
        public int TakeCount => Volatile.Read(ref _takeCount);
        public TaskCompletionSource SuspensionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseOwnedOperation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BuildDisposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IInteractiveIndexBuild> CreateIndexBuildAsync(string path, FileEncoding encoding, CancellationToken ct)
        {
            var index = await _inner.BuildIndexAsync(path, encoding, ct);
            Volatile.Write(ref _handleOpen, 1);
            return new Build(this, index, !unknownIdentity, failAdvance);
        }
        public async Task<LineIndex> BuildIndexAsync(string path, FileEncoding encoding, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _fallbackCount);
            return await _inner.BuildIndexAsync(path, encoding, ct);
        }
        public Task<LineIndex> UpdateIndexAsync(string path, LineIndex index, FileEncoding encoding, CancellationToken ct = default)
            => _inner.UpdateIndexAsync(path, index, encoding, ct);
        public Task<IReadOnlyList<string>> ReadLinesAsync(string path, LineIndex index, int start, int count, FileEncoding encoding, CancellationToken ct = default)
            => _inner.ReadLinesAsync(path, index, start, count, encoding, ct);
        public Task<string> ReadLineAsync(string path, LineIndex index, int line, FileEncoding encoding, CancellationToken ct = default)
            => _inner.ReadLineAsync(path, index, line, encoding, ct);
        public Task<RecentLogPreview> ReadRecentPreviewAsync(string path, FileEncoding encoding, CancellationToken ct)
            => ((IInteractiveLogReaderService)_inner).ReadRecentPreviewAsync(path, encoding, ct);

        private sealed class Build(ControlledReader owner, LineIndex index, bool canResume, bool failAdvance) : IInteractiveIndexBuild
        {
            private LineIndex? _index = index;
            private int _disposed;
            public bool CanResume => canResume;
            public InteractiveIndexProgress Progress => new(Math.Min(2, owner.AdvanceCount), 2, 1, owner.AdvanceCount >= 2);
            public ValueTask<bool> AdvanceAsync(int bytes, Func<bool> shouldYield, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                var advance = Interlocked.Increment(ref owner._advanceCount);
                if (shouldYield()) return ValueTask.FromResult(false);
                Volatile.Write(ref owner._handleOpen, 1);
                if (failAdvance) throw new IOException("Controlled advance failure.");
                var complete = advance >= 2;
                if (complete) Volatile.Write(ref owner._handleOpen, 0);
                return ValueTask.FromResult(complete);
            }
            public async ValueTask SuspendAsync()
            {
                if (failAdvance)
                {
                    Volatile.Write(ref owner._handleOpen, 0);
                    return;
                }
                owner.SuspensionStarted.TrySetResult();
                await owner.ReleaseOwnedOperation.Task; // Controlled uninterruptible close; capacity stays owned.
                Volatile.Write(ref owner._handleOpen, 0);
            }
            public LineIndex TakeCompletedIndex()
            {
                Interlocked.Increment(ref owner._takeCount);
                return Interlocked.Exchange(ref _index, null) ?? throw new InvalidOperationException("Index transferred twice.");
            }
            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                Interlocked.Increment(ref owner._disposeCount);
                owner.DisposalStarted.TrySetResult();
                if (!canResume || failAdvance) await owner.ReleaseOwnedOperation.Task;
                Volatile.Write(ref owner._handleOpen, 0);
                _index?.Dispose();
                _index = null;
                owner.BuildDisposed.TrySetResult();
            }
        }
    }

    private sealed class ScopeClient : IFileSessionClient
    {
        public string DashboardScopeId => "scope";
        public bool IsSessionClientDisposed => false;
        public bool IsSessionClientVisible => true;
        public Task HandleSessionContentAdvancedAsync(int previous, int updated, CancellationToken ct) => Task.CompletedTask;
        public Task HandleSessionReloadedAsync(CancellationToken ct) => Task.CompletedTask;
        public void SetStatusText(string text) { }
    }

    private sealed class NoPollingTailService : IFileTailService
    {
        public event EventHandler<TailEventArgs>? LinesAppended { add { } remove { } }
        public event EventHandler<FileRotatedEventArgs>? FileRotated { add { } remove { } }
        public event EventHandler<FileAvailabilityChangedEventArgs>? FileAvailabilityChanged { add { } remove { } }
        public event EventHandler<TailErrorEventArgs>? TailError { add { } remove { } }
        public void StartTailing(string path, FileEncoding encoding, int pollingIntervalMs = 250) { }
        public void StopTailing(string path) { }
        public void StopAll() { }
        public void Dispose() { }
    }
}
