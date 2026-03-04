namespace LogReader.Tests;

using System.ComponentModel;
using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

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

    private static LogTabViewModel CreateTab(ILogReaderService stub) =>
        new("test-id", @"C:\test\file.log", stub, new StubFileTailService(), new AppSettings());

    // ─── #1 Load cancellation race ────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_CalledTwice_SecondCancelsFirst_NoErrorStatus()
    {
        var stub = new DelayedBuildStub(delayMs: 50);
        var tab = CreateTab(stub);

        var t1 = tab.LoadAsync();
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
}
