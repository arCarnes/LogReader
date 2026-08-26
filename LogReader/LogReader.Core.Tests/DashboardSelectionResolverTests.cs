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
    public void Resolve_OversizedOrSensitiveCatalogMetadataFailsGenerically()
    {
        var sensitiveId = Path.Combine(_rootPath, "credential-like-secret");
        var snapshot = new ConfiguredLogCatalogSnapshot(
            1,
            [Group("dashboard", new string('n', ConfiguredLogLimits.DefaultMaxNameCharacters + 1), LogGroupKind.Dashboard)],
            [new ConfiguredLogFile(sensitiveId, Path.Combine(_rootPath, "app.log"))]);

        var result = _resolver.Resolve(
            snapshot,
            Request(new ConfiguredLogTarget(ConfiguredLogTargetKind.Dashboard, "dashboard")));

        var error = Assert.Single(result.Errors);
        Assert.Equal("invalid_catalog", error.Code);
        Assert.Equal("The configured dashboard catalog is invalid.", error.Message);
        Assert.DoesNotContain(_rootPath, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_OversizedCallerIdIsRejectedWithoutReflectingIt()
    {
        var oversized = new string('x', ConfiguredLogLimits.DefaultMaxIdCharacters + 1);

        var result = _resolver.Resolve(
            CreateSnapshot(),
            Request(new ConfiguredLogTarget(ConfiguredLogTargetKind.LogFile, oversized)));

        var error = Assert.Single(result.Errors);
        Assert.Equal("invalid_target_id", error.Code);
        Assert.Null(error.TargetId);
        Assert.DoesNotContain(oversized, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_DeepCatalogFailsBeforeRecursiveProjection()
    {
        var groups = Enumerable.Range(0, ConfiguredLogLimits.HardMaxTreeDepth + 2)
            .Select(index => Group(
                $"group-{index}",
                $"Group {index}",
                LogGroupKind.Branch,
                parentId: index == 0 ? null : $"group-{index - 1}"))
            .ToArray();
        var snapshot = new ConfiguredLogCatalogSnapshot(1, groups, []);

        var result = _resolver.Resolve(
            snapshot,
            Request(new ConfiguredLogTarget(ConfiguredLogTargetKind.Folder, "group-0")));

        var error = Assert.Single(result.Errors);
        Assert.Equal("invalid_catalog", error.Code);
        Assert.Equal("The configured dashboard catalog is invalid.", error.Message);
    }

    [Fact]
    public void Resolve_LargeDuplicateSelectionReturnsBoundedPageAndContinuation()
    {
        var files = Enumerable.Range(0, ConfiguredLogLimits.DefaultMaxExpandedStableFiles + 1)
            .Select(index => new ConfiguredLogFile($"file-{index}", Path.Combine(_rootPath, "shared.log")))
            .ToArray();
        var snapshot = new ConfiguredLogCatalogSnapshot(
            1,
            [Group("dashboard", "Dashboard", LogGroupKind.Dashboard, fileIds: files.Select(file => file.Id))],
            files);

        var result = _resolver.Resolve(
            snapshot,
            Request(new ConfiguredLogTarget(ConfiguredLogTargetKind.Dashboard, "dashboard")));

        Assert.True(result.IsSuccess);
        Assert.False(result.Summary.RejectedByLimit);
        Assert.Single(result.Files);
        Assert.True(result.HasMore);
        Assert.Equal(ConfiguredLogLimits.DefaultMaxResolvedFiles, result.Summary.PageCandidateCount);
        Assert.Equal(ConfiguredLogLimits.DefaultMaxExpandedStableFiles + 1, result.Summary.ExpandedStableFileCount);
    }

    [Fact]
    public void Resolve_LargeRepeatedMembershipPreservesBoundedAuthorizedProvenance()
    {
        var dashboards = Enumerable.Range(0, ConfiguredLogLimits.DefaultMaxProvenanceEntries + 1)
            .Select(index => Group(
                $"dashboard-{index}",
                $"Dashboard {index}",
                LogGroupKind.Dashboard,
                parentId: "folder",
                sortOrder: index,
                fileIds: ["file"]))
            .ToArray();
        var snapshot = new ConfiguredLogCatalogSnapshot(
            1,
            [Group("folder", "Folder", LogGroupKind.Branch), .. dashboards],
            [new ConfiguredLogFile("file", Path.Combine(_rootPath, "shared.log"))]);

        var result = _resolver.Resolve(
            snapshot,
            Request(new ConfiguredLogTarget(ConfiguredLogTargetKind.Folder, "folder")));

        Assert.True(result.IsSuccess);
        Assert.False(result.HasMore);
        Assert.Equal(ConfiguredLogLimits.DefaultMaxProvenanceEntries + 1, Assert.Single(result.Files).Provenance.Length);
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
    public void Resolve_PhysicalFileLimitReturnsStableFirstPage()
    {
        var result = _resolver.Resolve(
            CreateSnapshot(),
            new ConfiguredLogSelectionRequest(
                [new(ConfiguredLogTargetKind.Folder, "folder-root")],
                ReferenceDate,
                maxResolvedFiles: 2));

        Assert.True(result.IsSuccess);
        Assert.False(result.Summary.RejectedByLimit);
        Assert.Equal(["file-a", "file-shared"], result.Files.Select(file => file.FileId));
        Assert.True(result.HasMore);
        Assert.Equal(2, result.Summary.RemainingCandidateCount);
    }

    [Fact]
    public void Resolve_ExactPageBoundaryHasNoContinuation()
    {
        var files = Enumerable.Range(0, 50)
            .Select(index => new ConfiguredLogFile($"file-{index:D3}", Path.Combine(_rootPath, $"{index:D3}.log")))
            .ToArray();
        var snapshot = new ConfiguredLogCatalogSnapshot(
            1,
            [Group("dashboard", "Dashboard", LogGroupKind.Dashboard, fileIds: files.Select(file => file.Id))],
            files);

        var result = _resolver.Resolve(
            snapshot,
            Request(new ConfiguredLogTarget(ConfiguredLogTargetKind.Dashboard, "dashboard")));

        Assert.Equal(50, result.Files.Length);
        Assert.False(result.HasMore);
        Assert.Equal(50, result.Summary.PageCandidateCount);
        Assert.Equal(0, result.Summary.RemainingCandidateCount);
    }

    [Fact]
    public void Resolve_MoreThanFiftyFilesTraversesStablePagesWithoutSkipping()
    {
        var files = Enumerable.Range(0, 123)
            .Select(index => new ConfiguredLogFile($"file-{index:D3}", Path.Combine(_rootPath, $"{index:D3}.log")))
            .ToArray();
        var snapshot = new ConfiguredLogCatalogSnapshot(
            1,
            [Group("dashboard", "Dashboard", LogGroupKind.Dashboard, fileIds: files.Select(file => file.Id))],
            files);
        var targets = new[] { new ConfiguredLogTarget(ConfiguredLogTargetKind.Dashboard, "dashboard") };
        var returnedIds = new List<string>();
        ConfiguredLogSelectionContinuation? continuation = null;
        var pageSizes = new List<int>();

        do
        {
            var page = _resolver.Resolve(
                snapshot,
                new ConfiguredLogSelectionRequest(targets, ReferenceDate, maxResolvedFiles: 50, continuation: continuation));
            Assert.True(page.IsSuccess);
            returnedIds.AddRange(page.Files.Select(file => file.FileId));
            pageSizes.Add(page.Files.Length);
            continuation = page.Continuation;
        }
        while (continuation != null);

        Assert.Equal([50, 50, 23], pageSizes);
        Assert.Equal(files.Select(file => file.Id), returnedIds);
        Assert.Equal(returnedIds.Count, returnedIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Resolve_TwoThousandCandidatesTraverseFortyStablePages()
    {
        var files = Enumerable.Range(0, ConfiguredLogLimits.DefaultMaxSearchCandidates)
            .Select(index => new ConfiguredLogFile($"file-{index:D4}", Path.Combine(_rootPath, $"{index:D4}.log")))
            .ToArray();
        var snapshot = new ConfiguredLogCatalogSnapshot(
            1,
            [Group("dashboard", "Dashboard", LogGroupKind.Dashboard, fileIds: files.Select(file => file.Id))],
            files);
        var target = new[] { new ConfiguredLogTarget(ConfiguredLogTargetKind.Dashboard, "dashboard") };
        var returnedIds = new List<string>();
        ConfiguredLogSelectionContinuation? continuation = null;
        var pageCount = 0;

        do
        {
            var page = _resolver.Resolve(
                snapshot,
                new ConfiguredLogSelectionRequest(target, ReferenceDate, maxResolvedFiles: 50, continuation: continuation));
            Assert.True(page.IsSuccess);
            returnedIds.AddRange(page.Files.Select(file => file.FileId));
            continuation = page.Continuation;
            pageCount++;
        }
        while (continuation != null);

        Assert.Equal(40, pageCount);
        Assert.Equal(files.Select(file => file.Id), returnedIds);
    }

    [Fact]
    public void Resolve_CandidateTwoThousandOneRejectsBeforePathSelection()
    {
        var files = Enumerable.Range(0, ConfiguredLogLimits.DefaultMaxSearchCandidates + 1)
            .Select(index => new ConfiguredLogFile($"file-{index:D4}", Path.Combine(_rootPath, $"{index:D4}.log")))
            .ToArray();
        var snapshot = new ConfiguredLogCatalogSnapshot(
            1,
            [Group("dashboard", "Dashboard", LogGroupKind.Dashboard, fileIds: files.Select(file => file.Id))],
            files);
        var selector = new CountingCandidateSelector();

        var result = _resolver.Resolve(
            snapshot,
            Request(new ConfiguredLogTarget(ConfiguredLogTargetKind.Dashboard, "dashboard")),
            selector);

        Assert.Equal("search_candidate_limit_exceeded", Assert.Single(result.Errors).Code);
        Assert.True(result.Summary.RejectedByLimit);
        Assert.Empty(result.Files);
        Assert.Equal(0, selector.CallCount);
    }

    [Fact]
    public void Resolve_DuplicatePhysicalPathsSpanningPagesAreReturnedOnlyOnce()
    {
        var files = Enumerable.Range(0, 75)
            .Select(index => new ConfiguredLogFile(
                $"file-{index:D3}",
                Path.Combine(_rootPath, index == 60 ? "010.log" : $"{index:D3}.log")))
            .ToArray();
        var snapshot = new ConfiguredLogCatalogSnapshot(
            1,
            [Group("dashboard", "Dashboard", LogGroupKind.Dashboard, fileIds: files.Select(file => file.Id))],
            files);
        var target = new[] { new ConfiguredLogTarget(ConfiguredLogTargetKind.Dashboard, "dashboard") };
        var paths = new List<string>();
        ConfiguredLogSelectionContinuation? continuation = null;

        do
        {
            var page = _resolver.Resolve(
                snapshot,
                new ConfiguredLogSelectionRequest(target, ReferenceDate, maxResolvedFiles: 25, continuation: continuation));
            paths.AddRange(page.Files.Select(file => file.PhysicalPath));
            continuation = page.Continuation;
        }
        while (continuation != null);

        Assert.Equal(74, paths.Count);
        Assert.Equal(paths.Count, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Resolve_PageCandidateProbingIsBoundedAndCancellationIsObserved()
    {
        var files = Enumerable.Range(0, 80)
            .Select(index => new ConfiguredLogFile($"file-{index:D3}", Path.Combine(_rootPath, $"{index:D3}.log")))
            .ToArray();
        var snapshot = new ConfiguredLogCatalogSnapshot(
            1,
            [Group("dashboard", "Dashboard", LogGroupKind.Dashboard, fileIds: files.Select(file => file.Id))],
            files);
        var selector = new CountingCandidateSelector();

        var page = _resolver.Resolve(
            snapshot,
            new ConfiguredLogSelectionRequest(
                [new(ConfiguredLogTargetKind.Dashboard, "dashboard")],
                ReferenceDate,
                maxResolvedFiles: 20),
            selector);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Equal(20, selector.CallCount);
        Assert.True(page.HasMore);
        Assert.ThrowsAny<OperationCanceledException>(() => _resolver.Resolve(
            snapshot,
            new ConfiguredLogSelectionRequest(
                [new(ConfiguredLogTargetKind.Dashboard, "dashboard")],
                ReferenceDate,
                maxResolvedFiles: 20,
                continuation: page.Continuation),
            selector,
            cancellation.Token));
    }

    [Fact]
    public void Resolve_InvalidContinuationFailsSafely()
    {
        var result = _resolver.Resolve(
            CreateSnapshot(),
            new ConfiguredLogSelectionRequest(
                [new(ConfiguredLogTargetKind.Folder, "folder-root")],
                ReferenceDate,
                continuation: new ConfiguredLogSelectionContinuation(999, [])));

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_selection_continuation", Assert.Single(result.Errors).Code);
        Assert.Empty(result.Files);
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
    public void Resolve_DatePatternExpansionCannotAllocateBeyondPathBound()
    {
        var repeated = new string('a', 100);
        var snapshot = new ConfiguredLogCatalogSnapshot(
            1,
            [Group("dashboard", "Dashboard", LogGroupKind.Dashboard, fileIds: ["file"])],
            [new ConfiguredLogFile("file", Path.Combine(_rootPath, repeated, "app.log"))],
            [new("pattern", "Expansion", "a", new string('b', ConfiguredLogLimits.DefaultMaxDatePatternCharacters))]);

        var result = _resolver.Resolve(
            snapshot,
            new ConfiguredLogSelectionRequest(
                [new(ConfiguredLogTargetKind.Dashboard, "dashboard")],
                ReferenceDate,
                dateOffsetDays: 1));

        Assert.True(result.IsSuccess);
        Assert.True(result.IsPartial);
        Assert.Empty(result.Files);
        Assert.Equal("date_pattern_no_match", Assert.Single(result.FileErrors).Code);
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
    public void Resolve_PathSelectorCannotEscapePersistedAuthorizedCandidates()
    {
        var result = _resolver.Resolve(
            CreateSnapshot(),
            Request(new ConfiguredLogTarget(ConfiguredLogTargetKind.LogFile, "file-a")),
            new FixedCandidateSelector(Path.Combine(_rootPath, "not-configured.log")));

        Assert.True(result.IsSuccess);
        Assert.True(result.IsPartial);
        Assert.Empty(result.Files);
        Assert.Equal("path_candidate_selection_failed", Assert.Single(result.FileErrors).Code);
    }

    [Fact]
    public void Resolve_NormalizesDotSegmentsBeforePhysicalPathDeduplication()
    {
        var canonical = Path.Combine(_rootPath, "shared.log");
        var equivalent = Path.Combine(_rootPath, "folder", "..", "shared.log");
        var snapshot = new ConfiguredLogCatalogSnapshot(
            1,
            [Group("dashboard", "Dashboard", LogGroupKind.Dashboard, fileIds: ["one", "two"])],
            [new ConfiguredLogFile("one", canonical), new ConfiguredLogFile("two", equivalent)]);

        var result = _resolver.Resolve(
            snapshot,
            Request(new ConfiguredLogTarget(ConfiguredLogTargetKind.Dashboard, "dashboard")));

        var file = Assert.Single(result.Files);
        Assert.Equal(["one", "two"], file.EquivalentFileIds);
        Assert.Equal(Path.GetFullPath(canonical), file.PhysicalPath);
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

    private sealed class FixedCandidateSelector(string path) : IConfiguredLogPathCandidateSelector
    {
        public string SelectPath(string fileId, ImmutableArray<string> orderedCandidates) => path;
    }

    private sealed class CountingCandidateSelector : IConfiguredLogPathCandidateSelector
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public string SelectPath(string fileId, ImmutableArray<string> orderedCandidates)
        {
            Interlocked.Increment(ref _callCount);
            return orderedCandidates[0];
        }
    }
}
