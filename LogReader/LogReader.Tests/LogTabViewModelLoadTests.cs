namespace LogReader.Tests;

using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

/// <summary>
/// Tests for LoadAsync cancellation/restart race (#1) and encoding change during load (#2).
/// </summary>
public class LogTabViewModelLoadTests
{
    // ─── Stub ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// BuildIndexAsync blocks for <paramref name="delayMs"/> ms (cancellation-aware).
    /// Signals <see cref="FirstBuildStarted"/> as soon as the first call enters BuildIndexAsync.
    /// </summary>
    private class DelayedBuildStub : ILogReaderService
    {
        private readonly int _delayMs;
        private int _callCount;
        private readonly TaskCompletionSource<bool> _firstBuildStarted = new();

        public int CallCount => _callCount;
        public FileEncoding LastEncoding { get; private set; } = FileEncoding.Utf8;
        public Task FirstBuildStarted => _firstBuildStarted.Task;

        public DelayedBuildStub(int delayMs = 50) => _delayMs = delayMs;

        public async Task<LineIndex> BuildIndexAsync(string filePath, FileEncoding encoding, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _callCount);
            LastEncoding = encoding;
            _firstBuildStarted.TrySetResult(true); // fires on first call; no-op on subsequent

            await Task.Delay(_delayMs, ct); // throws OperationCanceledException if ct is cancelled

            var index = new LineIndex { FilePath = filePath, FileSize = 100 };
            index.LineOffsets.Add(0L);
            return index;
        }

