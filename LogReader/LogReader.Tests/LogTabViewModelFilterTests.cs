namespace LogReader.Tests;

using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

public class LogTabViewModelFilterTests
{
    private sealed class RecordingAppendableFilterLogReaderStub : ILogReaderService
    {
        private readonly List<string> _lines;

        public RecordingAppendableFilterLogReaderStub(IEnumerable<string> initialLines)
        {
            _lines = initialLines.ToList();
        }

        public int ReadLineCallCount { get; private set; }

        public void AppendLine(string line) => _lines.Add(line);

        public Task<LineIndex> BuildIndexAsync(string filePath, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(CreateIndex(filePath));

        public Task<LineIndex> UpdateIndexAsync(string filePath, LineIndex existingIndex, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(CreateIndex(filePath));

        public Task<IReadOnlyList<string>> ReadLinesAsync(string filePath, LineIndex index, int startLine, int count, FileEncoding encoding, CancellationToken ct = default)
        {
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

    private sealed class AppendableLogReaderStub : ILogReaderService
    {
        private readonly List<string> _lines;

        public AppendableLogReaderStub(IEnumerable<string> initialLines)
        {
            _lines = initialLines.ToList();
        }

        public void AppendLine(string line) => _lines.Add(line);

        public Task<LineIndex> BuildIndexAsync(string filePath, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(CreateIndex(filePath));

        public Task<LineIndex> UpdateIndexAsync(string filePath, LineIndex existingIndex, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(CreateIndex(filePath));

        public Task<IReadOnlyList<string>> ReadLinesAsync(string filePath, LineIndex index, int startLine, int count, FileEncoding encoding, CancellationToken ct = default)
        {
            var boundedStart = Math.Max(0, startLine);
            var boundedCount = Math.Max(0, Math.Min(count, _lines.Count - boundedStart));
            var slice = _lines.Skip(boundedStart).Take(boundedCount).ToList();
            return Task.FromResult<IReadOnlyList<string>>(slice);
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
    public async Task ResumeTailingWithFilter_CatchUpMergesMatchingAppendedLines()
    {
        var reader = new AppendableLogReaderStub(new[]
        {
            "INFO startup",
            "ERROR first"
        });
        var tab = new LogTabViewModel(
            "tab-1",
            @"C:\test\file.log",
            reader,
            new StubFileTailService(),
            new FileEncodingDetectionService(),
            new AppSettings());

        await tab.LoadAsync();
        Assert.Equal(2, tab.TotalLines);

        var filterRequest = new SearchRequest
        {
            Query = "ERROR",
            CaseSensitive = false,
            FilePaths = new List<string> { tab.FilePath },
            SourceMode = SearchRequestSourceMode.SnapshotAndTail
        };

        await tab.ApplyFilterAsync(
            matchingLineNumbers: new[] { 2 },
            statusText: "Filter active: 1 matching lines.",
            filterRequest: filterRequest,
            hasParseableTimestamps: false);
        Assert.True(tab.IsFilterActive);
        Assert.Equal(1, tab.FilteredLineCount);
        var navigateTargetBeforeResume = tab.NavigateToLineNumber;

        reader.AppendLine("INFO heartbeat");
        reader.AppendLine("ERROR second");

        tab.SuspendTailing();
        await tab.ResumeTailingWithCatchUpAsync(pollingIntervalMs: 250);

        Assert.Equal(4, tab.TotalLines);
        Assert.True(tab.IsFilterActive);
        Assert.Equal(2, tab.FilteredLineCount);
        Assert.Equal(new[] { 2, 4 }, tab.VisibleLines.Select(l => l.LineNumber).ToArray());
        Assert.Equal(navigateTargetBeforeResume, tab.NavigateToLineNumber);
        Assert.Contains("tailing", tab.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResumeTailingWithFilter_CatchUpAppendsInPlaceWithoutReloadingFilteredViewport()
    {
        var reader = new RecordingAppendableFilterLogReaderStub(new[]
        {
            "INFO startup",
            "ERROR first",
            "INFO heartbeat",
            "ERROR second"
        });
        var tab = new LogTabViewModel(
            "tab-2",
            @"C:\test\file.log",
            reader,
            new StubFileTailService(),
            new FileEncodingDetectionService(),
            new AppSettings());

        await tab.LoadAsync();
        var filterRequest = new SearchRequest
        {
            Query = "ERROR",
            CaseSensitive = false,
            FilePaths = new List<string> { tab.FilePath },
            SourceMode = SearchRequestSourceMode.SnapshotAndTail
        };

        await tab.ApplyFilterAsync(
            matchingLineNumbers: new[] { 2, 4 },
            statusText: "Filter active: 2 matching lines.",
            filterRequest: filterRequest,
            hasParseableTimestamps: false);
        var navigateTargetBeforeResume = tab.NavigateToLineNumber;

        var readLineCallCountBeforeResume = reader.ReadLineCallCount;

        reader.AppendLine("INFO trailing");
        reader.AppendLine("ERROR third");
        tab.SuspendTailing();
        await tab.ResumeTailingWithCatchUpAsync(pollingIntervalMs: 250);

        Assert.Equal(3, tab.FilteredLineCount);
        Assert.Equal(new[] { 2, 4, 6 }, tab.VisibleLines.Select(l => l.LineNumber).ToArray());
        Assert.Equal(navigateTargetBeforeResume, tab.NavigateToLineNumber);
        Assert.Equal(readLineCallCountBeforeResume, reader.ReadLineCallCount);
    }

    [Fact]
    public async Task ApplyFilterAsync_WhenAutoScrollEnabled_LoadsFilteredViewportAtBottomAndKeepsFollowing()
    {
        var reader = new AppendableLogReaderStub(
            Enumerable.Range(1, 60).Select(i => $"ERROR {i}"));
        var tab = new LogTabViewModel(
            "tab-3",
            @"C:\test\file.log",
            reader,
            new StubFileTailService(),
            new FileEncodingDetectionService(),
            new AppSettings());

        await tab.LoadAsync();
        var filterRequest = new SearchRequest
        {
            Query = "ERROR",
            CaseSensitive = false,
            FilePaths = new List<string> { tab.FilePath },
            SourceMode = SearchRequestSourceMode.SnapshotAndTail
        };

        await tab.ApplyFilterAsync(
            matchingLineNumbers: Enumerable.Range(1, 60).ToArray(),
            statusText: "Filter active: 60 matching lines.",
            filterRequest: filterRequest,
            hasParseableTimestamps: false);

        Assert.True(tab.AutoScrollEnabled);
        Assert.Equal(tab.MaxScrollPosition, tab.ScrollPosition);
        Assert.Equal(11, tab.VisibleLines.First().LineNumber);
        Assert.Equal(60, tab.VisibleLines.Last().LineNumber);

        reader.AppendLine("ERROR 61");
        tab.SuspendTailing();
        await tab.ResumeTailingWithCatchUpAsync(pollingIntervalMs: 250);

        Assert.Equal(61, tab.TotalLines);
        Assert.Equal(61, tab.FilteredLineCount);
        Assert.Equal(tab.MaxScrollPosition, tab.ScrollPosition);
        Assert.Equal(12, tab.VisibleLines.First().LineNumber);
        Assert.Equal(61, tab.VisibleLines.Last().LineNumber);
    }

    [Fact]
    public async Task ResumeTailingWithFilter_WhenAutoScrollDisabled_DoesNotMoveViewportEvenAtBottom()
    {
        var reader = new AppendableLogReaderStub(
            Enumerable.Range(1, 60).Select(i => $"ERROR {i}"));
        var tab = new LogTabViewModel(
            "tab-4",
            @"C:\test\file.log",
            reader,
            new StubFileTailService(),
            new FileEncodingDetectionService(),
            new AppSettings());

        await tab.LoadAsync();
        tab.AutoScrollEnabled = false;
        var filterRequest = new SearchRequest
        {
            Query = "ERROR",
            CaseSensitive = false,
            FilePaths = new List<string> { tab.FilePath },
            SourceMode = SearchRequestSourceMode.SnapshotAndTail
        };

        await tab.ApplyFilterAsync(
            matchingLineNumbers: Enumerable.Range(1, 60).ToArray(),
            statusText: "Filter active: 60 matching lines.",
            filterRequest: filterRequest,
            hasParseableTimestamps: false);
        await tab.LoadViewportAsync(tab.MaxScrollPosition, tab.ViewportLineCount);

        var visibleBeforeResume = tab.VisibleLines.Select(line => line.LineNumber).ToArray();
        var scrollPositionBeforeResume = tab.ScrollPosition;

        reader.AppendLine("ERROR 61");
        tab.SuspendTailing();
        await tab.ResumeTailingWithCatchUpAsync(pollingIntervalMs: 250);

        Assert.Equal(61, tab.TotalLines);
        Assert.Equal(61, tab.FilteredLineCount);
        Assert.Equal(scrollPositionBeforeResume, tab.ScrollPosition);
        Assert.Equal(visibleBeforeResume, tab.VisibleLines.Select(line => line.LineNumber).ToArray());
    }

}
