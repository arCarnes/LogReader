namespace LogReader.Core.Tests;

using System.Collections.Immutable;
using System.Text.Json;
using LogReader.Core.Models;

public sealed class DashboardSelectionResolverTests
{
    private static readonly DateOnly ReferenceDate = new(2026, 8, 4);
    private readonly DashboardSelectionResolver _resolver = new();

    [Fact]
    public void Resolve_FolderDashboardAndFileTargets_PreserveStableUnionAndAllProvenance()
    {
        var snapshot = CreateSnapshot();
        var request = Request(
            new ConfiguredLogTarget(ConfiguredLogTargetKind.Folder, "folder-root"),
            new ConfiguredLogTarget(ConfiguredLogTargetKind.Dashboard, "dash-a"),
            new ConfiguredLogTarget(ConfiguredLogTargetKind.LogFile, "file-shared"));

        var result = _resolver.Resolve(snapshot, request);

        Assert.True(result.IsSuccess);
        Assert.False(result.IsPartial);
        Assert.Equal(["file-a", "file-shared", "file-b"], result.Files.Select(file => file.FileId));

        var shared = result.Files[1];
        Assert.Equal(["file-shared", "file-duplicate-path"], shared.EquivalentFileIds);
        Assert.Equal(
            ["folder-root", "dash-a", "file-shared"],
            shared.Provenance.Select(item => item.RequestedTargetId).Distinct());
        Assert.Contains(shared.Provenance, item =>
            item.DashboardId == "dash-c" &&
            item.DashboardTreePath == "Operations / Nested / Duplicate");
        Assert.DoesNotContain(shared.Provenance, item => item.TargetTreePath.Contains(_rootPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Resolve_Dashboard_PreservesConfiguredFileOrder()
    {
        var result = _resolver.Resolve(
            CreateSnapshot(),
            Request(new ConfiguredLogTarget(ConfiguredLogTargetKind.Dashboard, "dash-a")));

        Assert.True(result.IsSuccess);
        Assert.Equal(["file-a", "file-shared"], result.Files.Select(file => file.FileId));
    }

    [Fact]
    public void Resolve_CatalogOnlyFile_IsNotAuthorized()
    {
        var result = _resolver.Resolve(
            CreateSnapshot(),
            Request(new ConfiguredLogTarget(ConfiguredLogTargetKind.LogFile, "catalog-only")));

        Assert.False(result.IsSuccess);
        var error = Assert.Single(result.Errors);
        Assert.Equal("file_not_dashboard_member", error.Code);
        Assert.Empty(result.Files);
    }

    [Fact]
    public void Resolve_UnknownAndKindMismatchedTargets_RejectWholeRequest()
    {
        var result = _resolver.Resolve(
            CreateSnapshot(),
            Request(
                new ConfiguredLogTarget(ConfiguredLogTargetKind.Dashboard, "folder-root"),
                new ConfiguredLogTarget(ConfiguredLogTargetKind.LogFile, "missing")));

        Assert.False(result.IsSuccess);
        Assert.Equal(["target_kind_mismatch", "unknown_target"], result.Errors.Select(error => error.Code));
        Assert.Empty(result.Files);
    }

    [Fact]
    public void Resolve_InvalidTopology_ReturnsStructuredCatalogError()
    {
        var snapshot = new ConfiguredLogCatalogSnapshot(
            1,
            [
                Group("one", "One", LogGroupKind.Branch, parentId: "two"),
                Group("two", "Two", LogGroupKind.Branch, parentId: "one")
            ],
            []);

        var result = _resolver.Resolve(
            snapshot,
            Request(new ConfiguredLogTarget(ConfiguredLogTargetKind.Folder, "one")));

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_catalog", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void Resolve_MissingCatalogMembership_ReturnsStructuredCatalogError()
    {
        var snapshot = new ConfiguredLogCatalogSnapshot(
            1,
            [Group("dashboard", "Dashboard", LogGroupKind.Dashboard, fileIds: ["missing-file"])],
            []);

        var result = _resolver.Resolve(
            snapshot,
            Request(new ConfiguredLogTarget(ConfiguredLogTargetKind.Dashboard, "dashboard")));

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_catalog", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void Resolve_DuplicateCatalogFileIdsReturnStructuredCatalogError()
    {
        var path = Path.Combine(_rootPath, "app.log");
        var snapshot = new ConfiguredLogCatalogSnapshot(
            1,
            [Group("dashboard", "Dashboard", LogGroupKind.Dashboard, fileIds: ["file"])],
            [new ConfiguredLogFile("file", path), new ConfiguredLogFile("file", path)]);

        var result = _resolver.Resolve(
            snapshot,
            Request(new ConfiguredLogTarget(ConfiguredLogTargetKind.Dashboard, "dashboard")));

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_catalog", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void Resolve_EmptyDashboardSucceedsWithNoFiles()
    {
        var snapshot = new ConfiguredLogCatalogSnapshot(
            1,
            [Group("dashboard", "Empty", LogGroupKind.Dashboard)],
            []);

        var result = _resolver.Resolve(
            snapshot,
            Request(new ConfiguredLogTarget(ConfiguredLogTargetKind.Dashboard, "dashboard")));

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Files);
    }

    [Fact]
    public void Resolve_EmptyTargetsAndExcessTargets_ReturnStructuredLimitErrors()
    {
        var snapshot = CreateSnapshot();
        var empty = _resolver.Resolve(snapshot, Request());
        var excess = _resolver.Resolve(
            snapshot,
            new ConfiguredLogSelectionRequest(
                [
                    new(ConfiguredLogTargetKind.Dashboard, "dash-a"),
                    new(ConfiguredLogTargetKind.Dashboard, "dash-root")
                ],
                ReferenceDate,
                maxTargets: 1));

        Assert.Equal("targets_required", Assert.Single(empty.Errors).Code);
        Assert.Equal("target_limit_exceeded", Assert.Single(excess.Errors).Code);
        Assert.True(excess.Summary.RejectedByLimit);
    }

    [Fact]
    public void Resolve_DateOffsetOutsideCalendarRangeRejectsWholeRequest()
    {
        var request = new ConfiguredLogSelectionRequest(
            [new(ConfiguredLogTargetKind.Dashboard, "dash-a")],
            ReferenceDate,
            dateOffsetDays: int.MaxValue);

        var result = _resolver.Resolve(CreateSnapshot(), request);

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_date_offset", Assert.Single(result.Errors).Code);
        Assert.Empty(result.Files);
    }

    [Fact]
    public void Resolve_PhysicalFileLimitRejectsWithoutFirstSubset()
    {
        var result = _resolver.Resolve(
            CreateSnapshot(),
            new ConfiguredLogSelectionRequest(
                [new(ConfiguredLogTargetKind.Folder, "folder-root")],
                ReferenceDate,
                maxResolvedFiles: 2));

        Assert.False(result.IsSuccess);
        Assert.True(result.Summary.RejectedByLimit);
        Assert.Equal(3, result.Summary.ResolvedPhysicalFileCount);
        Assert.Empty(result.Files);
        Assert.Equal("resolved_file_limit_exceeded", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void Resolve_ExplicitDateOffsetProducesOrderedCandidates_AndZeroUsesBasePath()
    {
        var basePath = Path.Combine(_rootPath, "current", "app.log");
        var snapshot = new ConfiguredLogCatalogSnapshot(
            1,
            [Group("dashboard", "Dashboard", LogGroupKind.Dashboard, fileIds: ["file"])],
            [new ConfiguredLogFile("file", basePath)],
            [
                new("one", "Date folder", "current", "{yyyyMMdd}"),
                new("two", "Date folder alternative", "current", "{yyyy-MM-dd}")
            ]);
        var target = new ConfiguredLogTarget(ConfiguredLogTargetKind.Dashboard, "dashboard");

        var baseResult = _resolver.Resolve(snapshot, Request(target));
        var rolledResult = _resolver.Resolve(
            snapshot,
            new ConfiguredLogSelectionRequest([target], ReferenceDate, dateOffsetDays: 2));

        Assert.Equal(Path.GetFullPath(basePath), Assert.Single(baseResult.Files).PhysicalPath);
        Assert.Equal(
            [
                Path.GetFullPath(Path.Combine(_rootPath, "20260802", "app.log")),
                Path.GetFullPath(Path.Combine(_rootPath, "2026-08-02", "app.log"))
            ],
            Assert.Single(rolledResult.Files).OrderedPathCandidates);
    }

    [Fact]
    public void Resolve_DateOffsetDoesNotUseAmbientStateAndReportsPerFilePatternErrors()
    {
        var snapshot = new ConfiguredLogCatalogSnapshot(
            1,
            [Group("dashboard", "Dashboard", LogGroupKind.Dashboard, fileIds: ["file-a", "file-b"])],
            [
                new ConfiguredLogFile("file-a", Path.Combine(_rootPath, "current", "a.log")),
                new ConfiguredLogFile("file-b", Path.Combine(_rootPath, "static", "b.log"))
            ],
            [new("pattern", "Date", "current", "{yyyyMMdd}")]);
        var request = new ConfiguredLogSelectionRequest(
            [new(ConfiguredLogTargetKind.Dashboard, "dashboard")],
            ReferenceDate,
            dateOffsetDays: 1);

        var first = _resolver.Resolve(snapshot, request);
        var second = _resolver.Resolve(snapshot, request);

        Assert.Equal(
            first.Files.Select(file => new
            {
                file.FileId,
                file.PhysicalPath,
                Candidates = string.Join('|', file.OrderedPathCandidates),
                Provenance = string.Join('|', file.Provenance.Select(item => $"{item.RequestedTargetId}:{item.DashboardId}"))
            }),
            second.Files.Select(file => new
            {
                file.FileId,
                file.PhysicalPath,
                Candidates = string.Join('|', file.OrderedPathCandidates),
                Provenance = string.Join('|', file.Provenance.Select(item => $"{item.RequestedTargetId}:{item.DashboardId}"))
            }));
        Assert.Equal("file-a", Assert.Single(first.Files).FileId);
        Assert.True(first.IsPartial);
        Assert.Equal("date_pattern_no_match", Assert.Single(first.FileErrors).Code);
    }

    [Fact]
    public void Resolve_ExistenceAwareCandidateSelectionDeduplicatesFinalPhysicalPath()
    {
        var snapshot = new ConfiguredLogCatalogSnapshot(
            1,
            [Group("dashboard", "Dashboard", LogGroupKind.Dashboard, fileIds: ["file-a", "file-b"])],
            [
                new ConfiguredLogFile("file-a", Path.Combine(_rootPath, "slot-a", "app.log")),
                new ConfiguredLogFile("file-b", Path.Combine(_rootPath, "slot-b", "app.log"))
            ],
            [
                new("a-first", "A first", "slot-a", "first-a-{yyyyMMdd}"),
                new("a-shared", "A shared", "slot-a", "shared-{yyyyMMdd}"),
                new("b-first", "B first", "slot-b", "first-b-{yyyyMMdd}"),
                new("b-shared", "B shared", "slot-b", "shared-{yyyyMMdd}")
            ]);
        var request = new ConfiguredLogSelectionRequest(
            [new(ConfiguredLogTargetKind.Dashboard, "dashboard")],
            ReferenceDate,
            dateOffsetDays: 1);

        var result = _resolver.Resolve(snapshot, request, LastCandidateSelector.Instance);

        var file = Assert.Single(result.Files);
        Assert.Equal(["file-a", "file-b"], file.EquivalentFileIds);
        Assert.EndsWith(
            Path.Combine("shared-20260803", "app.log"),
            file.PhysicalPath,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Snapshot_IsDefensiveAndRevisionChangesOnlyWithRelevantContent()
    {
        var groups = new List<ConfiguredLogGroup>
        {
            Group("dashboard", "Dashboard", LogGroupKind.Dashboard, fileIds: ["file"])
        };
        var files = new List<ConfiguredLogFile>
        {
            new("file", Path.Combine(_rootPath, "secret-machine-path", "app.log"))
        };
        var snapshot = new ConfiguredLogCatalogSnapshot(1, groups, files);
        var equivalent = new ConfiguredLogCatalogSnapshot(1, groups, files);
        groups.Clear();
        files[0] = files[0] with { PhysicalPath = Path.Combine(_rootPath, "changed.log") };
        var changed = new ConfiguredLogCatalogSnapshot(
            1,
            snapshot.Groups,
            [snapshot.Files[0] with { PhysicalPath = Path.Combine(_rootPath, "changed.log") }]);

        Assert.Single(snapshot.Groups);
        Assert.Single(snapshot.Files);
        Assert.Equal(snapshot.Revision, equivalent.Revision);
        Assert.NotEqual(snapshot.Revision, changed.Revision);
        Assert.DoesNotContain("secret-machine-path", snapshot.Revision, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("sha256:", snapshot.Revision, StringComparison.Ordinal);
    }

    [Fact]
    public void Contracts_DoNotSerializePhysicalPathsOrDatePathPatterns()
    {
        var snapshot = CreateSnapshot();
        var result = _resolver.Resolve(
            snapshot,
            Request(new ConfiguredLogTarget(ConfiguredLogTargetKind.Dashboard, "dash-a")));

        var snapshotJson = JsonSerializer.Serialize(snapshot);
        var resultJson = JsonSerializer.Serialize(result);

        Assert.DoesNotContain(_rootPath, snapshotJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_rootPath, resultJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("physicalPath", resultJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("orderedPathCandidates", resultJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_DuplicateDisplayNamesAndRootDashboardRemainSelectableById()
    {
        var result = _resolver.Resolve(
            CreateSnapshot(),
            Request(
                new ConfiguredLogTarget(ConfiguredLogTargetKind.Dashboard, "dash-root"),
                new ConfiguredLogTarget(ConfiguredLogTargetKind.Dashboard, "dash-c")));

        Assert.True(result.IsSuccess);
        Assert.Contains(result.Files, file => file.FileId == "file-root");
        Assert.Contains(result.Files.SelectMany(file => file.Provenance), item =>
            item.DashboardTreePath == "Duplicate");
        Assert.Contains(result.Files.SelectMany(file => file.Provenance), item =>
            item.DashboardTreePath == "Operations / Nested / Duplicate");
    }

    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "WeezTailCatalogContracts");

    private ConfiguredLogCatalogSnapshot CreateSnapshot()
        => new(
            1,
            [
                Group("folder-root", "Operations", LogGroupKind.Branch, sortOrder: 0),
                Group("dash-a", "Primary", LogGroupKind.Dashboard, "folder-root", 0, ["file-a", "file-shared"]),
                Group("folder-nested", "Nested", LogGroupKind.Branch, "folder-root", 1),
                Group("dash-c", "Duplicate", LogGroupKind.Dashboard, "folder-nested", 0, ["file-b", "file-shared", "file-duplicate-path"]),
                Group("dash-root", "Duplicate", LogGroupKind.Dashboard, sortOrder: 1, fileIds: ["file-root"])
            ],
            [
                new("file-a", Path.Combine(_rootPath, "a.log")),
                new("file-shared", Path.Combine(_rootPath, "shared.log")),
                new("file-b", Path.Combine(_rootPath, "b.log")),
                new("file-duplicate-path", Path.Combine(_rootPath.ToUpperInvariant(), "SHARED.LOG")),
                new("file-root", Path.Combine(_rootPath, "root.log")),
                new("catalog-only", Path.Combine(_rootPath, "unused.log"))
            ]);

    private static ConfiguredLogSelectionRequest Request(params ConfiguredLogTarget[] targets)
        => new(targets, ReferenceDate);

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

    private sealed class LastCandidateSelector : IConfiguredLogPathCandidateSelector
    {
        internal static LastCandidateSelector Instance { get; } = new();

        public string SelectPath(string fileId, ImmutableArray<string> orderedCandidates)
            => orderedCandidates[^1];
    }
}
