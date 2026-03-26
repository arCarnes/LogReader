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
            FilePaths = new List<string> { tab.FilePath }
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
            FilePaths = new List<string> { tab.FilePath }
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
    public async Task ResumeTailingWithWholeWordFilter_UsesSameUnderscoreBoundariesAsSearch()
    {
        var reader = new AppendableLogReaderStub(new[]
        {
            "error"
        });
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
            Query = "error",
            CaseSensitive = false,
            WholeWord = true,
            FilePaths = new List<string> { tab.FilePath }
        };

        await tab.ApplyFilterAsync(
            matchingLineNumbers: new[] { 1 },
            statusText: "Filter active: 1 matching lines.",
            filterRequest: filterRequest,
            hasParseableTimestamps: false);

        reader.AppendLine("error_code");
        reader.AppendLine("error.code");
        reader.AppendLine("prefix_error_suffix");
        reader.AppendLine("error");

        tab.SuspendTailing();
        await tab.ResumeTailingWithCatchUpAsync(pollingIntervalMs: 250);

        Assert.Equal(3, tab.FilteredLineCount);
        Assert.Equal(new[] { 1, 3, 5 }, tab.VisibleLines.Select(line => line.LineNumber).ToArray());
    }
}
