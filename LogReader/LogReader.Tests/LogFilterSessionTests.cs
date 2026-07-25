namespace LogReader.Tests;

using LogReader.App.Services;
using LogReader.Core;
using LogReader.Core.Models;

public class LogFilterSessionTests
{
    [Fact]
    public void ApplyFilter_SortedUniquePositiveInputPreservesOrderAndCount()
    {
        var session = new LogFilterSession();

        session.ApplyFilter(
            new[] { 2, 5, 9 },
            "active",
            filterRequest: null,
            hasParseableTimestamps: false,
            totalLines: 10);

        Assert.Equal(3, session.FilteredLineCount);
        Assert.Equal(new[] { 2, 5, 9 }, session.SnapshotFilteredLineNumbers);
    }

    [Fact]
    public void ApplyFilter_DiscardsNonPositiveValuesDuringFallbackNormalization()
    {
        var session = new LogFilterSession();

        session.ApplyFilter(
            new[] { 5, 0, -1, 2, 5 },
            "active",
            filterRequest: null,
            hasParseableTimestamps: false,
            totalLines: 10);

        Assert.Equal(new[] { 2, 5 }, session.SnapshotFilteredLineNumbers);
    }

    [Fact]
    public void IncludeMode_UsesMatchingLinesAsDisplayLines()
    {
        var session = new LogFilterSession();

        session.ApplyFilter(
            new[] { 5, 2, 2, 9 },
            "active",
            filterRequest: null,
            hasParseableTimestamps: false,
            totalLines: 10);

        Assert.Equal(3, session.DisplayLineCount);
        Assert.Equal(new[] { 2, 5, 9 }, session.GetDisplayLineNumbers(0, 10));
        Assert.Equal(1, session.GetDisplayIndexForLineNumber(5));
        Assert.Null(session.GetDisplayIndexForLineNumber(4));
        Assert.True(session.IsLineVisible(9));
    }

    [Fact]
    public void ExcludeMode_UsesMatchingLinesAsHiddenLines()
    {
        var session = new LogFilterSession();

        session.ApplyFilter(
            new[] { 2, 4, 8 },
            "active",
            filterRequest: null,
            hasParseableTimestamps: false,
            totalLines: 10,
            lineSetMode: FilterLineSetMode.ExcludeMatching);

        Assert.Equal(7, session.DisplayLineCount);
        Assert.Equal(new[] { 1, 3, 5, 6, 7, 9, 10 }, session.GetDisplayLineNumbers(0, 10));
        Assert.Equal(2, session.GetDisplayIndexForLineNumber(5));
        Assert.Null(session.GetDisplayIndexForLineNumber(4));
        Assert.False(session.IsLineVisible(8));
        Assert.True(session.IsLineVisible(9));
    }

    [Fact]
    public void CloneAndRestore_PreserveModeAndTotalLineCount()
    {
        var session = new LogFilterSession();
        var token = FileGenerationToken.Create(7, 701);
        var evidence = new FileScanGenerationEvidence(token, FileGenerationCorrelation.Current);
        session.ApplyFilter(
            new[] { 2, 4 },
            "active",
            filterRequest: null,
            hasParseableTimestamps: false,
            totalLines: 6,
            lineSetMode: FilterLineSetMode.ExcludeMatching,
            generationEvidence: evidence,
            correlatedTabInstanceId: "tab-1",
            correlatedSearchContentVersion: 3,
            evaluatedEncoding: FileEncoding.Utf16);

        var clone = LogFilterSession.CloneSnapshot(session.CaptureSnapshot()!);
        var restored = new LogFilterSession();
        restored.RestoreSnapshot(clone, totalLines: 6);

        Assert.Equal(FilterLineSetMode.ExcludeMatching, clone.LineSetMode);
        Assert.Equal(6, clone.TotalLinesAtSnapshot);
        Assert.Equal(evidence, clone.GenerationEvidence);
        Assert.Equal("tab-1", clone.CorrelatedTabInstanceId);
        Assert.Equal(3, clone.CorrelatedSearchContentVersion);
        Assert.Equal(FileEncoding.Utf16, clone.EvaluatedEncoding);
        Assert.Equal(4, restored.DisplayLineCount);
        Assert.Equal(new[] { 1, 3, 5, 6 }, restored.GetDisplayLineNumbers(0, 10));
    }

