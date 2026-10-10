namespace LogReader.Core.Tests;

using LogReader.Core.Models;
using LogReader.Mcp;

public sealed partial class HeadlessLogQueryBackendTests
{
    [Fact]
    public async Task DisplayName_McpQueriesReportCustomNameAlongsideStableId()
    {
        var path = await CreateFileAsync("named.log", "needle one\nneedle two");
        var original = CreateSnapshot(("file", path));
        var named = new ConfiguredLogCatalogSnapshot(1, original.Groups,
            original.Files.Select(file => file with { DisplayName = "Production API" }));
        using var backend = CreateBackend(named);
        var tree = await backend.ListLogTreeAsync(new());
        Assert.Equal("Production API", tree.Result!.Nodes.Single(node => node.Id == "file").DisplayName);
        var targets = new[] { new ConfiguredLogTarget(ConfiguredLogTargetKind.LogFile, "file") };
        var search = await backend.SearchLogsAsync(new LogSearchQuery { Targets = targets, Query = "needle" });
        Assert.Equal("Production API", Assert.Single(search.Result!.Files).DisplayName);
        var count = await backend.CountLogsAsync(new LogCountQuery { Targets = targets, Query = "needle" });
        Assert.Equal("Production API", Assert.Single(count.Result!.Files).DisplayName);
        var lines = await backend.ReadLogLinesAsync(new LogReadLinesQuery { FileId = "file", Count = 1 });
        Assert.Equal("Production API", lines.Result!.File!.DisplayName);
        var tail = await backend.ReadLogTailAsync(new LogReadTailQuery { FileId = "file", MaxLines = 1 });
        Assert.Equal("Production API", tail.Result!.File!.DisplayName);
        var tools = new McpLogTools(backend);
        var response = await tools.SearchLogsAsync(targets, "needle");
        var record = response.StructuredContent!.Value.GetProperty("result").GetProperty("files")[0];
        Assert.Equal("file", record.GetProperty("fileId").GetString());
        Assert.Equal("Production API", record.GetProperty("displayName").GetString());
        Assert.DoesNotContain(path, response.StructuredContent.Value.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DisplayName_RenameInvalidatesExistingSearchContinuation()
    {
        var path = await CreateFileAsync("rename-cursor.log", "needle\nneedle");
        var catalog = new MutableCatalogReader(CreateSnapshot(("file", path)));
        using var backend = CreateBackend(catalog.Snapshot, catalogReader: catalog,
            limits: LogQueryEffectiveLimits.Default with { SearchScanBytes = 3 });
        LogSearchQuery Query(string? cursor) => new()
        {
            Targets = [new(ConfiguredLogTargetKind.LogFile, "file")], Query = "needle", Cursor = cursor
        };
        var first = await backend.SearchLogsAsync(Query(null));
        var cursor = Assert.IsType<string>(first.Result!.NextCursor);
        var original = catalog.Snapshot;
        catalog.Snapshot = new ConfiguredLogCatalogSnapshot(1, original.Groups,
            original.Files.Select(file => file with { DisplayName = "New name" }));
        var resumed = await backend.SearchLogsAsync(Query(cursor));
        Assert.Equal("stale_search_cursor", Assert.Single(resumed.Errors).Code);
    }
}
