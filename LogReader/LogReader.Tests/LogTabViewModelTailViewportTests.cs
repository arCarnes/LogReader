namespace LogReader.Tests;

using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

public class LogTabViewModelTailViewportTests
{
    private sealed class RecordingAppendableLogReader : ILogReaderService
    {
        private readonly List<string> _lines;

        public RecordingAppendableLogReader(IEnumerable<string> initialLines)
        {
            _lines = initialLines.ToList();
        }

        public List<(int StartLine, int Count)> ReadLinesRequests { get; } = new();
        public int ReadLineCallCount { get; private set; }

        public void AppendLine(string line) => _lines.Add(line);

        public Task<LineIndex> BuildIndexAsync(string filePath, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(CreateIndex(filePath));

        public Task<LineIndex> UpdateIndexAsync(string filePath, LineIndex existingIndex, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(CreateIndex(filePath));

        public Task<IReadOnlyList<string>> ReadLinesAsync(
            string filePath,
            LineIndex index,
            int startLine,
            int count,
            FileEncoding encoding,
            CancellationToken ct = default)
        {
            ReadLinesRequests.Add((startLine, count));
            var boundedStart = Math.Max(0, startLine);
            var boundedCount = Math.Max(0, Math.Min(count, _lines.Count - boundedStart));
            var slice = _lines.Skip(boundedStart).Take(boundedCount).ToList();
            return Task.FromResult<IReadOnlyList<string>>(slice);
        }

        public Task<string> ReadLineAsync(string filePath, LineIndex index, int lineNumber, FileEncoding encoding, CancellationToken ct = default)
        {
            ReadLineCallCount++;
            if (lineNumber < 0 || lineNumber >= _lines.Count)
                return Task.FromResult(string.Empty);

            return Task.FromResult(_lines[lineNumber]);
        }

        private LineIndex CreateIndex(string filePath)
        {
            var index = new LineIndex
            {
                FilePath = filePath,
                FileSize = _lines.Count * 100
            };

            for (var i = 0; i < _lines.Count; i++)
                index.LineOffsets.Add(i * 100L);

            return index;
        }
    }

    private sealed class SequencedViewportReadLogReader : ILogReaderService
    {
        private readonly int _lineCount;
        private readonly TaskCompletionSource<bool> _firstBlockedReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _secondBlockedReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseFirstBlockedRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseSecondBlockedRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readLinesCallCount;

        public SequencedViewportReadLogReader(int lineCount = 200)
        {
            _lineCount = lineCount;
        }

        public Task FirstBlockedReadStarted => _firstBlockedReadStarted.Task;

        public Task SecondBlockedReadStarted => _secondBlockedReadStarted.Task;

        public void ReleaseFirstBlockedRead() => _releaseFirstBlockedRead.TrySetResult(true);

        public void ReleaseSecondBlockedRead() => _releaseSecondBlockedRead.TrySetResult(true);

        public Task<LineIndex> BuildIndexAsync(string filePath, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(CreateIndex(filePath));

        public Task<LineIndex> UpdateIndexAsync(string filePath, LineIndex existingIndex, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(existingIndex);

        public async Task<IReadOnlyList<string>> ReadLinesAsync(
            string filePath,
            LineIndex index,
            int startLine,
            int count,
            FileEncoding encoding,
            CancellationToken ct = default)
        {
            var callNumber = Interlocked.Increment(ref _readLinesCallCount);
            if (callNumber == 2)
            {
                _firstBlockedReadStarted.TrySetResult(true);
                await _releaseFirstBlockedRead.Task.WaitAsync(ct);
            }
            else if (callNumber == 3)
            {
                _secondBlockedReadStarted.TrySetResult(true);
                await _releaseSecondBlockedRead.Task.WaitAsync(ct);
            }

            var boundedStart = Math.Max(0, startLine);
            var boundedCount = Math.Max(0, Math.Min(count, _lineCount - boundedStart));
            return Enumerable.Range(boundedStart + 1, boundedCount)
                .Select(lineNumber => $"Line {lineNumber}")
                .ToList();
        }

        public Task<string> ReadLineAsync(string filePath, LineIndex index, int lineNumber, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult($"Line {lineNumber + 1}");

        private LineIndex CreateIndex(string filePath)
        {
            var index = new LineIndex
            {
                FilePath = filePath,
                FileSize = _lineCount * 100
            };

            for (var i = 0; i < _lineCount; i++)
                index.LineOffsets.Add(i * 100L);

            return index;
        }
    }

    private sealed class RetryableViewportFailureLogReader : ILogReaderService
    {
        private readonly int _lineCount;
        private bool _failNextTopRead;

        public RetryableViewportFailureLogReader(int lineCount = 200)
        {
            _lineCount = lineCount;
        }

        public void FailNextTopRead() => _failNextTopRead = true;

        public Task<LineIndex> BuildIndexAsync(string filePath, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(CreateIndex(filePath));

        public Task<LineIndex> UpdateIndexAsync(string filePath, LineIndex existingIndex, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(existingIndex);

        public Task<IReadOnlyList<string>> ReadLinesAsync(
            string filePath,
            LineIndex index,
            int startLine,
            int count,
            FileEncoding encoding,
            CancellationToken ct = default)
        {
            if (_failNextTopRead && startLine == 0)
            {
                _failNextTopRead = false;
                throw new IOException("simulated top-of-file read failure");
            }

            var boundedStart = Math.Max(0, startLine);
            var boundedCount = Math.Max(0, Math.Min(count, _lineCount - boundedStart));
            return Task.FromResult<IReadOnlyList<string>>(
                Enumerable.Range(boundedStart + 1, boundedCount)
                    .Select(lineNumber => $"Line {lineNumber}")
                    .ToList());
        }

        public Task<string> ReadLineAsync(string filePath, LineIndex index, int lineNumber, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult($"Line {lineNumber + 1}");

        private LineIndex CreateIndex(string filePath)
        {
            var index = new LineIndex
            {
                FilePath = filePath,
                FileSize = _lineCount * 100
            };

            for (var i = 0; i < _lineCount; i++)
                index.LineOffsets.Add(i * 100L);

            return index;
        }
    }

    private sealed class BlockingRestoreViewportAppendableLogReader : ILogReaderService
    {
        private readonly List<string> _lines;
        private readonly TaskCompletionSource<bool> _blockedRestoreReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseBlockedRestoreRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readLinesCallCount;

        public BlockingRestoreViewportAppendableLogReader(IEnumerable<string> initialLines)
        {
            _lines = initialLines.ToList();
        }

        public Task BlockedRestoreReadStarted => _blockedRestoreReadStarted.Task;

        public void ReleaseBlockedRestoreRead() => _releaseBlockedRestoreRead.TrySetResult(true);

        public void AppendLine(string line) => _lines.Add(line);

        public Task<LineIndex> BuildIndexAsync(string filePath, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(CreateIndex(filePath));

        public Task<LineIndex> UpdateIndexAsync(string filePath, LineIndex existingIndex, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(CreateIndex(filePath));

        public async Task<IReadOnlyList<string>> ReadLinesAsync(
            string filePath,
            LineIndex index,
            int startLine,
            int count,
            FileEncoding encoding,
            CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _readLinesCallCount) == 2)
            {
                _blockedRestoreReadStarted.TrySetResult(true);
                await _releaseBlockedRestoreRead.Task.WaitAsync(ct);
            }

            var boundedStart = Math.Max(0, startLine);
            var boundedCount = Math.Max(0, Math.Min(count, _lines.Count - boundedStart));
            var slice = _lines.Skip(boundedStart).Take(boundedCount).ToList();
            return slice;
        }

        public Task<string> ReadLineAsync(string filePath, LineIndex index, int lineNumber, FileEncoding encoding, CancellationToken ct = default)
        {
            if (lineNumber < 0 || lineNumber >= _lines.Count)
                return Task.FromResult(string.Empty);

            return Task.FromResult(_lines[lineNumber]);
        }

        private LineIndex CreateIndex(string filePath)
        {
            var index = new LineIndex
            {
                FilePath = filePath,
                FileSize = _lines.Count * 100
            };

            for (var i = 0; i < _lines.Count; i++)
                index.LineOffsets.Add(i * 100L);

            return index;
        }
    }

    [Fact]
    public async Task ResumeTailingWithCatchUp_AutoScroll_AppendsViewportInPlace()
    {
        var reader = new RecordingAppendableLogReader(
            Enumerable.Range(1, 60).Select(i => $"Line {i}"));
        var tab = new LogTabViewModel(
            "tab-1",
            @"C:\test\file.log",
            reader,
            new StubFileTailService(),
            new FileEncodingDetectionService(),
            new AppSettings());

        await tab.LoadAsync();
        Assert.Equal(60, tab.TotalLines);
        Assert.Equal(50, tab.VisibleLines.Count);
        Assert.Equal(11, tab.VisibleLines.First().LineNumber);
        Assert.Equal(60, tab.VisibleLines.Last().LineNumber);

        var requestCountAfterLoad = reader.ReadLinesRequests.Count;

        reader.AppendLine("Line 61");
        tab.SuspendTailing();
        await tab.ResumeTailingWithCatchUpAsync(pollingIntervalMs: 250);

        Assert.Equal(61, tab.TotalLines);
        Assert.Equal(50, tab.VisibleLines.Count);
        Assert.Equal(12, tab.VisibleLines.First().LineNumber);
        Assert.Equal(61, tab.VisibleLines.Last().LineNumber);
        Assert.Equal(11, tab.ScrollPosition);
        Assert.Equal(-1, tab.NavigateToLineNumber);

        var resumeRequests = reader.ReadLinesRequests.Skip(requestCountAfterLoad).ToList();
        Assert.Contains(resumeRequests, request => request.StartLine == 60 && request.Count == 1);
        Assert.DoesNotContain(resumeRequests, request => request.StartLine == 11 && request.Count == 50);
    }

    [Fact]
    public async Task RestoreFilterSnapshotAsync_WhenTailAppendsDuringInitialFilteredReload_FallsBackToFullReload()
    {
        var reader = new BlockingRestoreViewportAppendableLogReader(new[]
        {
            "INFO startup",
            "ERROR first",
            "INFO heartbeat",
            "ERROR second"
        });
        var tailService = new StubFileTailService();
        using var tab = new LogTabViewModel(
            "tab-restore",
            @"C:\test\file.log",
            reader,
            tailService,
            new FileEncodingDetectionService(),
            new AppSettings());

        await tab.LoadAsync();

        var restoreTask = tab.RestoreFilterSnapshotAsync(new LogFilterSession.FilterSnapshot
        {
            MatchingLineNumbers = new[] { 2, 4 },
            StatusText = "Filter active: 2 matching lines.",
            FilterRequest = new SearchRequest
            {
                Query = "ERROR",
                FilePaths = new List<string> { tab.FilePath },
                SourceMode = SearchRequestSourceMode.SnapshotAndTail
            },
            HasSeenParseableTimestamp = false
        });

        await reader.BlockedRestoreReadStarted.WaitAsync(TimeSpan.FromSeconds(5));

        reader.AppendLine("ERROR third");
        tailService.RaiseLinesAppended(tab.FilePath);
        reader.ReleaseBlockedRestoreRead();

        await restoreTask;

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((tab.TotalLines != 5 || tab.FilteredLineCount != 3 || !tab.VisibleLines.Select(line => line.LineNumber).SequenceEqual(new[] { 2, 4, 5 })) &&
               DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.Equal(5, tab.TotalLines);
        Assert.True(tab.IsFilterActive);
        Assert.Equal(3, tab.FilteredLineCount);
        Assert.Equal(new[] { 2, 4, 5 }, tab.VisibleLines.Select(line => line.LineNumber).ToArray());
        Assert.Equal(new[] { "ERROR first", "ERROR second", "ERROR third" }, tab.VisibleLines.Select(line => line.Text).ToArray());
    }

    [Fact]
    public async Task LinesAppended_AutoScroll_UpdatesViewportWithoutChangingNavigateTarget()
    {
        var reader = new RecordingAppendableLogReader(
            Enumerable.Range(1, 60).Select(i => $"Line {i}"));
        var tailService = new StubFileTailService();
        var tab = new LogTabViewModel(
            "tab-append",
            @"C:\test\file.log",
            reader,
            tailService,
            new FileEncodingDetectionService(),
            new AppSettings());

        await tab.LoadAsync();
        Assert.Equal(-1, tab.NavigateToLineNumber);

        reader.AppendLine("Line 61");
        tailService.RaiseLinesAppended(tab.FilePath);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (tab.TotalLines != 61 && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        Assert.Equal(61, tab.TotalLines);
        Assert.Equal(12, tab.VisibleLines.First().LineNumber);
        Assert.Equal(61, tab.VisibleLines.Last().LineNumber);
        Assert.Equal(11, tab.ScrollPosition);
        Assert.Equal(-1, tab.NavigateToLineNumber);
    }

    [Fact]
    public async Task ScrollBarProperties_WhenAutoScrollEnabled_StayPinnedWhileTrueViewportMoves()
    {
        var reader = new RecordingAppendableLogReader(
            Enumerable.Range(1, 60).Select(i => $"Line {i}"));
        var tailService = new StubFileTailService();
        var tab = new LogTabViewModel(
            "tab-scrollbar",
            @"C:\test\file.log",
            reader,
            tailService,
            new FileEncodingDetectionService(),
            new AppSettings());

        await tab.LoadAsync();

        Assert.True(tab.AutoScrollEnabled);
        Assert.Equal(1000, tab.ScrollBarValue);
        Assert.Equal(1000, tab.ScrollBarMaximum);
        Assert.Equal(100, tab.ScrollBarViewportSize);

        reader.AppendLine("Line 61");
        tailService.RaiseLinesAppended(tab.FilePath);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (tab.TotalLines != 61 && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        Assert.Equal(11, tab.ScrollPosition);
        Assert.Equal(1000, tab.ScrollBarValue);
        Assert.Equal(1000, tab.ScrollBarMaximum);
        Assert.Equal(100, tab.ScrollBarViewportSize);

        tab.AutoScrollEnabled = false;

        Assert.Equal(tab.ScrollPosition, tab.ScrollBarValue);
        Assert.Equal(tab.MaxScrollPosition, tab.ScrollBarMaximum);
        Assert.Equal(tab.ViewportLineCount, tab.ScrollBarViewportSize);
    }

    [Fact]
    public async Task LoadViewportAsync_WhenOlderRequestFinishesLast_KeepsNewerViewport()
    {
        var reader = new SequencedViewportReadLogReader();
        var tab = new LogTabViewModel(
            "tab-1",
            @"C:\test\file.log",
            reader,
            new StubFileTailService(),
            new FileEncodingDetectionService(),
            new AppSettings());

        await tab.LoadAsync();
        Assert.Equal(151, tab.VisibleLines.First().LineNumber);
        Assert.Equal(200, tab.VisibleLines.Last().LineNumber);

        var olderViewportTask = tab.LoadViewportAsync(0, 50);
        await reader.FirstBlockedReadStarted.WaitAsync(TimeSpan.FromSeconds(5));

        var newerViewportTask = tab.LoadViewportAsync(100, 50);
        await reader.SecondBlockedReadStarted.WaitAsync(TimeSpan.FromSeconds(5));

        reader.ReleaseSecondBlockedRead();
        var newerApplied = await newerViewportTask;

        reader.ReleaseFirstBlockedRead();
        var olderApplied = await olderViewportTask;

        Assert.True(newerApplied);
        Assert.False(olderApplied);
        Assert.Equal(101, tab.VisibleLines.First().LineNumber);
        Assert.Equal(150, tab.VisibleLines.Last().LineNumber);
    }

    [Fact]
    public async Task LoadViewportAsync_SmallForwardScroll_ReusesViewportAndReadsOnlyDelta()
    {
        var reader = new RecordingAppendableLogReader(
            Enumerable.Range(1, 200).Select(i => $"Line {i}"));
        var tab = new LogTabViewModel(
            "tab-1",
            @"C:\test\file.log",
            reader,
            new StubFileTailService(),
            new FileEncodingDetectionService(),
            new AppSettings());

        await tab.LoadAsync();
        await tab.LoadViewportAsync(120, 50);
        var requestCountAfterBaseline = reader.ReadLinesRequests.Count;

        var applied = await tab.LoadViewportAsync(123, 50);

        Assert.True(applied);
        var scrollRequests = reader.ReadLinesRequests.Skip(requestCountAfterBaseline).ToList();
        Assert.Single(scrollRequests);
        Assert.Equal((170, 3), scrollRequests[0]);
        Assert.Equal(124, tab.VisibleLines.First().LineNumber);
        Assert.Equal(173, tab.VisibleLines.Last().LineNumber);
        Assert.Equal("Line 124", tab.VisibleLines.First().Text);
        Assert.Equal("Line 173", tab.VisibleLines.Last().Text);
    }

    [Fact]
    public async Task LoadViewportAsync_SmallBackwardScroll_ReusesViewportAndReadsOnlyDelta()
    {
        var reader = new RecordingAppendableLogReader(
            Enumerable.Range(1, 200).Select(i => $"Line {i}"));
        var tab = new LogTabViewModel(
            "tab-1",
            @"C:\test\file.log",
            reader,
            new StubFileTailService(),
            new FileEncodingDetectionService(),
            new AppSettings());

        await tab.LoadAsync();
        await tab.LoadViewportAsync(120, 50);
        var requestCountAfterBaseline = reader.ReadLinesRequests.Count;

        var applied = await tab.LoadViewportAsync(117, 50);

        Assert.True(applied);
        var scrollRequests = reader.ReadLinesRequests.Skip(requestCountAfterBaseline).ToList();
        Assert.Single(scrollRequests);
        Assert.Equal((117, 3), scrollRequests[0]);
        Assert.Equal(118, tab.VisibleLines.First().LineNumber);
        Assert.Equal(167, tab.VisibleLines.Last().LineNumber);
        Assert.Equal("Line 118", tab.VisibleLines.First().Text);
        Assert.Equal("Line 167", tab.VisibleLines.Last().Text);
    }

    [Fact]
    public async Task JumpToTop_AfterFailedViewportAttempt_CanRetrySameRange()
    {
        var reader = new RetryableViewportFailureLogReader();
        var tab = new LogTabViewModel(
            "tab-1",
            @"C:\test\file.log",
            reader,
            new StubFileTailService(),
            new FileEncodingDetectionService(),
            new AppSettings());

        await tab.LoadAsync();
        Assert.Equal(151, tab.VisibleLines.First().LineNumber);
        Assert.Equal(200, tab.VisibleLines.Last().LineNumber);

        reader.FailNextTopRead();
        await tab.JumpToTopCommand.ExecuteAsync(null);

        Assert.Contains("Read error:", tab.StatusText, StringComparison.Ordinal);
        Assert.Equal(151, tab.VisibleLines.First().LineNumber);
        Assert.Equal(200, tab.VisibleLines.Last().LineNumber);

        await tab.JumpToTopCommand.ExecuteAsync(null);

        Assert.Equal(1, tab.VisibleLines.First().LineNumber);
        Assert.Equal(50, tab.VisibleLines.Last().LineNumber);
    }

    [Fact]
    public async Task ApplyFilterAsync_BatchesFilteredViewportReadsIntoContiguousRanges()
    {
        var reader = new RecordingAppendableLogReader(
            Enumerable.Range(1, 20).Select(i => $"Line {i}"));
        var tab = new LogTabViewModel(
            "tab-1",
            @"C:\test\file.log",
            reader,
            new StubFileTailService(),
            new FileEncodingDetectionService(),
            new AppSettings());

        await tab.LoadAsync();
        var requestCountAfterLoad = reader.ReadLinesRequests.Count;

        await tab.ApplyFilterAsync(
            matchingLineNumbers: new[] { 2, 3, 4, 10, 11, 20 },
            statusText: "Filter active: 6 matching lines.");

        var filterRequests = reader.ReadLinesRequests.Skip(requestCountAfterLoad).ToList();
        Assert.Equal(0, reader.ReadLineCallCount);
        Assert.Equal(3, filterRequests.Count);
        Assert.Equal((1, 3), filterRequests[0]);
        Assert.Equal((9, 2), filterRequests[1]);
        Assert.Equal((19, 1), filterRequests[2]);
        Assert.Equal(new[] { 2, 3, 4, 10, 11, 20 }, tab.VisibleLines.Select(line => line.LineNumber).ToArray());
    }
}
