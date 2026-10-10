using LogReader.App.Models;
using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

namespace LogReader.Tests;

public partial class DashboardWorkspaceServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullRefresh_CopyDuringAwaitPreservesModifiedRowsAndEffectivePaths(bool blockMetadata)
    {
        var first = new LogFileEntry { FilePath = @"C:\logs\first.log" };
        var second = new LogFileEntry { FilePath = @"C:\logs\second.log" };
        var files = new BlockingRefreshMetadataRepository();
        await files.AddAsync(first);
        await files.AddAsync(second);
        var dashboard = CreateGroup("dashboard", "Dashboard", first.Id);
        var groups = new RecordingLogGroupRepository();
        await groups.AddAsync(dashboard.Model);
        var host = new DashboardWorkspaceHostStub(dashboard);
        var probes = new ControlledProbeMapBuilder(1);
        var blockBaseProbe = false;
        var activation = new DashboardActivationService(host, files, groups, paths =>
            blockBaseProbe && paths.Values.Contains(first.FilePath)
                ? probes.InvokeAsync(paths)
                : Task.FromResult(paths.ToDictionary(pair => pair.Key, _ => DashboardFileProbeResult.Found)));
        var workspace = new DashboardWorkspaceService(host, files, groups, null, null, activation);
        await activation.SetDashboardModifierAsync(dashboard, 1, ReviewModifierPatterns());
        await activation.DrainMembershipRefreshAsync();
        blockBaseProbe = true;
        files.BlockNextRead = blockMetadata;
        var fullRefresh = activation.RefreshAllMemberFilesAsync();
        if (blockMetadata)
            await files.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        else
            await probes.WaitForCallAsync(0);

        Assert.True(await workspace.CopyFileToDashboardAsync(dashboard, second.Id));
        await activation.DrainMembershipRefreshAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var currentRows = dashboard.MemberFiles.ToArray();
        Assert.Equal(2, currentRows.Length);
        Assert.True(activation.TryGetDashboardEffectivePaths(dashboard.Id, out var pathsBefore));
        Assert.Equal(2, pathsBefore.Count);

        if (blockMetadata)
        {
            files.ReleaseRead.SetResult();
            await probes.WaitForCallAsync(0);
        }
        probes.CompleteCall(0, DashboardFileProbeResult.Found);
        await fullRefresh.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(currentRows, dashboard.MemberFiles);
        Assert.True(activation.TryGetDashboardEffectivePaths(dashboard.Id, out var pathsAfter));
        Assert.True(pathsAfter.SetEquals(currentRows.Select(row => row.FilePath)));
        Assert.False(dashboard.MemberFiles[1].HasError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullRefresh_PreservesNewerModifiedStatusAndPendingPublication(bool leaveNewerProbePending)
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var first = new LogFileEntry { FilePath = @"C:\logs\first.log" };
            var blocker = new LogFileEntry { FilePath = @"C:\logs\blocker.log" };
            var untouched = new LogFileEntry { FilePath = @"C:\logs\untouched.log" };
            var files = new StubLogFileRepository();
            await files.AddAsync(first);
            await files.AddAsync(blocker);
            await files.AddAsync(untouched);
            var dashboard = CreateGroup("dashboard", "Dashboard", first.Id);
            var other = CreateGroup("other", "Other", blocker.Id);
            var untouchedDashboard = CreateGroup("untouched", "Untouched", untouched.Id);
            var host = new DashboardWorkspaceHostStub(dashboard, other, untouchedDashboard);
            var probes = new ControlledProbeMapBuilder(leaveNewerProbePending ? 3 : 2);
            var blockModifiedProbes = false;
            var activation = new DashboardActivationService(host, files, new StubLogGroupRepository(), paths =>
            {
                if (blockModifiedProbes && paths.Values.Any(path => path != first.FilePath && path != blocker.FilePath && path != untouched.FilePath))
                    return probes.InvokeAsync(paths);
                return Task.FromResult(paths.ToDictionary(pair => pair.Key, pair => blockModifiedProbes && pair.Value == untouched.FilePath
                    ? DashboardFileProbeResult.Missing : DashboardFileProbeResult.Found));
            }) { MembershipDispatcher = TestUiDispatcher.Current };
            await activation.SetDashboardModifierAsync(dashboard, 1, ReviewModifierPatterns());
            await activation.DrainMembershipRefreshAsync();
            await activation.SetDashboardModifierAsync(other, 1, ReviewModifierPatterns());
            await activation.DrainMembershipRefreshAsync();
            await activation.SetDashboardModifierAsync(untouchedDashboard, 1, ReviewModifierPatterns());
            await activation.DrainMembershipRefreshAsync();
            var row = Assert.Single(dashboard.MemberFiles);
            using var tab = CreateTab("effective-file", row.FilePath, dashboard.Id);
            tab.ActiveSession.FileSizeBytes = 1024;
            host.Tabs.Add(tab);
            await activation.ReconcileMembershipAsync(new[] { dashboard.Id }, checkNewPaths: false);
            blockModifiedProbes = true;

            // A shared pending path keeps the full refresh blocked after its first path completes.
            var otherRefresh = activation.RefreshMemberFilesForFileIdsAsync(new Dictionary<string, string>
            {
                [blocker.Id] = other.MemberFiles[0].FilePath
            });
            await probes.WaitForCallAsync(0);
            var fullRefresh = activation.RefreshAllMemberFilesAsync();
            await probes.WaitForCallAsync(1);
            host.Tabs.Remove(tab);
            var changes = new Dictionary<string, string> { [tab.FileId] = tab.FilePath };
            var targetedStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            row.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(row.IsChecking) && row.IsChecking)
                    targetedStarted.TrySetResult();
            };
            var targetedRefresh = activation.RefreshMemberFilesForFileIdsAsync(changes);
            await targetedStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            probes.CompleteCall(1, DashboardFileProbeResult.Missing);
            await targetedRefresh.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(DashboardFileProbeResult.Missing.ErrorMessage, row.ErrorMessage);
            Assert.Null(row.FileSizeText);

            Task? pendingRefresh = null;
            var revision = row.PresentationRevision;
            if (leaveNewerProbePending)
            {
                pendingRefresh = activation.RefreshMemberFilesForFileIdsAsync(changes);
                await probes.WaitForCallAsync(2);
                Assert.True(row.IsChecking);
                revision = row.PresentationRevision;
            }
            probes.CompleteCall(0, DashboardFileProbeResult.Found);
            await otherRefresh.WaitAsync(TimeSpan.FromSeconds(5));
            await fullRefresh.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(host.Tabs);
            Assert.Same(row, Assert.Single(dashboard.MemberFiles));
            Assert.Equal(DashboardFileProbeResult.Missing.ErrorMessage, row.ErrorMessage);
            Assert.Null(row.FileSizeText);
            Assert.Equal(revision, row.PresentationRevision);
            Assert.Equal(leaveNewerProbePending, row.IsChecking);
            Assert.Equal(DashboardFileProbeResult.Missing.ErrorMessage, Assert.Single(untouchedDashboard.MemberFiles).ErrorMessage);
            Assert.False(Assert.Single(other.MemberFiles).HasError);

            if (pendingRefresh != null)
            {
                probes.CompleteCall(2, DashboardFileProbeResult.AccessDenied);
                await pendingRefresh.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(row.IsChecking);
                Assert.Equal(DashboardFileProbeResult.AccessDenied.ErrorMessage, row.ErrorMessage);
            }
        });
    }

    [Fact]
    public async Task FullRefresh_DoesNotRecreateAnAbsentRowPreservedByNewerReconciliation()
    {
        var entry = new LogFileEntry { FilePath = @"C:\logs\absent.log" };
        var files = new StubLogFileRepository();
        await files.AddAsync(entry);
        var dashboard = CreateGroup("dashboard", "Dashboard", entry.Id);
        var host = new DashboardWorkspaceHostStub(dashboard);
        var probes = new ControlledProbeMapBuilder(1);
        var blockModifiedProbe = false;
        var activation = new DashboardActivationService(host, files, new StubLogGroupRepository(), paths =>
            blockModifiedProbe && paths.Values.Any(path => path != entry.FilePath)
                ? probes.InvokeAsync(paths)
                : Task.FromResult(paths.ToDictionary(pair => pair.Key, _ => DashboardFileProbeResult.Found)));
        await activation.SetDashboardModifierAsync(dashboard, 1, ReviewModifierPatterns());
        await activation.DrainMembershipRefreshAsync();
        blockModifiedProbe = true;
        var fullRefresh = activation.RefreshAllMemberFilesAsync();
        await probes.WaitForCallAsync(0);
        await files.DeleteAsync(entry.Id);
        await activation.ReconcileMembershipAsync(new[] { dashboard.Id }, checkNewPaths: false);
        Assert.Empty(dashboard.MemberFiles);
        probes.CompleteCall(0, DashboardFileProbeResult.Missing);
        await fullRefresh.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(dashboard.MemberFiles);
    }

    private sealed class BlockingRefreshMetadataRepository : StubLogFileRepository, ILogFileRepository
    {
        public bool BlockNextRead { get; set; }
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public new async Task<IReadOnlyDictionary<string, LogFileEntry>> GetByIdsAsync(IEnumerable<string> ids)
        {
            var entries = await base.GetByIdsAsync(ids);
            if (BlockNextRead)
            {
                BlockNextRead = false;
                ReadStarted.SetResult();
                await ReleaseRead.Task;
            }
            return entries;
        }
    }
}