    [Fact]
    public void RestoreSnapshot_ExcludeMode_RebuildsStatusForCurrentDisplayCount()
    {
        var restored = new LogFilterSession();
        restored.RestoreSnapshot(
            new LogFilterSession.FilterSnapshot
            {
                MatchingLineNumbers = new[] { 2, 4 },
                LineSetMode = FilterLineSetMode.ExcludeMatching,
                TotalLinesAtSnapshot = 10,
                StatusText = "Filter active: 8 non-matching lines."
            },
            totalLines: 6);

        Assert.Equal(4, restored.DisplayLineCount);
        Assert.Equal("Filter active: 4 matching lines.", restored.ActiveFilterStatusText);
    }

    [Fact]
    public void RestoreSnapshot_DefensivelyNormalizesMalformedLineNumbers()
    {
        var restored = new LogFilterSession();
        restored.RestoreSnapshot(
            new LogFilterSession.FilterSnapshot
            {
                MatchingLineNumbers = new[] { 5, 0, 2, 2, 12 },
                LineSetMode = FilterLineSetMode.IncludeMatching,
                TotalLinesAtSnapshot = 10
            },
            totalLines: 10);

        Assert.Equal(new[] { 2, 5 }, restored.SnapshotFilteredLineNumbers);
    }

    [Fact]
    public async Task RestoreSnapshot_PausedExcludeAtZeroBoundaryKeepsAppendedLinesUnevaluated()
    {
        var restored = new LogFilterSession();
        restored.RestoreSnapshot(
            new LogFilterSession.FilterSnapshot
            {
                MatchingLineNumbers = Array.Empty<int>(),
                LineSetMode = FilterLineSetMode.ExcludeMatching,
                TotalLinesAtSnapshot = 0,
                LastEvaluatedLine = 0,
                IsTailEvaluationPaused = true,
                FilterRequest = new SearchRequest
                {
                    Query = "ERROR",
                    SourceMode = SearchRequestSourceMode.SnapshotAndTail
                }
            },
            totalLines: 2);
        var reads = 0;

        var update = await restored.ProcessAppendedLinesAsync(
            updatedLineCount: 3,
            lineIndex: CreateLineIndex(),
            effectiveEncoding: FileEncoding.Utf8,
            readLinesAsync: (_, _, _, _, _) =>
            {
                reads++;
                return Task.FromResult<IReadOnlyList<string>>(new[] { "INFO" });
            },
            retainedDisplayLineLimit: 10,
            ct: CancellationToken.None);

        Assert.Equal(0, restored.DisplayLineCount);
        Assert.Empty(restored.GetDisplayLineNumbers(0, 10));
        Assert.True(update.IsEvaluationPaused);
        Assert.Equal(0, update.EvaluatedThroughLine);
        Assert.Equal(0, reads);
        Assert.Equal(0, restored.CaptureSnapshot()!.TotalLinesAtSnapshot);
    }

    [Fact]
    public void ExcludeMode_DoesNotExpandLargeComplement()
    {
        var session = new LogFilterSession();
        session.ApplyFilter(
            new[] { 2, 1_000_000, 3_500_000 },
            "active",
            filterRequest: null,
            hasParseableTimestamps: false,
            totalLines: 3_500_000,
            lineSetMode: FilterLineSetMode.ExcludeMatching);

        Assert.Equal(3_499_997, session.DisplayLineCount);
        Assert.Equal(new[] { 1, 3, 4, 5 }, session.GetDisplayLineNumbers(0, 4));
        Assert.Equal(999_997, session.GetDisplayIndexForLineNumber(999_999));
        Assert.Null(session.GetDisplayIndexForLineNumber(1_000_000));
    }

    [Fact]
    public void ExcludeMode_SkipsLargeContiguousHiddenRun()
    {
        var session = new LogFilterSession();
        session.ApplyFilter(
            Enumerable.Range(2, 999_999).ToArray(),
            "active",
            filterRequest: null,
            hasParseableTimestamps: false,
            totalLines: 1_000_010,
            lineSetMode: FilterLineSetMode.ExcludeMatching);

        Assert.Equal(11, session.DisplayLineCount);
        Assert.Equal(new[] { 1_000_001, 1_000_002, 1_000_003, 1_000_004 }, session.GetDisplayLineNumbers(1, 4));
    }

