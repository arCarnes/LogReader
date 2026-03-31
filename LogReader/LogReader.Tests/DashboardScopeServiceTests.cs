using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.Core;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

namespace LogReader.Tests;

public class DashboardScopeServiceTests
{
    private readonly DashboardScopeService _service = new();

    [Fact]
    public void ToggleGroupSelection_SelectsDashboardAndClearsOtherSelections()
    {
        var dashboardOne = CreateGroup("dashboard-1", "Dashboard One", LogGroupKind.Dashboard);
        var dashboardTwo = CreateGroup("dashboard-2", "Dashboard Two", LogGroupKind.Dashboard);
        dashboardOne.IsSelected = true;

        var activeDashboardId = _service.ToggleGroupSelection(
            new[] { dashboardOne, dashboardTwo },
            dashboardOne.Id,
            dashboardTwo);

        Assert.False(dashboardOne.IsSelected);
        Assert.True(dashboardTwo.IsSelected);
        Assert.Equal(dashboardTwo.Id, activeDashboardId);
    }

    [Fact]
    public void ToggleGroupSelection_OnActiveDashboardClearsSelection()
    {
        var dashboard = CreateGroup("dashboard-1", "Dashboard", LogGroupKind.Dashboard);
        dashboard.IsSelected = true;

        var activeDashboardId = _service.ToggleGroupSelection(
            new[] { dashboard },
            dashboard.Id,
            dashboard);

        Assert.False(dashboard.IsSelected);
        Assert.Null(activeDashboardId);
    }

    [Fact]
    public void GetAdHocTabs_ReturnsOnlyTabsOwnedByAdHocScope()
    {
        var dashboardTab = CreateTab("file-1", @"C:\logs\assigned.log", "dashboard-1");
        var adHocTab = CreateTab("file-2", @"C:\logs\adhoc.log");

        var result = _service.GetAdHocTabs(new[] { dashboardTab, adHocTab });

        Assert.Collection(
            result,
            tab => Assert.Same(adHocTab, tab));
    }

    [Fact]
    public void GetFilteredTabs_ForDashboardScope_UsesScopedTabsAndOrdering()
    {
        var firstTab = CreateTab("file-1", @"C:\logs\first.log", "dashboard-1");
        var secondTab = CreateTab("file-2", @"C:\logs\second.log", "dashboard-1");
        var adHocTab = CreateTab("file-3", @"C:\logs\adhoc.log");

        var result = _service.GetFilteredTabs(
            new[] { firstTab, secondTab, adHocTab },
            "dashboard-1",
            scopedTabs => scopedTabs.Reverse().ToList());

        Assert.Collection(
            result,
            tab => Assert.Same(secondTab, tab),
            tab => Assert.Same(firstTab, tab));
    }

    [Fact]
    public void FindContainingDashboard_ReturnsDashboardWithMatchingFile()
    {
        var targetFileId = "file-2";
        var folder = CreateGroup("folder-1", "Folder", LogGroupKind.Branch);
        var dashboard = CreateGroup("dashboard-1", "Dashboard", LogGroupKind.Dashboard, "file-1", targetFileId);

        var result = _service.FindContainingDashboard(
            new[] { folder, dashboard },
            targetFileId);

        Assert.Same(dashboard, result);
    }

    private static LogGroupViewModel CreateGroup(string id, string name, LogGroupKind kind, params string[] fileIds)
    {
        return new LogGroupViewModel(
            new LogGroup
            {
                Id = id,
                Name = name,
                Kind = kind,
                FileIds = fileIds.ToList()
            },
            _ => Task.CompletedTask);
    }

    private static LogTabViewModel CreateTab(string fileId, string filePath, string? scopeDashboardId = null)
    {
        return new LogTabViewModel(
            fileId,
            filePath,
            new StubLogReaderService(),
            new StubFileTailService(),
            new FileEncodingDetectionService(),
            new AppSettings(),
            skipInitialEncodingResolution: true,
            sessionRegistry: null,
            initialEncoding: FileEncoding.Auto,
            scopeDashboardId: scopeDashboardId);
    }
}
