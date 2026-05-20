namespace LogReader.Tests;

using LogReader.App.Services;

public class LogFilterSessionTests
{
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
        session.ApplyFilter(
            new[] { 2, 4 },
            "active",
            filterRequest: null,
            hasParseableTimestamps: false,
            totalLines: 6,
            lineSetMode: FilterLineSetMode.ExcludeMatching);

        var clone = LogFilterSession.CloneSnapshot(session.CaptureSnapshot()!);
        var restored = new LogFilterSession();
        restored.RestoreSnapshot(clone, totalLines: 6);

        Assert.Equal(FilterLineSetMode.ExcludeMatching, clone.LineSetMode);
        Assert.Equal(6, clone.TotalLinesAtSnapshot);
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
}