    [Fact]
    public void ExcludeMode_FirstDisplayIndexAtOrAfterLineNumber_SkipsLargeHiddenRun()
    {
        var session = new LogFilterSession();
        session.ApplyFilter(
            Enumerable.Range(2, 999_999).ToArray(),
            "active",
            filterRequest: null,
            hasParseableTimestamps: false,
            totalLines: 1_000_010,
            lineSetMode: FilterLineSetMode.ExcludeMatching);

        var displayIndex = session.GetFirstDisplayIndexAtOrAfterLineNumber(2);

        Assert.Equal(1, displayIndex);
        Assert.Equal(1_000_001, session.GetDisplayLineNumberAt(displayIndex!.Value));
    }

    [Fact]
    public void ExcludeMode_DisplayWindowSpansHiddenRun()
    {
        var session = new LogFilterSession();
        session.ApplyFilter(
            new[] { 4, 5, 6 },
            "active",
            filterRequest: null,
            hasParseableTimestamps: false,
            totalLines: 10,
            lineSetMode: FilterLineSetMode.ExcludeMatching);

        Assert.Equal(new[] { 2, 3, 7, 8, 9 }, session.GetDisplayLineNumbers(1, 5));
    }

    [Fact]
    public async Task ProcessAppendedLines_ReadsLargeRangeInBoundedChunks()
    {
        var session = CreateTailFilterSession("ERROR", totalLines: 0);
        var readRequests = new List<(int StartLine, int Count)>();

        var result = await session.ProcessAppendedLinesAsync(
            updatedLineCount: 4_500,
            lineIndex: CreateLineIndex(),
            effectiveEncoding: FileEncoding.Utf8,
            readLinesAsync: (_, startLine, count, _, _) =>
            {
                readRequests.Add((startLine, count));
                return Task.FromResult<IReadOnlyList<string>>(
                    Enumerable.Range(startLine + 1, count)
                        .Select(line => line % 2 == 0 ? $"ERROR {line}" : $"line {line}")
                        .ToArray());
            },
            retainedDisplayLineLimit: 10,
            ct: CancellationToken.None);

        Assert.Equal(new[] { (0, 2_000), (2_000, 2_000), (4_000, 500) }, readRequests);
        Assert.Equal(2_250, result.AddedDisplayLineCount);
        Assert.Equal(10, result.AddedMatchingLines.Count);
        Assert.False(result.HasCompleteAddedMatchingLines);
        Assert.Equal(2_250, session.DisplayLineCount);
        Assert.Equal(new[] { 2, 4, 6, 8 }, session.GetDisplayLineNumbers(0, 4));
    }

