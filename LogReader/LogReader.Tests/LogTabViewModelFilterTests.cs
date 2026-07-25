namespace LogReader.Tests;

using LogReader.App.Services;
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

        public FileGenerationToken GenerationToken { get; set; } = FileGenerationToken.Unknown;

        public bool ReturnShortRangeReads { get; set; }

        public void AppendLine(string line) => _lines.Add(line);

        public Task<LineIndex> BuildIndexAsync(string filePath, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(CreateIndex(filePath));

        public Task<LineIndex> UpdateIndexAsync(string filePath, LineIndex existingIndex, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(CreateIndex(filePath));

        public Task<IReadOnlyList<string>> ReadLinesAsync(string filePath, LineIndex index, int startLine, int count, FileEncoding encoding, CancellationToken ct = default)
        {
            var boundedStart = Math.Max(0, startLine);
            var boundedCount = Math.Max(0, Math.Min(count, _lines.Count - boundedStart));
            if (ReturnShortRangeReads && boundedCount > 0)
                boundedCount--;
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
                FileSize = _lines.Count * 100,
                GenerationToken = GenerationToken
            };

            for (var i = 0; i < _lines.Count; i++)
                index.LineOffsets.Add(i * 100L);

            return index;
        }
    }

    [Fact]
    public async Task RestoreFilterSnapshotAsync_StaleGenerationDoesNotReplaceActiveFilter()
    {
        var reader = new AppendableLogReaderStub(new[] { "INFO", "ERROR prior" });
        using var tab = new LogTabViewModel(
            "tab-stale",
            @"C:\test\file.log",
            reader,
            new StubFileTailService(),
            new FileEncodingDetectionService(),
            new AppSettings());
        await tab.LoadAsync();
        await tab.ApplyFilterAsync(new[] { 2 }, "prior filter");
        var staleToken = FileGenerationToken.Create(1, 99);
        var staleSnapshot = new LogFilterSession.FilterSnapshot
        {
            MatchingLineNumbers = new[] { 1 },
            TotalLinesAtSnapshot = 2,
            LastEvaluatedLine = 2,
            StatusText = "replacement filter",
            GenerationEvidence = new FileScanGenerationEvidence(
                staleToken,
                FileGenerationCorrelation.Stale),
            CorrelatedTabInstanceId = tab.TabInstanceId,
            CorrelatedSearchContentVersion = tab.SearchContentVersion,
            EvaluatedEncoding = tab.EffectiveEncoding
        };

        var restored = await tab.RestoreFilterSnapshotAsync(staleSnapshot);

        Assert.False(restored);
        Assert.True(tab.IsFilterActive);
        Assert.Equal(new[] { 2 }, tab.CaptureActiveFilterSnapshot()!.MatchingLineNumbers);
        Assert.Equal("prior filter", tab.ActiveFilterStatusText);
    }

    [Theory]
    [InlineData(FilterLineSetMode.IncludeMatching, 2)]
    [InlineData(FilterLineSetMode.ExcludeMatching, 1)]
    public async Task TryCommitFilterSnapshotAsync_CatchesUpFromEvaluatedBoundary(
        FilterLineSetMode lineSetMode,
        int expectedDisplayLineCount)
    {
        var reader = new AppendableLogReaderStub(new[]
        {
            "INFO startup",
            "ERROR scanned",
            "ERROR appended-before-commit"
        });
        using var tab = new LogTabViewModel(
            "tab-boundary",
            @"C:\test\file.log",
            reader,
            new StubFileTailService(),
            new FileEncodingDetectionService(),
            new AppSettings());
        await tab.LoadAsync();
        var request = new SearchRequest
        {
            Query = "ERROR",
            FilePaths = new List<string> { tab.FilePath },
            SourceMode = SearchRequestSourceMode.SnapshotAndTail
        };
        var snapshot = new LogFilterSession.FilterSnapshot
        {
            MatchingLineNumbers = new[] { 2 },
            LineSetMode = lineSetMode,
            TotalLinesAtSnapshot = 2,
            LastEvaluatedLine = 2,
            StatusText = "Filter active: 1 matching lines.",
            FilterRequest = request,
            GenerationEvidence = FileScanGenerationEvidence.Unknown,
            CorrelatedTabInstanceId = tab.TabInstanceId,
            CorrelatedSearchContentVersion = tab.SearchContentVersion,
            EvaluatedEncoding = tab.EffectiveEncoding
        };

        var committed = await tab.TryCommitFilterSnapshotAsync(snapshot);

        Assert.True(committed);
        var committedSnapshot = tab.CaptureActiveFilterSnapshot();
        Assert.NotNull(committedSnapshot);
        Assert.Equal(new[] { 2, 3 }, committedSnapshot!.MatchingLineNumbers);
        Assert.Equal(3, committedSnapshot.LastEvaluatedLine);
        Assert.Equal(expectedDisplayLineCount, tab.DisplayLineCount);
    }

    [Fact]
    public async Task TryCommitFilterSnapshotAsync_ShortCatchUpReadRejectsIncompleteFilter()
    {
        var reader = new RecordingAppendableFilterLogReaderStub(new[]
        {
            "INFO startup",
            "ERROR scanned",
            "ERROR appended-before-commit"
        })
        {
            ReturnShortRangeReads = true
        };
        using var tab = new LogTabViewModel(
            "tab-short-catch-up",
            @"C:\test\file.log",
            reader,
            new StubFileTailService(),
            new FileEncodingDetectionService(),
            new AppSettings());
        await tab.LoadAsync();
        await tab.ApplyFilterAsync(new[] { 1 }, "prior filter");
        var snapshot = new LogFilterSession.FilterSnapshot
        {
            MatchingLineNumbers = new[] { 2 },
            TotalLinesAtSnapshot = 2,
            LastEvaluatedLine = 2,
            StatusText = "replacement filter",
            FilterRequest = new SearchRequest
            {
                Query = "ERROR",
                FilePaths = new List<string> { tab.FilePath },
                SourceMode = SearchRequestSourceMode.SnapshotAndTail
            },
            CorrelatedTabInstanceId = tab.TabInstanceId,
            CorrelatedSearchContentVersion = tab.SearchContentVersion,
            EvaluatedEncoding = tab.EffectiveEncoding
        };

        await Assert.ThrowsAsync<IOException>(() => tab.TryCommitFilterSnapshotAsync(snapshot));

        Assert.True(tab.IsFilterActive);
        Assert.Equal(new[] { 1 }, tab.CaptureActiveFilterSnapshot()!.MatchingLineNumbers);
        Assert.Equal("prior filter", tab.ActiveFilterStatusText);
    }

    [Fact]
    public async Task ActiveFilter_LateGenerationMismatchClearsSnapshot()
    {
        var scannedToken = FileGenerationToken.Create(11, 101);
        var reader = new RecordingAppendableFilterLogReaderStub(new[] { "ERROR" });
        using var tab = new LogTabViewModel(
            "tab-late-generation",
            @"C:\test\file.log",
            reader,
            new StubFileTailService(),
            new FileEncodingDetectionService(),
            new AppSettings());
        await tab.LoadAsync();
        var snapshot = new LogFilterSession.FilterSnapshot
        {
            MatchingLineNumbers = new[] { 1 },
            TotalLinesAtSnapshot = 1,
            LastEvaluatedLine = 1,
            StatusText = "Filter active: 1 matching lines.",
            GenerationEvidence = new FileScanGenerationEvidence(
                scannedToken,
                FileGenerationCorrelation.Current),
            CorrelatedTabInstanceId = tab.TabInstanceId,
            CorrelatedSearchContentVersion = tab.SearchContentVersion,
            EvaluatedEncoding = tab.EffectiveEncoding
        };

        Assert.True(await tab.TryCommitFilterSnapshotAsync(snapshot));
        Assert.True(tab.IsFilterActive);

        reader.GenerationToken = FileGenerationToken.Create(11, 102);
        await tab.UpdateLineIndexLineCountAsync(CancellationToken.None);
        await WaitForConditionAsync(() => !tab.IsFilterActive);

        Assert.Null(tab.CaptureActiveFilterSnapshot());
    }

    [Fact]
    public async Task TryCommitFilterSnapshotAsync_AdvancesIndexToEvaluationBoundaryBeforeCommit()
    {
        var reader = new AppendableLogReaderStub(new[] { "INFO startup", "ERROR scanned" });
        using var tab = new LogTabViewModel(
            "tab-index-behind",
            @"C:\test\file.log",
            reader,
            new StubFileTailService(),
            new FileEncodingDetectionService(),
            new AppSettings());
        await tab.LoadAsync();
        Assert.Equal(2, tab.TotalLines);
        reader.AppendLine("ERROR scanned-before-index-update");
        var snapshot = new LogFilterSession.FilterSnapshot
        {
            MatchingLineNumbers = new[] { 2, 3 },
            TotalLinesAtSnapshot = 3,
            LastEvaluatedLine = 3,
            StatusText = "Filter active: 2 matching lines.",
            FilterRequest = new SearchRequest
            {
                Query = "ERROR",
                FilePaths = new List<string> { tab.FilePath },
                SourceMode = SearchRequestSourceMode.SnapshotAndTail
            },
            CorrelatedTabInstanceId = tab.TabInstanceId,
            CorrelatedSearchContentVersion = tab.SearchContentVersion,
            EvaluatedEncoding = tab.EffectiveEncoding
        };

        var committed = await tab.TryCommitFilterSnapshotAsync(snapshot);

        Assert.True(committed);
        Assert.Equal(3, tab.TotalLines);
        Assert.Equal(new[] { 2, 3 }, tab.CaptureActiveFilterSnapshot()!.MatchingLineNumbers);
    }

    [Fact]
    public async Task TryCommitFilterSnapshotAsync_ZeroBoundaryCatchesUpFirstLine()
    {
        var reader = new AppendableLogReaderStub(new[] { "ERROR first" });
        using var tab = new LogTabViewModel(
            "tab-zero-boundary",
            @"C:\test\file.log",
            reader,
            new StubFileTailService(),
            new FileEncodingDetectionService(),
            new AppSettings());
        await tab.LoadAsync();
        var snapshot = new LogFilterSession.FilterSnapshot
        {
            MatchingLineNumbers = Array.Empty<int>(),
            TotalLinesAtSnapshot = 0,
            LastEvaluatedLine = 0,
            StatusText = "Filter active: 0 matching lines.",
            FilterRequest = new SearchRequest
            {
                Query = "ERROR",
                FilePaths = new List<string> { tab.FilePath },
                SourceMode = SearchRequestSourceMode.SnapshotAndTail
            },
            CorrelatedTabInstanceId = tab.TabInstanceId,
            CorrelatedSearchContentVersion = tab.SearchContentVersion,
            EvaluatedEncoding = tab.EffectiveEncoding
        };

        var committed = await tab.TryCommitFilterSnapshotAsync(snapshot);

        Assert.True(committed);
        var committedSnapshot = tab.CaptureActiveFilterSnapshot();
        Assert.Equal(new[] { 1 }, committedSnapshot!.MatchingLineNumbers);
        Assert.Equal(1, committedSnapshot.LastEvaluatedLine);
    }

    [Fact]
    public async Task TryCommitFilterSnapshotAsync_PausedExcludePreservesExplicitZeroBoundary()
    {
        var reader = new AppendableLogReaderStub(new[] { "INFO appended", "ERROR appended" });
        using var tab = new LogTabViewModel(
            "tab-paused-zero-boundary",
            @"C:\test\file.log",
            reader,
            new StubFileTailService(),
            new FileEncodingDetectionService(),
            new AppSettings());
        await tab.LoadAsync();
        var snapshot = new LogFilterSession.FilterSnapshot
        {
            MatchingLineNumbers = Array.Empty<int>(),
            LineSetMode = FilterLineSetMode.ExcludeMatching,
            TotalLinesAtSnapshot = 0,
            IsTailEvaluationPaused = true,
            StatusText = LogFilterSession.TailRegexTimeoutStatusText,
            FilterRequest = new SearchRequest
            {
                Query = "ERROR",
                FilePaths = new List<string> { tab.FilePath },
                SourceMode = SearchRequestSourceMode.SnapshotAndTail
            },
            CorrelatedTabInstanceId = tab.TabInstanceId,
            CorrelatedSearchContentVersion = tab.SearchContentVersion,
            EvaluatedEncoding = tab.EffectiveEncoding
        };

        var committed = await tab.TryCommitFilterSnapshotAsync(snapshot);

        Assert.True(committed);
        Assert.Equal(0, tab.DisplayLineCount);
        var committedSnapshot = tab.CaptureActiveFilterSnapshot();
        Assert.NotNull(committedSnapshot);
        Assert.Equal(0, committedSnapshot!.TotalLinesAtSnapshot);
        Assert.Equal(0, committedSnapshot.LastEvaluatedLine);
        Assert.True(committedSnapshot.IsTailEvaluationPaused);
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
    public async Task ResumeTailingWithTimeOnlyFilter_CatchUpMergesInRangeTimestampedLines()
    {
        var reader = new AppendableLogReaderStub(new[]
        {
            "2026-03-09T19:49:10Z INFO startup",
            "2026-03-09T19:49:16Z INFO initial"
        });
        var tab = new LogTabViewModel(
            "tab-time-only",
            @"C:\test\file.log",
            reader,
            new StubFileTailService(),
            new FileEncodingDetectionService(),
            new AppSettings());

        await tab.LoadAsync();
        var filterRequest = new SearchRequest
        {
            Query = string.Empty,
            FilePaths = new List<string> { tab.FilePath },
            SourceMode = SearchRequestSourceMode.SnapshotAndTail,
            Usage = SearchRequestUsage.FilterApply,
            FromTimestamp = "2026-03-09T19:49:15Z",
            ToTimestamp = "2026-03-09T19:49:20Z"
        };

        await tab.ApplyFilterAsync(
            matchingLineNumbers: new[] { 2 },
            statusText: "Filter active: 1 matching lines.",
            filterRequest: filterRequest,
            hasParseableTimestamps: true);

        reader.AppendLine("2026-03-09T19:49:25Z INFO outside");
        reader.AppendLine("INFO no timestamp");
        reader.AppendLine("2026-03-09T19:49:18Z WARN inside");

        tab.SuspendTailing();
        await tab.ResumeTailingWithCatchUpAsync(pollingIntervalMs: 250);

        Assert.Equal(5, tab.TotalLines);
        Assert.True(tab.IsFilterActive);
        Assert.Equal(2, tab.FilteredLineCount);
        Assert.Equal(new[] { 2, 5 }, tab.VisibleLines.Select(l => l.LineNumber).ToArray());
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

    private static async Task WaitForConditionAsync(Func<bool> condition)
    {
        var timeoutAt = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeoutAt)
                throw new TimeoutException("Condition was not met before the test timeout.");

            await Task.Delay(10);
        }
    }

}
