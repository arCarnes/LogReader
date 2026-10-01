namespace LogReader.Tests;

using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public class AutomaticViewportTests
{
    internal sealed class BlockedRead
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class ControlledReader : ILogReaderService
    {
        private readonly List<string> _lines = Enumerable.Range(1, 200).Select(i => $"Line {i}").ToList();
        private BlockedRead? _blockedRead;
        private int _readCount;
        public int ReadCount => Volatile.Read(ref _readCount);
        public bool FailNextRead { get; set; }
        public bool OmitLastLineOnNextRead { get; set; }
        public System.Collections.Concurrent.ConcurrentQueue<(int Start, int Count)> ReadRequests { get; } = new();
        public FileGenerationToken Generation { get; set; } = FileGenerationToken.Create(1, 1);

        public BlockedRead BlockNextRead()
        {
            var blocked = new BlockedRead();
            _blockedRead = blocked;
            return blocked;
        }

        public void Append(string text) { lock (_lines) _lines.Add(text); }

        public Task<LineIndex> BuildIndexAsync(string filePath, FileEncoding encoding, CancellationToken ct = default)
        {
            lock (_lines)
            {
                var index = new LineIndex { FilePath = filePath, FileSize = _lines.Count * 100, GenerationToken = Generation };
                for (var i = 0; i < _lines.Count; i++) index.LineOffsets.Add(i * 100L);
                return Task.FromResult(index);
            }
        }

        public Task<LineIndex> UpdateIndexAsync(string filePath, LineIndex existingIndex, FileEncoding encoding, CancellationToken ct = default)
            => BuildIndexAsync(filePath, encoding, ct);

        public async Task<IReadOnlyList<string>> ReadLinesAsync(string filePath, LineIndex index, int startLine, int count,
            FileEncoding encoding, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _readCount);
            ReadRequests.Enqueue((startLine, count));
            var blocked = Interlocked.Exchange(ref _blockedRead, null);
            if (blocked != null)
            {
                blocked.Started.TrySetResult();
                try { await blocked.Release.Task.WaitAsync(ct); }
                catch (OperationCanceledException)
                {
                    blocked.Canceled.TrySetResult();
                    throw;
                }
            }

            if (FailNextRead)
            {
                FailNextRead = false;
                throw new IOException("Simulated selected-line read failure.");
            }
            if (OmitLastLineOnNextRead)
            {
                OmitLastLineOnNextRead = false;
                count--;
            }
            lock (_lines) return _lines.Skip(startLine).Take(count).ToArray();
        }

        public Task<string> ReadLineAsync(string filePath, LineIndex index, int lineNumber, FileEncoding encoding, CancellationToken ct = default)
        {
            lock (_lines) return Task.FromResult(_lines[lineNumber]);
        }
    }

    internal static LogTabViewModel CreateTab(ControlledReader reader, LogViewportCapacity? capacity = null)
        => new("race", @"C:\test\race.log", reader, new StubFileTailService(), new StubEncodingDetectionService(),
            new AppSettings(), false, null, FileEncoding.Auto, null, TestUiDispatcher.Current, viewportCapacity: capacity);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PendingTail_DisableOrNavigate_DoesNotApplyOrReload(bool navigate, bool toggleAgain)
    {
        var reader = new ControlledReader();
        using var tab = CreateTab(reader);
        await tab.LoadAsync();
        reader.Append("Line 201");
        tab.TotalLines = 201;
        var blocked = reader.BlockNextRead();
        var tail = ((IFileSessionClient)tab).HandleSessionContentAdvancedAsync(200, 201, CancellationToken.None);
        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        tab.AutoScrollEnabled = false;
        if (toggleAgain) { tab.AutoScrollEnabled = true; tab.AutoScrollEnabled = false; }
        if (navigate) await tab.NavigateToLineAsync(30);
        var expectedStart = tab.ViewportStartLine;
        var expectedReads = reader.ReadCount;
        blocked.Release.TrySetResult();
        await tail;
        Assert.Equal(expectedStart, tab.ViewportStartLine);
        Assert.Equal(expectedReads, reader.ReadCount);
        Assert.Equal(201, tab.TotalLines);
        Assert.Contains("201", tab.StatusText);
    }

    [Fact]
    public async Task GlobalBottomSynchronization_DisableDuringRead_PreservesCommittedPosition()
    {
        var reader = new ControlledReader();
        using var vm = TestMainViewModelFactory.Create(new StubLogFileRepository(), new StubLogGroupRepository(),
            new StubSettingsRepository(), reader, new StubSearchService(), new StubFileTailService(),
            new StubEncodingDetectionService(), enableLifecycleTimer: false);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\race.log");
        var tab = vm.SelectedTab!;
        vm.GlobalAutoScrollEnabled = false;
        await tab.LoadViewportAsync(20, tab.ViewportLineCount);
        var blocked = reader.BlockNextRead();
        vm.GlobalAutoScrollEnabled = true;
        var sync = vm.AutoScrollSyncTask;
        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.GlobalAutoScrollEnabled = false;
        blocked.Release.TrySetResult();
        await sync;
        Assert.Equal(20, tab.ViewportStartLine);
        Assert.All(vm.Tabs, item => Assert.False(item.AutoScrollEnabled));
    }

    [Fact]
    public async Task AutomaticCapacity_DisableDuringRead_ReconcilesAtCommittedPosition()
    {
        var capacity = new LogViewportCapacity();
        var reader = new ControlledReader();
        using var tab = CreateTab(reader, capacity);
        await tab.LoadAsync();
        await tab.LoadViewportAsync(20, 50);
        capacity.UpdateLineCount(80);
        var blocked = reader.BlockNextRead();
        var sync = tab.SynchronizeViewportCapacityAsync();
        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        tab.AutoScrollEnabled = false;
        blocked.Release.TrySetResult();
        Assert.True(await sync);
        Assert.Equal(20, tab.ViewportStartLine);
        Assert.Equal(80, tab.VisibleLines.Count);
    }

    [Fact]
    public async Task DisabledTail_UpdatesCounts_EnablingResumesBottomAndLaterAppends()
    {
        var reader = new ControlledReader();
        using var tab = CreateTab(reader);
        await tab.LoadAsync();
        tab.AutoScrollEnabled = false;
        await tab.LoadViewportAsync(20, 50);
        reader.Append("Line 201");
        tab.SuspendTailing();
        await tab.ResumeTailingWithCatchUpAsync(250);
        Assert.Equal(201, tab.TotalLines);
        Assert.Equal(20, tab.ViewportStartLine);
        tab.AutoScrollEnabled = true;
        Assert.True(await tab.MoveViewportToBottomAsync());
        Assert.Equal(151, tab.ViewportStartLine);
        reader.Append("Line 202");
        tab.SuspendTailing();
        await tab.ResumeTailingWithCatchUpAsync(250);
        Assert.Equal(152, tab.ViewportStartLine);
        Assert.Equal(202, tab.VisibleLines.Last().LineNumber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FilteredTail_DisableDuringProcessingOrReload_UpdatesMatchesWithoutMoving(bool reload)
    {
        var reader = new ControlledReader();
        using var tab = CreateTab(reader);
        await tab.LoadAsync();
        await tab.ApplyFilterAsync(Enumerable.Range(1, 200).ToArray(), "matches", new SearchRequest
        {
            Query = "Line", SourceMode = SearchRequestSourceMode.SnapshotAndTail
        });
        reader.Append("Line 201");
        tab.TotalLines = 201;
        var blocked = reader.BlockNextRead();
        var tail = tab.ApplyTailFilterForAppendedLinesAsync(201, CancellationToken.None);
        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Force the reload branch by moving away from the old filtered bottom.
        if (reload)
        {
            await tab.LoadViewportAsync(20, tab.ViewportLineCount);
            var reloadBlocked = reader.BlockNextRead();
            blocked.Release.TrySetResult();
            await reloadBlocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            blocked = reloadBlocked;
        }
        tab.AutoScrollEnabled = false;
        var expectedStart = tab.ViewportStartLine;
        blocked.Release.TrySetResult();
        await tail;
        Assert.Equal(201, tab.FilteredLineCount);
        Assert.Equal(expectedStart, tab.ViewportStartLine);
    }
}
