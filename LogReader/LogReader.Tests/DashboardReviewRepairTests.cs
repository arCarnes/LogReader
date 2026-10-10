using LogReader.App.Models;
using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.Core.Models;

namespace LogReader.Tests;

public partial class DashboardWorkspaceServiceTests
{
    [Fact]
    public async Task ReviewRepair_RemovalPrunesModifiedScopeImmediatelyWithoutProbes()
    {
        var (dashboard, host, activation, workspace, checkedPaths) = await CreateModifiedReviewFixtureAsync();
        var paths = dashboard.MemberFiles.Select(row => row.FilePath).ToArray();
        checkedPaths.Clear();

        Assert.True(await workspace.RemoveFileFromDashboardAsync(dashboard, dashboard.Model.FileIds[0]));
        Assert.True(activation.TryGetDashboardEffectivePaths(dashboard.Id, out var remaining));
        Assert.Equal(new[] { paths[1] }, remaining);
        Assert.Equal(paths[1], Assert.Single(dashboard.MemberFiles).FilePath);
        Assert.True(await workspace.RemoveFileFromDashboardAsync(dashboard, dashboard.Model.FileIds[0]));
        Assert.True(activation.TryGetDashboardEffectivePaths(dashboard.Id, out var empty));
        Assert.Empty(empty);
        Assert.Empty(dashboard.MemberFiles);
        Assert.Empty(checkedPaths);
        Assert.Contains(dashboard, host.Groups);
    }

    [Fact]
    public async Task ReviewRepair_MovePrunesModifiedSourceScopeBeforeBackgroundRefresh()
    {
        var (dashboard, host, activation, workspace, _) = await CreateModifiedReviewFixtureAsync();
        var removedPath = dashboard.MemberFiles[0].FilePath;
        await workspace.CreateGroupAsync(LogGroupKind.Dashboard);
        var target = host.Groups.Last();
        Assert.True(await workspace.MoveFilesBetweenDashboardsAsync(dashboard, target,
            new[] { dashboard.Model.FileIds[0] }, null, DropPlacement.Inside));
        Assert.True(activation.TryGetDashboardEffectivePaths(dashboard.Id, out var remaining));
        Assert.DoesNotContain(removedPath, remaining);
        await activation.DrainMembershipRefreshAsync();
        Assert.Single(remaining);
        Assert.Contains(target, host.Groups);
    }

    [Fact]
    public async Task ReviewRepair_RetainedModifiedRowRefreshesSizeOnOpenChangeAndClose()
    {
        var (dashboard, host, activation, _, _) = await CreateModifiedReviewFixtureAsync();
        var row = dashboard.MemberFiles[0];
        row.IsBatchSelected = true;
        using var tab = CreateTab("effective-file", row.FilePath.ToUpperInvariant(), dashboard.Id);
        host.Tabs.Add(tab);
        tab.ActiveSession.FileSizeBytes = 1024;
        var changes = new Dictionary<string, string> { [tab.FileId] = tab.FilePath };
        await activation.RefreshMemberFilesForFileIdsAsync(changes);
        Assert.Equal(GroupFileMemberViewModel.CreateFileSizeText(tab), row.FileSizeText);

        tab.ActiveSession.FileSizeBytes = 2048;
        await activation.RefreshMemberFilesForFileIdsAsync(changes);
        Assert.Equal(GroupFileMemberViewModel.CreateFileSizeText(tab), row.FileSizeText);
        tab.ActiveSession.FileSizeBytes = null;
        await activation.RefreshMemberFilesForFileIdsAsync(changes);
        Assert.Null(row.FileSizeText);
        tab.ActiveSession.FileSizeBytes = 4096;
        await activation.RefreshMemberFilesForFileIdsAsync(changes);
        Assert.NotNull(row.FileSizeText);
        host.Tabs.Remove(tab);
        await activation.RefreshMemberFilesForFileIdsAsync(changes);
        Assert.Null(row.FileSizeText);
        Assert.Same(row, dashboard.MemberFiles[0]);
        Assert.True(row.IsBatchSelected);
    }

    [Fact]
    public async Task ReviewRepair_SizeReconciliationKeepsPendingStatusPublicationValid()
    {
        var entry = new LogFileEntry { FilePath = @"C:\logs\size.log" };
        var files = new StubLogFileRepository();
        await files.AddAsync(entry);
        var dashboard = CreateGroup("dashboard", "Dashboard", entry.Id);
        var host = new DashboardWorkspaceHostStub(dashboard);
        var pending = new TaskCompletionSource<Dictionary<string, DashboardFileProbeResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var block = false;
        IReadOnlyDictionary<string, string>? arguments = null;
        var activation = new DashboardActivationService(host, files, new StubLogGroupRepository(), paths =>
        {
            if (!block)
                return Task.FromResult(paths.ToDictionary(pair => pair.Key, _ => DashboardFileProbeResult.Found));
            arguments = paths;
            started.SetResult();
            return pending.Task;
        });
        await activation.SetDashboardModifierAsync(dashboard, 1, ReviewModifierPatterns());
        await activation.DrainMembershipRefreshAsync();
        var row = Assert.Single(dashboard.MemberFiles);
        block = true;
        var refresh = activation.RefreshMemberFilesForFileIdsAsync(new Dictionary<string, string> { [entry.Id] = row.FilePath });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var revision = row.PresentationRevision;
        using var tab = CreateTab("effective-file", row.FilePath, dashboard.Id);
        tab.ActiveSession.FileSizeBytes = 1024;
        host.Tabs.Add(tab);
        await activation.ReconcileMembershipAsync(new[] { dashboard.Id }, checkNewPaths: false);
        Assert.Equal(GroupFileMemberViewModel.CreateFileSizeText(tab), row.FileSizeText);
        Assert.Equal(revision, row.PresentationRevision);
        Assert.True(row.IsChecking);
        host.Tabs.Remove(tab);
        pending.SetResult(arguments!.ToDictionary(pair => pair.Key, _ => DashboardFileProbeResult.AccessDenied));
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(row, Assert.Single(dashboard.MemberFiles));
        Assert.False(row.IsChecking);
        Assert.Equal(DashboardFileProbeResult.AccessDenied.ErrorMessage, row.ErrorMessage);
    }

