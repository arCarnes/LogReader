namespace LogReader.Tests;

using System.Collections.Specialized;
using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.Core.Models;

public partial class DashboardWorkspaceServiceTests
{
    [Fact]
    public void ReconcileGroupsCollection_UpdatesCatalogWithoutResettingRuntimeState()
    {
        var original = CreateGroup("dashboard", "Before", "old-file");
        var progress = new DashboardLoadProgress(7, DashboardLoadPhase.Ready, 1, 1, 1, 0);
        original.SetLoadProgress(progress);
        original.DashboardReadyRevision = 7;
        original.IsExpanded = true;
        var host = new DashboardWorkspaceHostStub(original) { ActiveDashboardId = original.Id };
        var service = new DashboardWorkspaceService(host, new StubLogFileRepository(), new StubLogGroupRepository());
        var changes = new List<NotifyCollectionChangedAction>();
        host.Groups.CollectionChanged += (_, args) => changes.Add(args.Action);
        var readyEvents = 0;
        original.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(LogGroupViewModel.DashboardReadyRevision)) readyEvents++;
        };

        service.ReconcileGroupsCollection(new List<LogGroup>
        {
            new() { Id = "folder", Name = "Folder", Kind = LogGroupKind.Branch },
            new() { Id = original.Id, Name = "After", Kind = LogGroupKind.Dashboard, ParentGroupId = "folder", FileIds = ["new-file"] }
        });

        Assert.Same(original, host.Groups[1]);
        Assert.Equal("After", original.Name);
        Assert.Equal(new[] { "new-file" }, original.Model.FileIds);
        Assert.Same(host.Groups[0], original.Parent);
        Assert.Same(original, Assert.Single(host.Groups[0].Children));
        Assert.Equal(progress, original.LoadProgress);
        Assert.Equal(7, original.DashboardReadyRevision);
        Assert.Equal(0, readyEvents);
        Assert.True(original.IsExpanded);
        Assert.True(original.IsSelected);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReconcileGroupsCollection_LeavesScopeIfActiveDashboardIsNoLongerPresent(bool becomesFolder)
    {
        var original = CreateGroup("dashboard", "Before");
        original.IsSelected = true;
        var host = new DashboardWorkspaceHostStub(original) { ActiveDashboardId = original.Id };
        var service = new DashboardWorkspaceService(host, new StubLogFileRepository(), new StubLogGroupRepository());
        var refreshed = becomesFolder
            ? new List<LogGroup> { new() { Id = original.Id, Name = "Folder", Kind = LogGroupKind.Branch } }
            : new List<LogGroup>();

        service.ReconcileGroupsCollection(refreshed);

        Assert.Null(host.ActiveDashboardId);
        Assert.All(host.Groups, group => Assert.False(group.IsSelected));
    }
}
