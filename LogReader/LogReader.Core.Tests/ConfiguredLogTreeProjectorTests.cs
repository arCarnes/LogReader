namespace LogReader.Core.Tests;

using System.Collections.Immutable;
using LogReader.Core.Models;

public sealed class ConfiguredLogTreeProjectorTests
{
    private readonly ConfiguredLogTreeProjector _projector = new();

    [Fact]
    public void Project_FlattensTreeInStableOrderWithoutCatalogOnlyFiles()
    {
        var result = _projector.Project(CreateSnapshot(), new ConfiguredLogTreeRequest());

        Assert.True(result.IsSuccess);
        Assert.Equal(
            ["folder", "dashboard-a", "file-a", "nested", "dashboard-b", "file-b", "root-dashboard", "file-root"],
            result.Nodes.Select(node => node.Id));
        Assert.DoesNotContain(result.Nodes, node => node.Id == "catalog-only");
        Assert.Equal("Folder / Nested / Dashboard", result.Nodes.Single(node => node.Id == "dashboard-b").TreePath);
        Assert.Equal(ConfiguredLogTargetKind.LogFile, result.Nodes.Single(node => node.Id == "file-b").Kind);
    }

    [Fact]
    public void Project_PaginatesStableFlatProjection()
    {
        var snapshot = CreateSnapshot();
        var first = _projector.Project(snapshot, new ConfiguredLogTreeRequest(MaxNodes: 3));
        var second = _projector.Project(snapshot, new ConfiguredLogTreeRequest(MaxNodes: 3, StartIndex: first.NextStartIndex!.Value));

        Assert.Equal(["folder", "dashboard-a", "file-a"], first.Nodes.Select(node => node.Id));
        Assert.Equal(3, first.NextStartIndex);
        Assert.Equal(["nested", "dashboard-b", "file-b"], second.Nodes.Select(node => node.Id));
        Assert.Equal(8, first.TotalNodeCount);
        Assert.Equal(8, second.TotalNodeCount);
    }

    [Fact]
    public void Project_MaxDepthMarksTruncationWithoutEmittingDescendants()
    {
        var result = _projector.Project(
            CreateSnapshot(),
            new ConfiguredLogTreeRequest(RootGroupId: "folder", MaxDepth: 1));

        Assert.True(result.IsSuccess);
        Assert.True(result.DepthTruncated);
        Assert.Equal(["folder", "dashboard-a", "nested"], result.Nodes.Select(node => node.Id));
    }

    [Fact]
    public void Project_DashboardRootIncludesOnlyItsMembership()
    {
        var result = _projector.Project(
            CreateSnapshot(),
            new ConfiguredLogTreeRequest(RootGroupId: "dashboard-b"));

        Assert.Equal(["dashboard-b", "file-b"], result.Nodes.Select(node => node.Id));
        Assert.All(result.Nodes, node => Assert.StartsWith("Folder / Nested / Dashboard", node.TreePath, StringComparison.Ordinal));
    }

    [Fact]
    public void Project_UnknownAndFileRootsReturnStructuredErrors()
    {
        var snapshot = CreateSnapshot();
        var unknown = _projector.Project(snapshot, new ConfiguredLogTreeRequest(RootGroupId: "missing"));
        var file = _projector.Project(snapshot, new ConfiguredLogTreeRequest(RootGroupId: "file-a"));

        Assert.Equal("unknown_target", Assert.Single(unknown.Errors).Code);
        Assert.Equal("target_kind_mismatch", Assert.Single(file.Errors).Code);
    }

    [Fact]
    public void Project_InvalidLimitsRejectBeforeTraversal()
    {
        var result = _projector.Project(
            CreateSnapshot(),
            new ConfiguredLogTreeRequest(MaxDepth: -1, MaxNodes: 0, StartIndex: -1));

        Assert.False(result.IsSuccess);
        Assert.Equal(
            ["invalid_tree_depth", "invalid_tree_node_limit", "invalid_tree_continuation"],
            result.Errors.Select(error => error.Code));
    }

    private static ConfiguredLogCatalogSnapshot CreateSnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), "WeezTailTreeProjection");
        return new ConfiguredLogCatalogSnapshot(
            1,
            [
                Group("folder", "Folder", LogGroupKind.Branch, sortOrder: 0),
                Group("nested", "Nested", LogGroupKind.Branch, "folder", 1),
                Group("dashboard-b", "Dashboard", LogGroupKind.Dashboard, "nested", 0, ["file-b"]),
                Group("dashboard-a", "Dashboard", LogGroupKind.Dashboard, "folder", 0, ["file-a"]),
                Group("root-dashboard", "Root", LogGroupKind.Dashboard, sortOrder: 1, fileIds: ["file-root"])
            ],
            [
                new("file-a", Path.Combine(root, "a.log")),
                new("file-b", Path.Combine(root, "b.log")),
                new("file-root", Path.Combine(root, "root.log")),
                new("catalog-only", Path.Combine(root, "unused.log"))
            ]);
    }

    private static ConfiguredLogGroup Group(
        string id,
        string name,
        LogGroupKind kind,
        string? parentId = null,
        int sortOrder = 0,
        IEnumerable<string>? fileIds = null)
        => new(
            id,
            name,
            sortOrder,
            parentId,
            kind,
            (fileIds ?? Enumerable.Empty<string>()).ToImmutableArray());
}