    [Fact]
    public async Task ReviewRepair_QueuedModifiedCaseVariantsShareProbeAndReceiveSameStatus()
    {
        var (dashboard, _, _, _, checkedPaths) = await CreateModifiedReviewFixtureAsync(caseVariants: true);
        Assert.Single(checkedPaths);
        Assert.Equal(2, dashboard.MemberFiles.Count);
        Assert.All(dashboard.MemberFiles, row =>
        {
            Assert.False(row.IsChecking);
            Assert.Equal(DashboardFileProbeResult.AccessDenied.ErrorMessage, row.ErrorMessage);
        });
    }

    [Fact]
    public async Task ReviewRepair_FullModifiedCaseVariantsReceiveSameStatus()
    {
        var first = new LogFileEntry { FilePath = @"C:\logs\same.log" };
        var second = new LogFileEntry { FilePath = @"c:\LOGS\same.log" };
        var files = new StubLogFileRepository();
        await files.AddAsync(first);
        await files.AddAsync(second);
        var dashboard = CreateGroup("dashboard", "Dashboard", first.Id, second.Id);
        var host = new DashboardWorkspaceHostStub(dashboard);
        var checkedPaths = new List<string>();
        var activation = new DashboardActivationService(host, files, new StubLogGroupRepository(), paths =>
        {
            checkedPaths.AddRange(paths.Values);
            return Task.FromResult(paths.ToDictionary(pair => pair.Key, _ => DashboardFileProbeResult.AccessDenied));
        });
        var date = DateTime.Today.AddDays(-1).ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        using var tab = CreateTab("effective-file", $@"C:\logs\same-{date}.log", dashboard.Id);
        host.Tabs.Add(tab);
        await activation.SetDashboardModifierAsync(dashboard, 1, ReviewModifierPatterns());
        await activation.DrainMembershipRefreshAsync();
        host.Tabs.Remove(tab);
        checkedPaths.Clear();
        await activation.RefreshAllMemberFilesAsync();
        Assert.Equal(2, checkedPaths.Count); // One base path and one modified path.
        Assert.All(dashboard.MemberFiles, row => Assert.Equal(DashboardFileProbeResult.AccessDenied.ErrorMessage, row.ErrorMessage));
    }

    [Fact]
    public async Task ReviewRepair_FileIdStatusesRemainCaseSensitive()
    {
        var first = new LogFileEntry { Id = "file", FilePath = @"C:\logs\first.log" };
        var second = new LogFileEntry { Id = "FILE", FilePath = @"C:\logs\second.log" };
        var files = new StubLogFileRepository();
        await files.AddAsync(first);
        await files.AddAsync(second);
        var dashboard = CreateGroup("dashboard", "Dashboard", first.Id, second.Id);
        var activation = new DashboardActivationService(new DashboardWorkspaceHostStub(dashboard), files, new StubLogGroupRepository(),
            paths => Task.FromResult(paths.ToDictionary(pair => pair.Key, pair => pair.Key == first.Id
                ? DashboardFileProbeResult.Found : DashboardFileProbeResult.AccessDenied, StringComparer.Ordinal)));
        await activation.RefreshAllMemberFilesAsync();
        Assert.Null(dashboard.MemberFiles[0].ErrorMessage);
        Assert.Equal(DashboardFileProbeResult.AccessDenied.ErrorMessage, dashboard.MemberFiles[1].ErrorMessage);
    }

    private static ReplacementPattern[] ReviewModifierPatterns()
        => new[] { new ReplacementPattern { FindPattern = ".log", ReplacePattern = "-{yyyyMMdd}.log" } };

    private static async Task<(LogGroupViewModel Dashboard, DashboardWorkspaceHostStub Host,
        DashboardActivationService Activation, DashboardWorkspaceService Workspace, List<string> CheckedPaths)>
        CreateModifiedReviewFixtureAsync(bool caseVariants = false)
    {
        var first = new LogFileEntry { FilePath = @"C:\logs\first.log" };
        var second = new LogFileEntry { FilePath = caseVariants ? @"c:\LOGS\first.log" : @"C:\logs\second.log" };
        var files = new StubLogFileRepository();
        await files.AddAsync(first);
        await files.AddAsync(second);
        var dashboard = CreateGroup("dashboard", "Dashboard", first.Id, second.Id);
        var repo = new RecordingLogGroupRepository();
        await repo.AddAsync(dashboard.Model);
        var host = new DashboardWorkspaceHostStub(dashboard);
        var checkedPaths = new List<string>();
        var activation = new DashboardActivationService(host, files, repo, paths =>
        {
            checkedPaths.AddRange(paths.Values);
            return Task.FromResult(paths.ToDictionary(pair => pair.Key, _ => DashboardFileProbeResult.AccessDenied));
        });
        var workspace = new DashboardWorkspaceService(host, files, repo, null, null, activation);
        await activation.SetDashboardModifierAsync(dashboard, 1, ReviewModifierPatterns());
        await activation.DrainMembershipRefreshAsync();
        return (dashboard, host, activation, workspace, checkedPaths);
    }
}
