using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

namespace LogReader.Tests;

/// <summary>
/// Tests the navigation chain: NavigateToLineAsync → PropertyChanged(NavigateToLineNumber).
/// Verifies that every consecutive call fires PropertyChanged with the correct value,
/// which is the prerequisite for the view to scroll to the line.
/// </summary>
public class NavigationTests
{
    /// <summary>
    /// Holds the first navigation read until a newer navigation cancels it.
    /// </summary>
    private sealed class GatedLogReaderService : ILogReaderService
    {
        private readonly StubLogReaderService _inner = new();
        private int _blockNextRead;
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadCanceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void BlockNextRead() => Interlocked.Exchange(ref _blockNextRead, 1);

        public Task<LineIndex> BuildIndexAsync(string filePath, FileEncoding encoding, CancellationToken ct = default)
            => _inner.BuildIndexAsync(filePath, encoding, ct);

        public Task<LineIndex> UpdateIndexAsync(string filePath, LineIndex existingIndex, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(existingIndex);

        public async Task<IReadOnlyList<string>> ReadLinesAsync(string filePath, LineIndex index, int startLine, int count, FileEncoding encoding, CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _blockNextRead, 0) == 1)
            {
                ReadStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    ReadCanceled.TrySetResult();
                    throw;
                }
            }

