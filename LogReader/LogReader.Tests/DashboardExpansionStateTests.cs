namespace LogReader.Tests;

using LogReader.App.Models;
using LogReader.App.Services;
using LogReader.Core.Models;

public partial class DashboardWorkspaceServiceTests
{
    [Fact]
    public async Task Expansion_RestoresEmptyAndNestedGroupsWithoutEventsOrCatalogWrites()
    {
        var folder = CreateGroup("folder", "Folder", LogGroupKind.Branch);
        var dashboard = CreateGroup("dashboard", "Dashboard");
        dashboard.Model.ParentGroupId = folder.Id;
        var repo = new RecordingLogGroupRepository();
        await repo.AddAsync(folder.Model);
        await repo.AddAsync(dashboard.Model);
        var host = new DashboardWorkspaceHostStub();
        var service = new DashboardWorkspaceService(host, new StubLogFileRepository(), repo,
            _ => throw new InvalidOperationException("Expansion must not probe paths."));
        service.RebuildGroupsCollection(await repo.GetAllAsync());
        var notifications = 0;
        service.ExpansionStateChanged += () => notifications++;
        service.RestoreExpansionState(new UiState { GroupExpansionById = new() { ["folder"] = true, ["dashboard"] = true, ["unknown"] = true } });
        Assert.All(host.Groups, group => Assert.True(group.IsExpanded));
        Assert.Equal(0, notifications);
        Assert.DoesNotContain("unknown", service.CaptureExpansionState().GroupExpansionById.Keys);
        host.Groups[1].IsExpanded = false;
        Assert.Equal(1, notifications);
        Assert.False(service.CaptureExpansionState().GroupExpansionById["dashboard"]);
        Assert.Equal(0, repo.ReplaceAllCallCount);
    }

    [Fact]
    public async Task FilteringAndRebuild_KeepDurableBaselineAndDiscardFilteredToggles()
    {
        var folder = CreateGroup("folder", "Folder", LogGroupKind.Branch);
        var dashboard = CreateGroup("dashboard", "Match");
        dashboard.Model.ParentGroupId = folder.Id;
        var repo = new RecordingLogGroupRepository();
        await repo.AddAsync(folder.Model);
        await repo.AddAsync(dashboard.Model);
        var host = new DashboardWorkspaceHostStub();
        var service = new DashboardWorkspaceService(host, new StubLogFileRepository(), repo);
        service.RebuildGroupsCollection(await repo.GetAllAsync());
        service.RestoreExpansionState(new UiState { GroupExpansionById = new() { ["folder"] = false, ["dashboard"] = true } });
        var notifications = 0;
        service.ExpansionStateChanged += () => notifications++;
        host.DashboardTreeFilter = "Match";
        service.ApplyDashboardTreeFilter();
        Assert.True(host.Groups[0].IsExpanded);
        host.Groups[1].IsExpanded = false;
        Assert.False(service.CaptureExpansionState().GroupExpansionById["folder"]);
        Assert.True(service.CaptureExpansionState().GroupExpansionById["dashboard"]);
        service.RebuildGroupsCollection(await repo.GetAllAsync());
        host.DashboardTreeFilter = "";
        service.ApplyDashboardTreeFilter();
        Assert.False(host.Groups[0].IsExpanded);
        Assert.True(host.Groups[1].IsExpanded);
        Assert.Equal(0, notifications);
    }

    [Fact]
    public async Task MutationAndImport_KeepSurvivingIds_PruneDeletedIds_AndUseCreationDefaults()
    {
        var folder = CreateGroup("folder", "Folder", LogGroupKind.Branch);
        var dashboard = CreateGroup("dashboard", "Dashboard");
        dashboard.Model.SortOrder = 1;
        var repo = new RecordingLogGroupRepository();
        await repo.AddAsync(folder.Model);
        await repo.AddAsync(dashboard.Model);
        var host = new DashboardWorkspaceHostStub();
        var service = new DashboardWorkspaceService(host, new StubLogFileRepository(), repo);
        service.RebuildGroupsCollection(await repo.GetAllAsync());
        service.RestoreExpansionState(new UiState { GroupExpansionById = new() { ["folder"] = false, ["dashboard"] = true } });
        var current = host.Groups[1];
        current.BeginEdit();
        current.EditName = "Renamed";
        await current.CommitEditAsync();
        await service.MoveGroupToAsync(current, host.Groups[0], DropPlacement.Inside);
        Assert.True(service.CaptureExpansionState().GroupExpansionById["dashboard"]);
        Assert.True(service.CaptureExpansionState().GroupExpansionById["folder"]);
        await service.DuplicateGroupAsync(current);
        var duplicate = host.Groups.Single(group => group.Id != "folder" && group.Id != "dashboard");
        Assert.False(service.CaptureExpansionState().GroupExpansionById[duplicate.Id]);
        await service.DeleteGroupAsync(duplicate);
        Assert.DoesNotContain(duplicate.Id, service.CaptureExpansionState().GroupExpansionById.Keys);
        host.DashboardTreeFilter = "Renamed";
        service.ApplyDashboardTreeFilter();
        var notifications = 0;
        service.ExpansionStateChanged += () => notifications++;
        await service.CreateChildGroupAsync(host.Groups[0]);
        Assert.Equal(1, notifications);
        var created = host.Groups.Single(group => group.Id != "folder" && group.Id != "dashboard");
        Assert.True(service.CaptureExpansionState().GroupExpansionById[created.Id]);
        await service.ApplyImportedViewAsync(new ViewExport { Groups = new()
        {
            new ViewExportGroup { Id = "dashboard", Name = "Imported survivor" },
            new ViewExportGroup { Id = "imported", Name = "New imported" }
        } });
        var snapshot = service.CaptureExpansionState().GroupExpansionById;
        // Imports deliberately allocate new IDs; no prior expansion belongs to these groups.
        Assert.Equal(2, snapshot.Count);
        Assert.All(snapshot.Values, expanded => Assert.False(expanded));
        Assert.DoesNotContain("dashboard", snapshot.Keys);
        Assert.DoesNotContain("folder", snapshot.Keys);
    }
}
