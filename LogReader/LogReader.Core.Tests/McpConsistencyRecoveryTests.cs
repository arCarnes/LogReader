namespace LogReader.Core.Tests;

using LogReader.Core.Models;
using LogReader.Mcp;

public sealed partial class HeadlessLogQueryBackendTests
{
    [Fact]
    public async Task Consistency_DiscoveryExpectedRevisionRejectsMixedPagesAndFlagsMcpError()
    {
        var path = await CreateFileAsync("discovery-revision.log", "content");
        var catalog = new MutableCatalogReader(CreateSnapshot(("file", path)));
        using var backend = CreateBackend(catalog.Snapshot, catalogReader: catalog);
        var first = await backend.ListLogTreeAsync(new(MaxNodes: 1));
        var revision = first.CatalogRevision;
        var page = await backend.ListLogTreeAsync(new(MaxNodes: 1, StartIndex: 1, ExpectedCatalogRevision: revision));
        Assert.Empty(page.Errors);
        catalog.Snapshot = CreateSnapshot(("changed", path));
        var rejected = await new McpLogTools(backend).ListLogTreeToolAsync(startIndex: 1, expectedCatalogRevision: revision);
        Assert.True(rejected.IsError);
        var envelope = rejected.StructuredContent!.Value;
        Assert.Equal("stale_catalog_revision", envelope.GetProperty("errors")[0].GetProperty("code").GetString());
        Assert.Empty(envelope.GetProperty("result").GetProperty("nodes").EnumerateArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Consistency_InvalidSnapshotCursorIsTerminalAndNotAnAutomaticRetryHint(bool count)
    {
        var path = await CreateFileAsync("invalid-recovery.log", "needle");
        using var backend = CreateBackend(CreateSnapshot(("file", path)));
        var targets = new[] { new ConfiguredLogTarget(ConfiguredLogTargetKind.LogFile, "file") };
        var errors = count
            ? (await backend.CountLogsAsync(new() { Targets = targets, Query = "needle", Cursor = "invalid" })).Errors
            : (await backend.SearchLogsAsync(new() { Targets = targets, Query = "needle", Cursor = "invalid" })).Errors;
        var error = Assert.Single(errors);
        Assert.False(error.IsRetryable);
        Assert.Contains("explicit intent", error.Message);
    }

    [Fact]
    public async Task Consistency_TimestampCoverageDoesNotInventTimestampPresenceWithoutTimeCriteria()
    {
        var path = await CreateFileAsync("coverage-unknown.log", "needle without timestamp");
        using var backend = CreateBackend(CreateSnapshot(("file", path)));
        var result = (await backend.SearchLogsAsync(Search("file", "needle", ConfiguredLogTargetKind.LogFile))).Result!;
        Assert.Equal(1, result.TimestampCoverage.EvaluatedLineCount);
        Assert.False(result.TimestampCoverage.TimestampEvaluationPerformed);
    }
}