            return await _inner.ReadLinesAsync(filePath, index, startLine, count, encoding, ct);
        }

        public Task<string> ReadLineAsync(string filePath, LineIndex index, int lineNumber, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult($"Line {lineNumber + 1} content");
    }

    private static async Task<LogTabViewModel> CreateLoadedTabAsync(int lineCount = 200, ILogReaderService? logReader = null)
    {
        var tailService = new StubFileTailService();
        var tab = new LogTabViewModel(
            "test-id", @"C:\test\file.log", logReader ?? new StubLogReaderService(lineCount),
            tailService, new StubEncodingDetectionService(), new AppSettings(),
            skipInitialEncodingResolution: false, sessionRegistry: null,
            initialEncoding: FileEncoding.Auto, scopeDashboardId: null,
            uiDispatcher: TestUiDispatcher.Current);
        await tab.LoadAsync();
        return tab;
    }

    // ─── Test 1: Single navigation fires PropertyChanged ────────────────────

    [Fact]
    public async Task NavigateToLineAsync_SingleCall_FiresPropertyChanged()
    {
        using var tab = await CreateLoadedTabAsync();
        var firedValues = new List<int>();

        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LogTabViewModel.NavigateToLineNumber))
                firedValues.Add(tab.NavigateToLineNumber);
        };

        await tab.NavigateToLineAsync(47);

        Assert.Contains(47, firedValues);
        Assert.Equal(47, tab.NavigateToLineNumber);
    }

    // ─── Test 2: Two consecutive navigations both fire with correct values ──

    [Fact]
    public async Task NavigateToLineAsync_TwoConsecutiveCalls_BothFirePropertyChanged()
    {
        using var tab = await CreateLoadedTabAsync();
        var positiveValues = new List<int>();

        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LogTabViewModel.NavigateToLineNumber) && tab.NavigateToLineNumber > 0)
                positiveValues.Add(tab.NavigateToLineNumber);
        };

        await tab.NavigateToLineAsync(47);
        await tab.NavigateToLineAsync(100);

        Assert.Equal(2, positiveValues.Count);
        Assert.Equal(47, positiveValues[0]);
        Assert.Equal(100, positiveValues[1]);
    }

    // ─── Test 3: Three consecutive calls — the alternating pattern ──────────

    [Fact]
    public async Task NavigateToLineAsync_ThreeConsecutiveCalls_AllFirePropertyChanged()
    {
        using var tab = await CreateLoadedTabAsync();
        var positiveValues = new List<int>();

        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LogTabViewModel.NavigateToLineNumber) && tab.NavigateToLineNumber > 0)
                positiveValues.Add(tab.NavigateToLineNumber);
        };

        await tab.NavigateToLineAsync(10);
        await tab.NavigateToLineAsync(20);
        await tab.NavigateToLineAsync(30);

        Assert.Equal(3, positiveValues.Count);
        Assert.Equal(new[] { 10, 20, 30 }, positiveValues);
    }

    // ─── Test 4: Navigate to SAME line twice — must still fire both times ───

    [Fact]
    public async Task NavigateToLineAsync_SameLineTwice_StillFiresBothTimes()
    {
        using var tab = await CreateLoadedTabAsync();
        var positiveValues = new List<int>();

        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LogTabViewModel.NavigateToLineNumber) && tab.NavigateToLineNumber > 0)
                positiveValues.Add(tab.NavigateToLineNumber);
        };

        await tab.NavigateToLineAsync(47);
        await tab.NavigateToLineAsync(47);

        Assert.Equal(2, positiveValues.Count);
        Assert.All(positiveValues, v => Assert.Equal(47, v));
    }

    // ─── Test 5: Viewport contains the target line after navigation ─────────

    [Fact]
    public async Task NavigateToLineAsync_ViewportContainsTargetLine()
    {
        using var tab = await CreateLoadedTabAsync(500);

        await tab.NavigateToLineAsync(250);

        var lineNumbers = tab.VisibleLines.Select(l => l.LineNumber).ToList();
        Assert.Contains(250, lineNumbers);
    }

    // ─── Test 6: Final state of NavigateToLineNumber is correct ─────────────

    [Fact]
    public async Task NavigateToLineAsync_FinalState_IsTargetLine()
    {
        using var tab = await CreateLoadedTabAsync();

        await tab.NavigateToLineAsync(47);
        Assert.Equal(47, tab.NavigateToLineNumber);

        await tab.NavigateToLineAsync(100);
        Assert.Equal(100, tab.NavigateToLineNumber);

        await tab.NavigateToLineAsync(10);
        Assert.Equal(10, tab.NavigateToLineNumber);
    }

    // ─── Test 7: Concurrent navigations — last one wins, no corruption ──────

    [Fact]
    public async Task NavigateToLineAsync_ConcurrentCalls_LastNavigationWins()
    {
        var reader = new GatedLogReaderService();
        using var tab = await CreateLoadedTabAsync(logReader: reader);
        var positiveValues = new List<int>();

        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LogTabViewModel.NavigateToLineNumber) && tab.NavigateToLineNumber > 0)
                positiveValues.Add(tab.NavigateToLineNumber);
        };

        // Keep click 1 in flight regardless of scheduling before click 2 cancels it.
        reader.BlockNextRead();
        var t1 = tab.NavigateToLineAsync(47);
        await reader.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var t2 = tab.NavigateToLineAsync(100);
        await Task.WhenAll(t1, t2, reader.ReadCanceled.Task).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(100, tab.NavigateToLineNumber);
        Assert.Single(positiveValues);
        Assert.Equal(100, positiveValues[0]);

        Assert.Contains(tab.VisibleLines, l => l.LineNumber == 100);
    }

    // ─── Test 8: VisibleLines contains target when PropertyChanged fires ─────

    [Fact]
    public async Task NavigateToLineAsync_VisibleLinesReadyWhenPropertyChangedFires()
    {
        using var tab = await CreateLoadedTabAsync(500);
        var issues = new List<string>();

        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LogTabViewModel.NavigateToLineNumber) && tab.NavigateToLineNumber > 0)
            {
                var target = tab.NavigateToLineNumber;
                if (!tab.VisibleLines.Any(l => l.LineNumber == target))
                    issues.Add($"Line {target} not in VisibleLines when PropertyChanged fired");
            }
        };

        await tab.NavigateToLineAsync(47);
        await tab.NavigateToLineAsync(250);
        await tab.NavigateToLineAsync(400);

        Assert.Empty(issues);
    }
}