    [Fact]
    public async Task ProcessAppendedLines_RetainsOnlyNewestDisplayLines()
    {
        var session = CreateTailFilterSession("ERROR", totalLines: 0);

        var result = await session.ProcessAppendedLinesAsync(
            updatedLineCount: 6,
            lineIndex: CreateLineIndex(),
            effectiveEncoding: FileEncoding.Utf8,
            readLinesAsync: (_, startLine, count, _, _) => Task.FromResult<IReadOnlyList<string>>(
                Enumerable.Range(startLine + 1, count)
                    .Select(line => $"ERROR {line}")
                    .ToArray()),
            retainedDisplayLineLimit: 3,
            ct: CancellationToken.None);

        Assert.Equal(6, result.AddedDisplayLineCount);
        Assert.Equal(new[] { 4, 5, 6 }, result.AddedMatchingLines.Select(line => line.LineNumber));
        Assert.Equal(new[] { "ERROR 4", "ERROR 5", "ERROR 6" }, result.AddedMatchingLines.Select(line => line.LineText));
        Assert.False(result.HasCompleteAddedMatchingLines);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, session.GetDisplayLineNumbers(0, 10));
    }

    [Fact]
    public async Task ProcessAppendedLines_ExcludeModePreservesVisibleLineSemantics()
    {
        var session = CreateTailFilterSession(
            "ERROR",
            totalLines: 2,
            matchingLineNumbers: new[] { 2 },
            lineSetMode: FilterLineSetMode.ExcludeMatching);

        var result = await session.ProcessAppendedLinesAsync(
            updatedLineCount: 6,
            lineIndex: CreateLineIndex(),
            effectiveEncoding: FileEncoding.Utf8,
            readLinesAsync: (_, _, _, _, _) => Task.FromResult<IReadOnlyList<string>>(
                new[] { "visible 3", "ERROR 4", "visible 5", "ERROR 6" }),
            retainedDisplayLineLimit: 10,
            ct: CancellationToken.None);

        Assert.Equal(2, result.AddedDisplayLineCount);
        Assert.True(result.HasCompleteAddedMatchingLines);
        Assert.Equal(new[] { 3, 5 }, result.AddedMatchingLines.Select(line => line.LineNumber));
        Assert.Equal(new[] { 1, 3, 5 }, session.GetDisplayLineNumbers(0, 10));
    }

    [Fact]
    public async Task ProcessAppendedLines_ObservesCancellationBetweenChunks()
    {
        var session = CreateTailFilterSession("ERROR", totalLines: 0);
        using var cts = new CancellationTokenSource();
        var readCount = 0;

        await Assert.ThrowsAsync<OperationCanceledException>(() => session.ProcessAppendedLinesAsync(
            updatedLineCount: 4_001,
            lineIndex: CreateLineIndex(),
            effectiveEncoding: FileEncoding.Utf8,
            readLinesAsync: (_, startLine, count, _, _) =>
            {
                readCount++;
                if (readCount == 1)
                    cts.Cancel();

                return Task.FromResult<IReadOnlyList<string>>(
                    Enumerable.Range(startLine + 1, count)
                        .Select(line => $"ERROR {line}")
                        .ToArray());
            },
            retainedDisplayLineLimit: 10,
            ct: cts.Token));

        Assert.Equal(1, readCount);
    }

    [Theory]
    [InlineData(FilterLineSetMode.IncludeMatching)]
    [InlineData(FilterLineSetMode.ExcludeMatching)]
    public async Task ProcessAppendedLines_RegexTimeoutPausesTailEvaluationAndRetainsViewport(
        FilterLineSetMode lineSetMode)
    {
        var session = new LogFilterSession();
        session.ApplyFilter(
            new[] { 1 },
            "active",
            SearchRequest.Create(
                @"(a+)+$",
                isRegex: true,
                caseSensitive: true,
                filePaths: new[] { @"C:\logs\a.log" },
                sourceMode: SearchRequestSourceMode.SnapshotAndTail,
                usage: SearchRequestUsage.FilterApply),
            hasParseableTimestamps: false,
            totalLines: 1,
            lineSetMode);
        var reads = 0;

        var firstUpdate = await ProcessAsync(updatedLineCount: 2);
        var secondUpdate = await ProcessAsync(updatedLineCount: 3);

        Assert.False(firstUpdate.HasChanges);
        Assert.False(secondUpdate.HasChanges);
        Assert.True(session.IsActive);
        Assert.Equal(new[] { 1 }, session.SnapshotFilteredLineNumbers);
        Assert.Equal(LogFilterSession.TailRegexTimeoutStatusText, session.ActiveFilterStatusText);
        Assert.Equal(LogFilterSession.TailRegexTimeoutStatusText, firstUpdate.StatusText);
        Assert.Equal(LogFilterSession.TailRegexTimeoutStatusText, secondUpdate.StatusText);
        Assert.Equal(1, reads);
        var snapshot = session.CaptureSnapshot();
        Assert.NotNull(snapshot);
        Assert.True(snapshot.IsTailEvaluationPaused);
        Assert.Equal(1, snapshot.LastEvaluatedLine);

        Task<LogFilterSession.FilterTailUpdateResult> ProcessAsync(int updatedLineCount)
            => session.ProcessAppendedLinesAsync(
                updatedLineCount,
                CreateLineIndex(),
                FileEncoding.Utf8,
                (_, _, _, _, _) =>
                {
                    reads++;
                    return Task.FromResult<IReadOnlyList<string>>(new[] { new string('a', 30) + "!" });
                },
                retainedDisplayLineLimit: 10,
                CancellationToken.None);
    }

    [Theory]
    [InlineData(FilterLineSetMode.IncludeMatching)]
    [InlineData(FilterLineSetMode.ExcludeMatching)]
    public async Task CaptureCloneRestore_AfterRegexTimeout_RemainsPausedWithoutReadingAppendedLines(
        FilterLineSetMode lineSetMode)
    {
        var session = new LogFilterSession();
        session.ApplyFilter(
            new[] { 1 },
            "active",
            SearchRequest.Create(
                @"(a+)+$",
                isRegex: true,
                caseSensitive: true,
                filePaths: new[] { @"C:\logs\a.log" },
                sourceMode: SearchRequestSourceMode.SnapshotAndTail,
                usage: SearchRequestUsage.FilterApply),
            hasParseableTimestamps: false,
            totalLines: 1,
            lineSetMode);
        await session.ProcessAppendedLinesAsync(
            2,
            CreateLineIndex(),
            FileEncoding.Utf8,
            (_, _, _, _, _) => Task.FromResult<IReadOnlyList<string>>(new[] { new string('a', 30) + "!" }),
            retainedDisplayLineLimit: 10,
            CancellationToken.None);

        var clone = LogFilterSession.CloneSnapshot(session.CaptureSnapshot()!);
        var restored = new LogFilterSession();
        restored.RestoreSnapshot(clone, totalLines: 2);
        var reads = 0;

        var update = await restored.ProcessAppendedLinesAsync(
            3,
            CreateLineIndex(),
            FileEncoding.Utf8,
            (_, _, _, _, _) =>
            {
                reads++;
                return Task.FromResult<IReadOnlyList<string>>(new[] { "aaaa" });
            },
            retainedDisplayLineLimit: 10,
            CancellationToken.None);

        Assert.True(clone.IsTailEvaluationPaused);
        Assert.False(update.HasChanges);
        Assert.Equal(0, reads);
        Assert.Equal(new[] { 1 }, restored.SnapshotFilteredLineNumbers);
        Assert.Equal(LogFilterSession.TailRegexTimeoutStatusText, restored.ActiveFilterStatusText);
    }

    [Fact]
    public async Task ApplyFilter_AfterRegexTimeoutResumesTailEvaluation()
    {
        var session = new LogFilterSession();
        session.ApplyFilter(
            Array.Empty<int>(),
            "active",
            SearchRequest.Create(
                @"(a+)+$",
                isRegex: true,
                caseSensitive: true,
                filePaths: new[] { @"C:\logs\a.log" },
                sourceMode: SearchRequestSourceMode.SnapshotAndTail,
                usage: SearchRequestUsage.FilterApply),
            hasParseableTimestamps: false,
            totalLines: 0);
        await session.ProcessAppendedLinesAsync(
            1,
            CreateLineIndex(),
            FileEncoding.Utf8,
            (_, _, _, _, _) => Task.FromResult<IReadOnlyList<string>>(new[] { new string('a', 30) + "!" }),
            retainedDisplayLineLimit: 10,
            CancellationToken.None);

        session.ApplyFilter(
            Array.Empty<int>(),
            "active",
            SearchRequest.Create(
                "ERROR",
                isRegex: false,
                caseSensitive: true,
                filePaths: new[] { @"C:\logs\a.log" },
                sourceMode: SearchRequestSourceMode.SnapshotAndTail,
                usage: SearchRequestUsage.FilterApply),
            hasParseableTimestamps: false,
            totalLines: 1);
        var recovered = await session.ProcessAppendedLinesAsync(
            2,
            CreateLineIndex(),
            FileEncoding.Utf8,
            (_, _, _, _, _) => Task.FromResult<IReadOnlyList<string>>(new[] { "ERROR recovered" }),
            retainedDisplayLineLimit: 10,
            CancellationToken.None);

        Assert.True(recovered.HasChanges);
        Assert.Equal(new[] { 2 }, session.SnapshotFilteredLineNumbers);
        Assert.NotEqual(LogFilterSession.TailRegexTimeoutStatusText, session.ActiveFilterStatusText);
    }

    private static LogFilterSession CreateTailFilterSession(
        string query,
        int totalLines,
        IReadOnlyList<int>? matchingLineNumbers = null,
        FilterLineSetMode lineSetMode = FilterLineSetMode.IncludeMatching)
    {
        var session = new LogFilterSession();
        session.ApplyFilter(
            matchingLineNumbers ?? Array.Empty<int>(),
            "active",
            SearchRequest.Create(
                query,
                isRegex: false,
                caseSensitive: false,
                filePaths: new[] { @"C:\logs\a.log" },
                sourceMode: SearchRequestSourceMode.SnapshotAndTail,
                usage: SearchRequestUsage.FilterApply),
            hasParseableTimestamps: false,
            totalLines,
            lineSetMode);
        return session;
    }

    private static LineIndex CreateLineIndex()
        => new() { FilePath = @"C:\logs\a.log", FileSize = 0 };
}
