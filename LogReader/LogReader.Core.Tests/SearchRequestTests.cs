namespace LogReader.Core.Tests;

using LogReader.Core.Models;

public class SearchRequestTests
{
    [Fact]
    public void Clone_DeepCopiesMutableCollections()
    {
        var request = new SearchRequest
        {
            Query = "error",
            IsRegex = true,
            CaseSensitive = true,
            FilePaths = new List<string> { @"C:\logs\a.log" },
            AllowedLineNumbersByFilePath = new Dictionary<string, IReadOnlyList<int>>(StringComparer.OrdinalIgnoreCase)
            {
                [@"C:\logs\a.log"] = new List<int> { 1, 2 }
            },
            StartLineNumber = 1,
            EndLineNumber = 2,
            FromTimestamp = "2026-04-28",
            ToTimestamp = "2026-04-29",
            SourceMode = SearchRequestSourceMode.SnapshotAndTail,
            Usage = SearchRequestUsage.FilterApply,
            MaxHitsPerFile = 25,
            MaxRetainedLineTextLength = 100
        };

        var clone = request.Clone();
        request.FilePaths.Add(@"C:\logs\b.log");
        ((List<int>)request.AllowedLineNumbersByFilePath[@"C:\logs\a.log"]).Add(3);

        Assert.Equal(new[] { @"C:\logs\a.log" }, clone.FilePaths);
        Assert.Equal(new[] { 1, 2 }, clone.AllowedLineNumbersByFilePath[@"C:\LOGS\A.LOG"]);
        Assert.Equal(request.Query, clone.Query);
        Assert.Equal(request.SourceMode, clone.SourceMode);
        Assert.Equal(request.Usage, clone.Usage);
        Assert.Equal(request.MaxHitsPerFile, clone.MaxHitsPerFile);
        Assert.Equal(request.MaxRetainedLineTextLength, clone.MaxRetainedLineTextLength);
    }

    [Fact]
    public void Create_NormalizesOptionalTimestampTextAndCopiesAllowedLines()
    {
        var allowed = new Dictionary<string, IReadOnlyList<int>>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\logs\a.log"] = new List<int> { 10, 20 }
        };

        var request = SearchRequest.Create(
            "needle",
            isRegex: false,
            caseSensitive: true,
            new[] { @"C:\logs\a.log" },
            SearchRequestSourceMode.Tail,
            SearchRequestUsage.DiskSearch,
            fromTimestamp: " 2026-04-28 ",
            toTimestamp: " ",
            allowedLineNumbersByFilePath: allowed,
            startLineNumber: 10,
            endLineNumber: 20);

        ((List<int>)allowed[@"C:\logs\a.log"]).Add(30);

        Assert.Equal("2026-04-28", request.FromTimestamp);
        Assert.Null(request.ToTimestamp);
        Assert.Equal(new[] { 10, 20 }, request.AllowedLineNumbersByFilePath[@"C:\LOGS\A.LOG"]);
        Assert.Equal(10, request.StartLineNumber);
        Assert.Equal(20, request.EndLineNumber);
    }

    [Fact]
    public void Create_CanPreserveAllowedLineListReferences()
    {
        IReadOnlyList<int> lines = new List<int> { 10, 20 };
        var allowed = new Dictionary<string, IReadOnlyList<int>>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\logs\a.log"] = lines
        };

        var request = SearchRequest.Create(
            "needle",
            isRegex: false,
            caseSensitive: true,
            new[] { @"C:\logs\a.log" },
            SearchRequestSourceMode.DiskSnapshot,
            SearchRequestUsage.DiskSearch,
            allowedLineNumbersByFilePath: allowed,
            cloneAllowedLineNumbers: false);

        Assert.Same(lines, request.AllowedLineNumbersByFilePath[@"C:\LOGS\A.LOG"]);
    }
}