        public Task<LineIndex> UpdateIndexAsync(string filePath, LineIndex existingIndex, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(existingIndex);

        public Task<IReadOnlyList<string>> ReadLinesAsync(string filePath, LineIndex index, int startLine, int count, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(new List<string> { "line 1" });

        public Task<string> ReadLineAsync(string filePath, LineIndex index, int lineNumber, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult("line 1");
    }

    private sealed class BlockingViewportReadStub : ILogReaderService
    {
        private readonly TaskCompletionSource<bool> _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseRead = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ReadStarted => _readStarted.Task;

        public void ReleaseRead() => _releaseRead.TrySetResult(true);

        public Task<LineIndex> BuildIndexAsync(string filePath, FileEncoding encoding, CancellationToken ct = default)
        {
            var index = new LineIndex { FilePath = filePath, FileSize = 100 };
            index.LineOffsets.Add(0L);
            return Task.FromResult(index);
        }

        public Task<LineIndex> UpdateIndexAsync(string filePath, LineIndex existingIndex, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(existingIndex);

        public async Task<IReadOnlyList<string>> ReadLinesAsync(string filePath, LineIndex index, int startLine, int count, FileEncoding encoding, CancellationToken ct = default)
        {
            _readStarted.TrySetResult(true);
            await _releaseRead.Task.WaitAsync(ct);
            return new List<string> { "line 1" };
        }

        public Task<string> ReadLineAsync(string filePath, LineIndex index, int lineNumber, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult("line 1");
    }

    private sealed class TailAppendFailureStub : ILogReaderService
    {
        private bool _failOnTailRead;

        public void FailNextTailRead() => _failOnTailRead = true;

        public Task<LineIndex> BuildIndexAsync(string filePath, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(CreateIndex(filePath, lineCount: 60));

        public Task<LineIndex> UpdateIndexAsync(string filePath, LineIndex existingIndex, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(CreateIndex(filePath, lineCount: 61));

        public Task<IReadOnlyList<string>> ReadLinesAsync(
            string filePath,
            LineIndex index,
            int startLine,
            int count,
            FileEncoding encoding,
            CancellationToken ct = default)
        {
            if (_failOnTailRead && startLine >= 60)
                throw new IOException("tail append read failed");

            var lines = Enumerable.Range(startLine + 1, Math.Max(0, count))
                .Select(lineNumber => $"Line {lineNumber}")
                .ToList();
            return Task.FromResult<IReadOnlyList<string>>(lines);
        }

        public Task<string> ReadLineAsync(string filePath, LineIndex index, int lineNumber, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult($"Line {lineNumber + 1}");

        private static LineIndex CreateIndex(string filePath, int lineCount)
        {
            var index = new LineIndex
            {
                FilePath = filePath,
                FileSize = lineCount * 100
            };

            for (var i = 0; i < lineCount; i++)
                index.LineOffsets.Add(i * 100L);

            return index;
        }
    }

    private sealed class RotationReloadFailureStub : ILogReaderService
    {
        private bool _failBuild;

        public void FailReload() => _failBuild = true;

        public Task<LineIndex> BuildIndexAsync(string filePath, FileEncoding encoding, CancellationToken ct = default)
        {
            if (_failBuild)
                throw new IOException("rotation reload failed");

            var index = new LineIndex
            {
                FilePath = filePath,
                FileSize = 100
            };
            index.LineOffsets.Add(0L);
            return Task.FromResult(index);
        }

        public Task<LineIndex> UpdateIndexAsync(string filePath, LineIndex existingIndex, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(existingIndex);

        public Task<IReadOnlyList<string>> ReadLinesAsync(string filePath, LineIndex index, int startLine, int count, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(new List<string> { "line 1" });

        public Task<string> ReadLineAsync(string filePath, LineIndex index, int lineNumber, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult("line 1");
    }

    private static LogTabViewModel CreateTab(ILogReaderService stub) =>
        new("test-id", @"C:\test\file.log", stub, new StubFileTailService(), new FileEncodingDetectionService(), new AppSettings());

    // ─── #1 Load cancellation race ────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_CalledTwice_SecondCancelsFirst_NoErrorStatus()
    {
        var stub = new DelayedBuildStub(delayMs: 50);
        var tab = CreateTab(stub);

        var t1 = tab.LoadAsync();
        await stub.FirstBuildStarted;
        var t2 = tab.LoadAsync(); // cancels t1
        await Task.WhenAll(t1, t2);

        Assert.DoesNotContain("Error", tab.StatusText);
        Assert.True(tab.TotalLines > 0);
        Assert.Equal(2, stub.CallCount); // both build attempts were made
    }

    [Fact]
    public async Task LoadAsync_CalledTwice_IsLoadingFalseAfterCompletion()
    {
        var stub = new DelayedBuildStub(delayMs: 50);
        var tab = CreateTab(stub);

        var t1 = tab.LoadAsync();
        var t2 = tab.LoadAsync();
        await Task.WhenAll(t1, t2);

        Assert.False(tab.IsLoading);
    }

    [Fact]
    public async Task LoadAsync_CalledTwice_OnlyFinalResultKept()
    {
        // Both builds return identical data in the stub, so we verify
        // that the tab ended up in a consistent loaded state — not a half-built one.
        var stub = new DelayedBuildStub(delayMs: 50);
        var tab = CreateTab(stub);

        var t1 = tab.LoadAsync();
        var t2 = tab.LoadAsync();
        await Task.WhenAll(t1, t2);

        Assert.False(tab.IsLoading);
        Assert.True(tab.TotalLines > 0);
        Assert.NotNull(tab.VisibleLines); // collection exists and was populated
        Assert.NotEmpty(tab.VisibleLines);
    }

    // ─── #2 Encoding change during initial load ───────────────────────────────

    [Fact]
    public async Task OnEncodingChanged_WhileLoading_RestartsWithNewEncoding()
    {
        var stub = new DelayedBuildStub(delayMs: 100);
        var tab = CreateTab(stub);

        // Watch for IsLoading transitioning to false — this fires when the second load finishes.
        var loadingCompleted = new TaskCompletionSource<bool>();
        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LogTabViewModel.IsLoading) && !tab.IsLoading)
                loadingCompleted.TrySetResult(true);
        };

        var t1 = tab.LoadAsync();
        await stub.FirstBuildStarted; // first BuildIndexAsync is definitely running

        tab.Encoding = FileEncoding.Utf16; // OnEncodingChanged → cancels t1, starts second load

        await t1; // t1 completes quickly (first build cancelled)
        await loadingCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(tab.IsLoading);
        Assert.DoesNotContain("Error", tab.StatusText);
        Assert.Equal(FileEncoding.Utf16, stub.LastEncoding); // index rebuilt with the new encoding
    }

    [Fact]
    public async Task OnEncodingChanged_BeforeAnyLoad_IsIgnored()
    {
        // Guard: _lineIndex == null && !IsLoading → OnEncodingChanged should no-op.
        var stub = new DelayedBuildStub(delayMs: 50);
        var tab = CreateTab(stub);

        // Change encoding without ever calling LoadAsync
        tab.Encoding = FileEncoding.Utf16;

        // Give any fire-and-forget a chance to start
        await Task.Delay(100);

        Assert.Equal(0, stub.CallCount); // no build should have been triggered
        Assert.False(tab.IsLoading);
    }

    [Fact]
    public async Task EncodingDisplayLabel_AutoMode_ShowsResolvedUtf8BeforeAndAfterLoad()
    {
        var path = Path.Combine(Path.GetTempPath(), $"logreader-display-utf8-{Guid.NewGuid():N}.log");
        await File.WriteAllTextAsync(path, "line 1\nline 2\n", new UTF8Encoding(false));

        try
        {
            var tab = new LogTabViewModel("test-id", path, new StubLogReaderService(), new StubFileTailService(), new FileEncodingDetectionService(), new AppSettings());

            Assert.Equal("Auto (UTF-8)", tab.SelectedEncodingDisplayLabel);
            Assert.Equal("Auto (UTF-8)", tab.EncodingOptions[0].Label);

            await tab.LoadAsync();

            Assert.Equal("Auto (UTF-8)", tab.SelectedEncodingDisplayLabel);
            Assert.Equal("Auto (UTF-8)", tab.EncodingOptions[0].Label);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task EncodingDisplayLabel_SwitchingBetweenAutoAndManual_UpdatesText()
    {
        var path = Path.Combine(Path.GetTempPath(), $"logreader-display-switch-{Guid.NewGuid():N}.log");
        await File.WriteAllTextAsync(path, "line 1\nline 2\n", new UTF8Encoding(false));

        try
        {
            var tab = new LogTabViewModel("test-id", path, new StubLogReaderService(), new StubFileTailService(), new FileEncodingDetectionService(), new AppSettings());

            tab.Encoding = FileEncoding.Utf16;
            Assert.Equal("UTF-16", tab.SelectedEncodingDisplayLabel);

            tab.Encoding = FileEncoding.Auto;
            Assert.Equal("Auto (UTF-8)", tab.SelectedEncodingDisplayLabel);

            await tab.LoadAsync();

            Assert.Equal("Auto (UTF-8)", tab.SelectedEncodingDisplayLabel);
            Assert.Equal("Auto (UTF-8)", tab.EncodingOptions[0].Label);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task EncodingDisplayLabel_AutoMode_ShowsResolvedUtf16()
    {
        var path = Path.Combine(Path.GetTempPath(), $"logreader-display-utf16-{Guid.NewGuid():N}.log");
        await File.WriteAllBytesAsync(path, Encoding.Unicode.GetBytes("line 1\nline 2\n"));

        try
        {
            var tab = new LogTabViewModel("test-id", path, new StubLogReaderService(), new StubFileTailService(), new FileEncodingDetectionService(), new AppSettings());

            tab.Encoding = FileEncoding.Auto;

            Assert.Equal("Auto (UTF-16)", tab.SelectedEncodingDisplayLabel);
            Assert.Equal("Auto (UTF-16)", tab.EncodingOptions[0].Label);

            await tab.LoadAsync();

            Assert.Equal("Auto (UTF-16)", tab.SelectedEncodingDisplayLabel);
            Assert.Equal("Auto (UTF-16)", tab.EncodingOptions[0].Label);
            Assert.Equal(FileEncoding.Utf16, tab.EffectiveEncoding);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task Dispose_DuringInFlightViewportRead_DoesNotBlock()
    {
        var stub = new BlockingViewportReadStub();
        var tab = CreateTab(stub);

        var loadTask = tab.LoadAsync();
        await stub.ReadStarted.WaitAsync(TimeSpan.FromSeconds(5));

        var disposeTask = Task.Run(tab.Dispose);
        var completedQuickly = await Task.WhenAny(disposeTask, Task.Delay(500)) == disposeTask;

        stub.ReleaseRead();
        try { await loadTask; } catch { }
        await disposeTask;

        Assert.True(completedQuickly, "Dispose blocked while viewport read was in-flight.");
    }

    [Fact]
    public async Task Dispose_SchedulesLineIndexCleanup_WhenLockIsTemporarilyHeld()
    {
        var tab = CreateTab(new StubLogReaderService());
        await tab.LoadAsync();

        var session = tab.ActiveSession;
        var lineIndexLock = session.DebugLineIndexLock;
        await lineIndexLock.WaitAsync();

        Task? disposeTask;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            tab.Dispose();
            stopwatch.Stop();

            disposeTask = session.DebugLineIndexDisposeTask;
            Assert.NotNull(disposeTask);
            Assert.False(disposeTask!.IsCompleted);
            Assert.InRange(stopwatch.ElapsedMilliseconds, 0, 500);
        }
        finally
        {
            if (lineIndexLock.CurrentCount == 0)
                lineIndexLock.Release();
        }

        await disposeTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(session.DebugLineIndex);
    }

    [Fact]
    public async Task BeginShutdown_CancelsLoad_PreventsTailResume_AndSuppressesTailErrors()
    {
        var stub = new DelayedBuildStub(delayMs: 1000);
        var tailService = new StubFileTailService();
        var tab = new LogTabViewModel("test-id", @"C:\test\file.log", stub, tailService, new FileEncodingDetectionService(), new AppSettings());

        var loadTask = tab.LoadAsync();
        await stub.FirstBuildStarted.WaitAsync(TimeSpan.FromSeconds(5));

        var stopwatch = Stopwatch.StartNew();
        tab.BeginShutdown();
        await loadTask.WaitAsync(TimeSpan.FromSeconds(5));
        stopwatch.Stop();

        tab.StatusText = "Closing";
        tab.ApplyVisibleTailingMode(pollingIntervalMs: 250);
        await Task.Delay(100);
        tailService.RaiseTailError(tab.FilePath, "ignored after shutdown");

        Assert.True(tab.IsShuttingDown);
        Assert.False(tab.IsLoading);
        Assert.True(tab.IsSuspended);
        Assert.Equal(0, tailService.StartCallCount);
        Assert.Equal("Closing", tab.StatusText);
        Assert.InRange(stopwatch.ElapsedMilliseconds, 0, 500);
    }

    [Fact]
    public void TailErrorEvent_SetsSuspendedStatus()
    {
        var tailService = new StubFileTailService();
        var tab = new LogTabViewModel("test-id", @"C:\test\file.log", new StubLogReaderService(), tailService, new FileEncodingDetectionService(), new AppSettings());

        tailService.RaiseTailError(tab.FilePath, "simulated tail failure");

        Assert.True(tab.IsSuspended);
        Assert.Equal("Tailing stopped: simulated tail failure", tab.StatusText);
    }

    [Fact]
    public async Task LinesAppended_WhenViewportRefreshFails_SurfacesHandledTailError()
    {
        var reader = new TailAppendFailureStub();
        var tailService = new StubFileTailService();
        var tab = new LogTabViewModel("test-id", @"C:\test\file.log", reader, tailService, new FileEncodingDetectionService(), new AppSettings());
        await tab.LoadAsync();

        var statusChanged = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LogTabViewModel.StatusText) &&
                tab.StatusText.Contains("Tail error:", StringComparison.Ordinal))
            {
                statusChanged.TrySetResult(tab.StatusText);
            }
        };

        reader.FailNextTailRead();
        tailService.RaiseLinesAppended(tab.FilePath);

        var status = await statusChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("tail append read failed", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileRotated_WhenReloadFails_SurfacesHandledLoadError()
    {
        var reader = new RotationReloadFailureStub();
        var tailService = new StubFileTailService();
        var tab = new LogTabViewModel("test-id", @"C:\test\file.log", reader, tailService, new FileEncodingDetectionService(), new AppSettings());
        await tab.LoadAsync();

        var loadErrorObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LogTabViewModel.HasLoadError) && tab.HasLoadError)
                loadErrorObserved.TrySetResult(true);
        };

        reader.FailReload();
        tailService.RaiseFileRotated(tab.FilePath);

        await loadErrorObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(tab.HasLoadError);
        Assert.Contains("rotation reload failed", tab.StatusText, StringComparison.Ordinal);
    }
}
