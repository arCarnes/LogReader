using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows.Threading;
using LogReader.App.Models;
using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.Core.Models;

namespace LogReader.Tests;

public partial class DashboardWorkspaceServiceTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public DashboardWorkspaceServiceTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task TargetedEvent_WithUnrelatedModifier_ChecksOnlyChangedPathInAffectedDashboard()
    {
        var first = new LogFileEntry { FilePath = @"C:\logs\first.log" };
        var second = new LogFileEntry { FilePath = @"C:\logs\second.log" };
        var files = new StubLogFileRepository();
        await files.AddAsync(first);
        await files.AddAsync(second);
        var dashboard = CreateGroup("dashboard", "Dashboard", first.Id, second.Id);
        var modified = CreateGroup("modified", "Modified");
        var host = new DashboardWorkspaceHostStub(dashboard, modified);
        var checkedPaths = new List<string>();
        var activation = new DashboardActivationService(host, files, new StubLogGroupRepository(), paths =>
        {
            checkedPaths.AddRange(paths.Values);
            return Task.FromResult(paths.ToDictionary(pair => pair.Key, _ => DashboardFileProbeResult.Found));
        });
        await activation.RefreshAllMemberFilesAsync();
        await activation.SetDashboardModifierAsync(modified, 1, Array.Empty<ReplacementPattern>());
        await activation.DrainMembershipRefreshAsync();
        var untouched = dashboard.MemberFiles[1];
        checkedPaths.Clear();
        await activation.RefreshMemberFilesForFileIdsAsync(new Dictionary<string, string> { [first.Id] = first.FilePath });
        Assert.Equal(new[] { first.FilePath }, checkedPaths);
        Assert.Same(untouched, dashboard.MemberFiles[1]);
    }

    [Fact]
    public async Task CopyIntoModifiedDashboard_ResolvesNewRowOffCriticalPath()
    {
        var first = new LogFileEntry { FilePath = @"C:\logs\first.log" };
        var second = new LogFileEntry { FilePath = @"C:\logs\second.log" };
        var files = new StubLogFileRepository();
        await files.AddAsync(first);
        await files.AddAsync(second);
        var dashboard = CreateGroup("dashboard", "Dashboard", first.Id);
        var repo = new RecordingLogGroupRepository();
        await repo.AddAsync(dashboard.Model);
        var host = new DashboardWorkspaceHostStub(dashboard);
        var checkedPaths = new List<string>();
        var activation = new DashboardActivationService(host, files, repo, paths =>
        {
            checkedPaths.AddRange(paths.Values);
            return Task.FromResult(paths.ToDictionary(pair => pair.Key, _ => DashboardFileProbeResult.Found));
        });
        var workspace = new DashboardWorkspaceService(host, files, repo, null, null, activation);
        var date = DateTime.Today.AddDays(-1).ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        await activation.SetDashboardModifierAsync(dashboard, 1, new[] { new ReplacementPattern { FindPattern = ".log", ReplacePattern = "-archive-{yyyyMMdd}.log" } });
        await activation.DrainMembershipRefreshAsync();
        var original = Assert.Single(dashboard.MemberFiles);
        Assert.Equal($@"C:\logs\first-archive-{date}.log", original.FilePath);
        checkedPaths.Clear();
        Assert.True(await workspace.CopyFileToDashboardAsync(dashboard, second.Id));
        await activation.DrainMembershipRefreshAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(original, dashboard.MemberFiles[0]);
        Assert.Equal($@"C:\logs\second-archive-{date}.log", dashboard.MemberFiles[1].FilePath);
        Assert.Equal(new[] { $@"C:\logs\second-archive-{date}.log" }, checkedPaths);
        Assert.False(dashboard.MemberFiles[1].IsChecking);
    }


    [Fact]
    public async Task LargeTree_ReorderProducesOneMoveInsteadOfResetAndRecreation()
    {
        var groups = Enumerable.Range(0, 1000).Select(index => CreateGroup($"group-{index}", $"Dashboard {index}")).ToArray();
        var repo = new RecordingLogGroupRepository();
        for (var index = 0; index < groups.Length; index++)
        {
            groups[index].Model.SortOrder = index;
            await repo.AddAsync(groups[index].Model);
        }
        var host = new DashboardWorkspaceHostStub(groups);
        var service = new DashboardWorkspaceService(host, new StubLogFileRepository(), repo);
        var actions = new List<NotifyCollectionChangedAction>();
        host.Groups.CollectionChanged += (_, args) => actions.Add(args.Action);
        var stopwatch = Stopwatch.StartNew();
        await service.MoveGroupUpAsync(groups[^1]);
        stopwatch.Stop();
        _output.WriteLine($"1,000 groups: in-place move {stopwatch.Elapsed.TotalMilliseconds:F2} ms, {actions.Count} collection event(s), no probes.");
        Assert.Equal(new[] { NotifyCollectionChangedAction.Move }, actions);
        Assert.Same(groups[^1], host.Groups[^2]);
        Assert.All(groups, group => Assert.Contains(group, host.Groups));

        actions.Clear();
        stopwatch.Restart();
        service.RebuildGroupsCollection(await repo.GetAllAsync());
        stopwatch.Stop();
        _output.WriteLine($"Same groups: full rebuild {stopwatch.Elapsed.TotalMilliseconds:F2} ms, {actions.Count} collection events, 1,000 new group objects.");
        Assert.Contains(NotifyCollectionChangedAction.Reset, actions);
        Assert.Equal(1001, actions.Count);
    }

    [Fact]
    public async Task QueuedMembershipStatus_PublishesOnDispatcherWithoutReplacingRow()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var entry = new LogFileEntry { FilePath = @"C:\logs\worker.log" };
            var files = new StubLogFileRepository();
            await files.AddAsync(entry);
            var dashboard = CreateGroup("dashboard", "Dashboard");
            var repo = new RecordingLogGroupRepository();
            await repo.AddAsync(dashboard.Model);
            var host = new DashboardWorkspaceHostStub(dashboard);
            var probes = new ControlledProbeMapBuilder(1);
            var activation = new DashboardActivationService(host, files, repo, probes.InvokeAsync)
            {
                MembershipDispatcher = TestUiDispatcher.Current
            };
            var service = new DashboardWorkspaceService(host, files, repo, null, null, activation);
            await service.CopyFileToDashboardAsync(dashboard, entry.Id);
            await probes.WaitForCallAsync(0).WaitAsync(TimeSpan.FromSeconds(5));
            var row = Assert.Single(dashboard.MemberFiles);
            var changes = 0;
            row.PropertyChanged += (_, _) =>
            {
                Assert.True(dispatcher.CheckAccess());
                changes++;
            };
            await Task.Run(() => probes.CompleteCall(0, DashboardFileProbeResult.AccessDenied));
            await activation.DrainMembershipRefreshAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(changes > 0);
            Assert.Same(row, Assert.Single(dashboard.MemberFiles));
            Assert.True(row.HasError);
        });
    }

    [Fact]
    public async Task ShutdownDuringProbe_CancelsPublicationAndKeepsCommittedMembership()
    {
        var entry = new LogFileEntry { FilePath = @"C:\logs\shutdown.log" };
        var files = new StubLogFileRepository();
        await files.AddAsync(entry);
        var dashboard = CreateGroup("dashboard", "Dashboard");
        var repo = new RecordingLogGroupRepository();
        await repo.AddAsync(dashboard.Model);
        var host = new DashboardWorkspaceHostStub(dashboard);
        var probes = new ControlledProbeMapBuilder(1);
        var activation = new DashboardActivationService(host, files, repo, probes.InvokeAsync);
        var service = new DashboardWorkspaceService(host, files, repo, null, null, activation);
        await service.CopyFileToDashboardAsync(dashboard, entry.Id);
        await probes.WaitForCallAsync(0).WaitAsync(TimeSpan.FromSeconds(5));
        var row = Assert.Single(dashboard.MemberFiles);
        var changes = 0;
        row.PropertyChanged += (_, _) => changes++;
        activation.ShutdownMembershipRefresh();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => activation.DrainMembershipRefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        probes.CompleteCall(0, DashboardFileProbeResult.AccessDenied);
        Assert.Equal(0, changes);
        Assert.False(row.HasError);
        Assert.Equal(entry.Id, Assert.Single(Assert.Single(await repo.GetAllAsync()).FileIds));
    }

    [Fact]
    public async Task StructuralEdits_PreserveObjectsAndEditingWithoutResetOrProbes()
    {
        var folder = CreateGroup("folder", "Folder", LogGroupKind.Branch);
        var dashboard = CreateGroup("dashboard", "Dashboard", "file");
        dashboard.Model.SortOrder = 1;
        var row = new GroupFileMemberViewModel("file", "a.log", @"C:\logs\a.log", false) { IsBatchSelected = true };
        dashboard.ReplaceMemberFiles(new[] { row });
        row.IsBatchSelected = true;
        dashboard.IsExpanded = true;
        dashboard.IsSelected = true;
        dashboard.BeginEdit();
        dashboard.EditName = "Pending name";
        var repo = new RecordingLogGroupRepository();
        await repo.AddAsync(folder.Model);
        await repo.AddAsync(dashboard.Model);
        var host = new DashboardWorkspaceHostStub(folder, dashboard) { ActiveDashboardId = dashboard.Id };
        var activation = new DashboardActivationService(host, new StubLogFileRepository(), repo,
            _ => Task.FromException<Dictionary<string, DashboardFileProbeResult>>(new InvalidOperationException("Structural edits must not probe.")));
        var workspace = new DashboardWorkspaceService(host, new StubLogFileRepository(), repo, null, null, activation);
        var notifications = new List<NotifyCollectionChangedAction>();
        host.Groups.CollectionChanged += (_, args) => notifications.Add(args.Action);
        folder.Children.CollectionChanged += (_, args) => notifications.Add(args.Action);

        await workspace.MoveGroupToAsync(dashboard, folder, DropPlacement.Inside);
        Assert.Same(folder, host.Groups[0]);
        Assert.Same(dashboard, host.Groups[1]);
        Assert.Same(dashboard, Assert.Single(folder.Children));
        Assert.Same(folder, dashboard.Parent);
        Assert.Equal(1, dashboard.Depth);
        Assert.True(folder.IsExpanded);
        Assert.True(dashboard.IsExpanded);
        Assert.True(dashboard.IsSelected);
        Assert.True(dashboard.IsEditing);
        Assert.Equal("Pending name", dashboard.EditName);
        Assert.Same(row, Assert.Single(dashboard.MemberFiles));
        Assert.True(row.IsBatchSelected);

        await workspace.CreateGroupAsync(LogGroupKind.Dashboard);
        var created = host.Groups.Last();
        await workspace.DeleteGroupAsync(created);
        Assert.Same(dashboard, host.Groups[1]);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, notifications);
    }

    [Fact]
    public async Task MoveWhileFiltering_RestoresOriginalExpansionWhenFilterClears()
    {
        var folder = CreateGroup("folder", "Folder", LogGroupKind.Branch);
        var child = CreateGroup("child", "Matching child");
        child.Model.ParentGroupId = folder.Id;
        child.Parent = folder;
        child.Depth = 1;
        folder.Children.Add(child);
        var other = CreateGroup("other", "Other");
        other.Model.SortOrder = 1;
        var repo = new RecordingLogGroupRepository();
        foreach (var group in new[] { folder, child, other })
            await repo.AddAsync(group.Model);
        var host = new DashboardWorkspaceHostStub(folder, child, other) { DashboardTreeFilter = "Matching" };
        var service = new DashboardWorkspaceService(host, new StubLogFileRepository(), repo);
        service.ApplyDashboardTreeFilter();
        Assert.True(folder.IsExpanded);
        await service.MoveGroupUpAsync(other);
        host.DashboardTreeFilter = string.Empty;
        service.ApplyDashboardTreeFilter();
        Assert.False(folder.IsExpanded);
        Assert.Same(folder, host.Groups[1]);
        Assert.Same(child, host.Groups[2]);
    }

    [Fact]
    public async Task Copy_CompletesBeforeBlockedProbe_AndPublishesStatusIntoSameRow()
    {
        var entry = new LogFileEntry { FilePath = @"\\slow-server\logs\app.log" };
        var files = new StubLogFileRepository();
        await files.AddAsync(entry);
        var dashboard = CreateGroup("dashboard", "Dashboard");
        var repo = new RecordingLogGroupRepository();
        await repo.AddAsync(dashboard.Model);
        var host = new DashboardWorkspaceHostStub(dashboard);
        var probes = new ControlledProbeMapBuilder(1);
        var activation = new DashboardActivationService(host, files, repo, probes.InvokeAsync);
        var service = new DashboardWorkspaceService(host, files, repo, null, null, activation);

        Assert.True(await service.CopyFileToDashboardAsync(dashboard, entry.Id).WaitAsync(TimeSpan.FromSeconds(5)));
        await probes.WaitForCallAsync(0).WaitAsync(TimeSpan.FromSeconds(5));
        var member = Assert.Single(dashboard.MemberFiles);
        Assert.True(member.IsChecking);
        Assert.False(member.HasError);
        Assert.Equal(new[] { entry.Id }, Assert.Single(await repo.GetAllAsync()).FileIds);
        member.IsBatchSelected = true;
        probes.CompleteCall(0, DashboardFileProbeResult.AccessDenied);
        await activation.DrainMembershipRefreshAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(member, Assert.Single(dashboard.MemberFiles));
        Assert.False(member.IsChecking);
        Assert.Equal("File unavailable: access denied", member.ErrorMessage);
        Assert.True(member.IsBatchSelected);
        Assert.Equal(1, dashboard.ErroredMemberFileCount);
    }

    [Fact]
    public async Task DeleteDuringProbe_DoesNotResurrectDeletedGroup()
    {
        var entry = new LogFileEntry { FilePath = @"C:\logs\deleted.log" };
        var files = new StubLogFileRepository();
        await files.AddAsync(entry);
        var dashboard = CreateGroup("dashboard", "Dashboard");
        var repo = new RecordingLogGroupRepository();
        await repo.AddAsync(dashboard.Model);
        var host = new DashboardWorkspaceHostStub(dashboard);
        var probes = new ControlledProbeMapBuilder(1);
        var activation = new DashboardActivationService(host, files, repo, probes.InvokeAsync);
        var service = new DashboardWorkspaceService(host, files, repo, null, null, activation);
        await service.CopyFileToDashboardAsync(dashboard, entry.Id);
        await probes.WaitForCallAsync(0).WaitAsync(TimeSpan.FromSeconds(5));
        await service.DeleteGroupAsync(dashboard);
        probes.CompleteCall(0, DashboardFileProbeResult.Found);
        await activation.DrainMembershipRefreshAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(host.Groups);
        Assert.Empty(await repo.GetAllAsync());
    }

    [Fact]
    public async Task SharedPathsAndCachedDuplication_ProbeOnceAndPreserveSourceRows()
    {
        var first = new LogFileEntry { FilePath = @"C:\logs\shared.log" };
        var second = new LogFileEntry { FilePath = @"c:\LOGS\shared.log" };
        var files = new StubLogFileRepository();
        await files.AddAsync(first);
        await files.AddAsync(second);
        var dashboard = CreateGroup("dashboard", "Dashboard", first.Id, second.Id);
        var repo = new RecordingLogGroupRepository();
        await repo.AddAsync(dashboard.Model);
        var host = new DashboardWorkspaceHostStub(dashboard);
        var pathCount = 0;
        var activation = new DashboardActivationService(host, files, repo, paths =>
        {
            pathCount += paths.Count;
            return Task.FromResult(paths.ToDictionary(pair => pair.Key, _ => DashboardFileProbeResult.Found));
        });
        var service = new DashboardWorkspaceService(host, files, repo, null, null, activation);
        await activation.RefreshAllMemberFilesAsync();
        Assert.Equal(1, pathCount);
        var original = dashboard.MemberFiles.ToArray();
        await service.DuplicateGroupAsync(dashboard);
        await activation.DrainMembershipRefreshAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, pathCount);
        Assert.Equal(original, dashboard.MemberFiles);
        Assert.Equal(2, host.Groups[1].MemberFiles.Count);
        Assert.False(host.Groups[1].IsExpanded);
    }

    [Fact]
    public async Task RemoveAndReorderUncachedRows_PerformNoProbesAndPreserveSelection()
    {
        var first = new LogFileEntry { FilePath = @"C:\logs\a.log" };
        var second = new LogFileEntry { FilePath = @"C:\logs\b.log" };
        var files = new StubLogFileRepository();
        await files.AddAsync(first);
        await files.AddAsync(second);
        var dashboard = CreateGroup("dashboard", "Dashboard", first.Id, second.Id);
        var row = new GroupFileMemberViewModel(first.Id, "a.log", first.FilePath, false, "File not found") { IsBatchSelected = true };
        dashboard.ReplaceMemberFiles(new[] { row, new GroupFileMemberViewModel(second.Id, "b.log", second.FilePath, false) });
        row.IsBatchSelected = true;
        var repo = new RecordingLogGroupRepository();
        await repo.AddAsync(dashboard.Model);
        var host = new DashboardWorkspaceHostStub(dashboard);
        var activation = new DashboardActivationService(host, files, repo,
            _ => Task.FromException<Dictionary<string, DashboardFileProbeResult>>(new InvalidOperationException("Removal/reordering must not probe.")));
        var service = new DashboardWorkspaceService(host, files, repo, null, null, activation);
        Assert.True(await service.ReorderFilesInDashboardAsync(dashboard, new[] { second.Id }, first.Id, DropPlacement.Before));
        Assert.Same(row, dashboard.MemberFiles[1]);
        Assert.True(row.IsBatchSelected);
        Assert.Equal("File not found", row.ErrorMessage);
        Assert.True(await service.RemoveFileFromDashboardAsync(dashboard, second.Id));
        Assert.Same(row, Assert.Single(dashboard.MemberFiles));
        Assert.True(row.IsBatchSelected);
        await activation.DrainMembershipRefreshAsync();
    }

    [Fact]
    public async Task BackgroundProbeFailure_DoesNotFailOrUndoCommittedCopy()
    {
        var entry = new LogFileEntry { FilePath = @"C:\logs\app.log" };
        var files = new StubLogFileRepository();
        await files.AddAsync(entry);
        var dashboard = CreateGroup("dashboard", "Dashboard");
        var repo = new RecordingLogGroupRepository();
        await repo.AddAsync(dashboard.Model);
        var host = new DashboardWorkspaceHostStub(dashboard);
        var activation = new DashboardActivationService(host, files, repo,
            _ => Task.FromException<Dictionary<string, DashboardFileProbeResult>>(new IOException("probe failed")));
        var service = new DashboardWorkspaceService(host, files, repo, null, null, activation);
        Assert.True(await service.CopyFileToDashboardAsync(dashboard, entry.Id));
        await Assert.ThrowsAsync<IOException>(() => activation.DrainMembershipRefreshAsync());
        Assert.Equal(entry.Id, Assert.Single(dashboard.MemberFiles).FileId);
        Assert.False(dashboard.MemberFiles[0].IsChecking);
        Assert.Equal(entry.Id, Assert.Single(Assert.Single(await repo.GetAllAsync()).FileIds));
    }
}
