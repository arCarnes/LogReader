using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

namespace LogReader.Tests;

public partial class DashboardWorkspaceServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScopePublication_ResolvedMembershipRebuildsVisibleTabsBeforeProbing(bool readdCachedPath)
    {
        await WpfTestHost.RunAsync(async () =>
        {
            using var appPaths = AppPaths.BeginTestScope(rootPath: Path.Combine(Path.GetTempPath(), "WeezTailScopePublication_" + Guid.NewGuid().ToString("N")));
            var first = new LogFileEntry { FilePath = @"C:\logs\first.log" };
            var second = new LogFileEntry { FilePath = @"C:\logs\second.log" };
            var files = new StubLogFileRepository();
            await files.AddAsync(first);
            await files.AddAsync(second);
            var dashboard = CreateGroup("dashboard", "Dashboard", readdCachedPath ? new[] { first.Id, second.Id } : new[] { first.Id });
            var groups = new RecordingLogGroupRepository();
            await groups.AddAsync(dashboard.Model);
            var reference = new MainViewModelReference();
            var host = new DashboardWorkspaceHostAdapter(reference);
            var probes = new ControlledProbeMapBuilder(1);
            var blockProbe = false;
            var probeCalls = 0;
            var activation = new DashboardActivationService(host, files, groups, paths =>
            {
                probeCalls++;
                return blockProbe ? probes.InvokeAsync(paths)
                    : Task.FromResult(paths.ToDictionary(pair => pair.Key, _ => DashboardFileProbeResult.Found));
            });
            var workspace = new DashboardWorkspaceService(host, files, groups, null, null, activation);
            using var vm = TestMainViewModelFactory.Create(files, groups, new StubSettingsRepository(),
                new StubLogReaderService(), new StubSearchService(), new StubFileTailService(), new FileEncodingDetectionService(),
                workspaceViewModelReference: reference, dashboardWorkspace: workspace, dashboardActivation: activation);
            vm.Groups.Add(dashboard);
            await activation.SetDashboardModifierAsync(dashboard, 1, ReviewModifierPatterns());
            await activation.DrainMembershipRefreshAsync();
            var date = DateTime.Today.AddDays(-1).ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
            using var firstTab = CreateTab("effective-first", $@"C:\logs\first-{date}.log", dashboard.Id);
            using var secondTab = CreateTab("effective-second", $@"C:\logs\second-{date}.log", dashboard.Id);
            vm.BeginTabCollectionNotificationSuppression();
            vm.Tabs.Add(firstTab);
            vm.Tabs.Add(secondTab);
            await vm.EndTabCollectionNotificationSuppressionAsync();
            vm.ToggleGroupSelection(dashboard);
            if (readdCachedPath)
                Assert.True(await workspace.RemoveFileFromDashboardAsync(dashboard, second.Id));
            Assert.Equal(new[] { firstTab }, vm.FilteredTabs);

            TabMemberRefreshRequest? queued = null;
            activation.QueueMemberRefresh = request => { queued = request; return Task.CompletedTask; };
            Assert.True(await workspace.CopyFileToDashboardAsync(dashboard, second.Id));
            Assert.Equal(new[] { firstTab }, vm.FilteredTabs);
            Assert.NotNull(queued);
            var notifications = 0;
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(vm.FilteredTabs))
                {
                    Assert.True(dispatcher.CheckAccess());
                    notifications++;
                }
            };
            var callsBefore = probeCalls;
            blockProbe = true;
            var refresh = activation.RefreshQueuedMembersAsync(queued!, CancellationToken.None);
            if (!readdCachedPath)
                await probes.WaitForCallAsync(0);
            else
                await refresh.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, vm.FilteredTabs.Count());
            Assert.Contains(secondTab, vm.FilteredTabs);
            Assert.Equal(1, notifications);
            if (readdCachedPath)
                Assert.Equal(callsBefore, probeCalls);
            else
            {
                Assert.False(refresh.IsCompleted);
                probes.CompleteCall(0, DashboardFileProbeResult.Found);
                await refresh.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, notifications);
            }
        });
    }

    [Fact]
    public async Task ScopePublication_UnchangedCaseVariantsAndInactiveDashboardsDoNotNotify()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var first = new LogFileEntry { FilePath = @"C:\logs\first.log" };
            var second = new LogFileEntry { FilePath = @"C:\logs\second.log" };
            var files = new StubLogFileRepository();
            await files.AddAsync(first);
            await files.AddAsync(second);
            var dashboard = CreateGroup("dashboard", "Dashboard", first.Id);
            var inactive = CreateGroup("inactive", "Inactive", second.Id);
            var host = new DashboardWorkspaceHostStub(dashboard, inactive) { ActiveDashboardId = dashboard.Id };
            var activation = new DashboardActivationService(host, files, new StubLogGroupRepository(), paths =>
                Task.FromResult(paths.ToDictionary(pair => pair.Key, _ => DashboardFileProbeResult.Found)))
            { MembershipDispatcher = TestUiDispatcher.Current };
            await activation.SetDashboardModifierAsync(dashboard, 1, ReviewModifierPatterns());
            await activation.DrainMembershipRefreshAsync();
            Assert.Equal(1, host.NotifyFilteredTabsChangedCallCount);
            var baseline = host.NotifyFilteredTabsChangedCallCount;
            await activation.ReconcileMembershipAsync(new[] { dashboard.Id }, checkNewPaths: false);
            await activation.RefreshMemberFilesForFileIdsAsync(new Dictionary<string, string> { [first.Id] = dashboard.MemberFiles[0].FilePath });
            await activation.SetDashboardModifierAsync(inactive, 1, ReviewModifierPatterns());
            await activation.DrainMembershipRefreshAsync();
            first.FilePath = first.FilePath.ToUpperInvariant();
            dashboard.MemberFiles[0].RequiresPathResolution = true;
            await activation.RefreshQueuedMembersAsync(new TabMemberRefreshRequest(false, new Dictionary<string, string>(),
                new HashSet<string> { dashboard.Id }), CancellationToken.None);
            Assert.Equal(baseline, host.NotifyFilteredTabsChangedCallCount);
        });
    }

    [Theory]
    [InlineData("membership")]
    [InlineData("cancel")]
    [InlineData("shutdown")]
    public async Task ScopePublication_RejectedCancelledOrShutdownResultsDoNotNotify(string rejection)
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var first = new LogFileEntry { FilePath = @"C:\logs\first.log" };
            var second = new LogFileEntry { FilePath = @"C:\logs\second.log" };
            var files = new BlockingRefreshMetadataRepository();
            await files.AddAsync(first);
            await files.AddAsync(second);
            var dashboard = CreateGroup("dashboard", "Dashboard", first.Id);
            var host = new DashboardWorkspaceHostStub(dashboard) { ActiveDashboardId = dashboard.Id };
            var activation = new DashboardActivationService(host, files, new StubLogGroupRepository(), paths =>
                Task.FromResult(paths.ToDictionary(pair => pair.Key, _ => DashboardFileProbeResult.Found)))
            { MembershipDispatcher = TestUiDispatcher.Current, QueueMemberRefresh = _ => Task.CompletedTask };
            await activation.SetDashboardModifierAsync(dashboard, 1, ReviewModifierPatterns());
            dashboard.Model.FileIds.Add(second.Id);
            await activation.ReconcileMembershipAsync(new[] { dashboard.Id }, checkNewPaths: false);
            var baseline = host.NotifyFilteredTabsChangedCallCount;
            using var cancellation = new CancellationTokenSource();
            files.BlockNextRead = true;
            var refresh = activation.RefreshQueuedMembersAsync(new TabMemberRefreshRequest(false, new Dictionary<string, string>(),
                new HashSet<string> { dashboard.Id }), cancellation.Token);
            await files.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            switch (rejection)
            {
                case "membership":
                    dashboard.Model.FileIds.Remove(second.Id);
                    await activation.ReconcileMembershipAsync(new[] { dashboard.Id }, checkNewPaths: false);
                    break;
                case "cancel": cancellation.Cancel(); break;
                case "shutdown": activation.ShutdownMembershipRefresh(); break;
            }
            files.ReleaseRead.SetResult();
            if (rejection == "cancel")
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
            else
                await refresh.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(baseline, host.NotifyFilteredTabsChangedCallCount);
        });
    }

    [Theory]
    [InlineData("date")]
    [InlineData("pattern")]
    [InlineData("same")]
    [InlineData("apply")]
    [InlineData("clear")]
    [InlineData("unchanged")]
    [InlineData("committed")]
    public async Task AdHocNamePublication_FullRefreshPreservesCurrentMappingAndPublishesDashboardResults(string change)
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var date = DateTime.Today.AddDays(-1).ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
            var olderDate = DateTime.Today.AddDays(-2).ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
            var initialPatterns = new[] { new ReplacementPattern { FindPattern = ".log", ReplacePattern = ".log{yyyyMMdd}" } };
            var nextPatterns = change == "pattern"
                ? new[] { new ReplacementPattern { FindPattern = ".log", ReplacePattern = "-alt-{yyyyMMdd}.log" } } : initialPatterns;
            var baseEntry = new LogFileEntry { Id = "base", FilePath = @"C:\logs\api.log", DisplayName = change is "date" or "pattern" or "same" ? "Old API" : "API" };
            var oldEntry = new LogFileEntry { Id = "old", FilePath = baseEntry.FilePath + date };
            var nextEntry = new LogFileEntry { Id = "next", FilePath = change == "pattern" ? $@"C:\logs\api-alt-{date}.log" : baseEntry.FilePath + olderDate };
            var dashboardEntry = new LogFileEntry { Id = "dashboard-file", FilePath = @"C:\logs\dashboard.log" };
            var files = new SnapshotAdHocNameRepository();
            foreach (var entry in new[] { baseEntry, oldEntry, nextEntry, dashboardEntry })
                await files.AddAsync(entry);
            var dashboard = CreateGroup("dashboard", "Dashboard", dashboardEntry.Id);
            var host = new DashboardWorkspaceHostStub(dashboard);
            using var baseTab = CreateTab(baseEntry.Id, baseEntry.FilePath);
            using var oldTab = CreateTab(oldEntry.Id, oldEntry.FilePath);
            using var nextTab = CreateTab(nextEntry.Id, nextEntry.FilePath);
            host.Tabs.Add(baseTab);
            var probes = new ControlledProbeMapBuilder(1);
            var blockBaseProbe = false;
            var activation = new DashboardActivationService(host, files, new StubLogGroupRepository(), paths =>
                blockBaseProbe && paths.Values.Contains(dashboardEntry.FilePath) ? probes.InvokeAsync(paths)
                    : Task.FromResult(paths.ToDictionary(pair => pair.Key, _ => DashboardFileProbeResult.Found)))
            { MembershipDispatcher = TestUiDispatcher.Current };
            if (change != "apply")
                await activation.SetAdHocModifierAsync(1, initialPatterns);
            host.Tabs.Add(oldTab);
            host.Tabs.Add(nextTab);
            await activation.RefreshAllMemberFilesAsync();
            if (change != "apply")
                host.Tabs.Remove(baseTab);
            blockBaseProbe = true;
            var fullRefresh = activation.RefreshAllMemberFilesAsync();
            await probes.WaitForCallAsync(0);
            baseEntry.DisplayName = "API";
            switch (change)
            {
                case "date": await activation.SetAdHocModifierAsync(2, nextPatterns); break;
                case "pattern": await activation.SetAdHocModifierAsync(1, nextPatterns); break;
                case "same": await activation.SetAdHocModifierAsync(1, initialPatterns); break;
                case "apply": await activation.SetAdHocModifierAsync(1, initialPatterns); break;
                case "clear": await activation.ClearAdHocModifierAsync(); break;
                case "committed": activation.ApplyCommittedDisplayNames(new Dictionary<string, string?> { [baseEntry.Id] = "Committed API" }); break;
            }
            var currentTab = change is "date" or "pattern" ? nextTab : oldTab;
            var expectedName = change == "committed" ? "Committed API" : change == "clear" ? null : "API";
            Assert.Equal(expectedName, currentTab.CustomDisplayName);
            probes.CompleteCall(0, DashboardFileProbeResult.Missing);
            await fullRefresh.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(expectedName, currentTab.CustomDisplayName);
            if (change == "clear")
                Assert.False(activation.TryGetAdHocEffectivePaths(out _));
            else
            {
                Assert.True(activation.TryGetAdHocEffectivePaths(out var paths));
                Assert.Contains(currentTab.FilePath, paths);
            }
            Assert.Equal(DashboardFileProbeResult.Missing.ErrorMessage, Assert.Single(dashboard.MemberFiles).ErrorMessage);
        });
    }

    private sealed class SnapshotAdHocNameRepository : StubLogFileRepository, ILogFileRepository
    {
        public new async Task<IReadOnlyDictionary<string, LogFileEntry>> GetByPathsAsync(IEnumerable<string> filePaths)
        {
            var entries = await base.GetByPathsAsync(filePaths);
            return entries.ToDictionary(pair => pair.Key, pair => new LogFileEntry
            {
                Id = pair.Value.Id, FilePath = pair.Value.FilePath, DisplayName = pair.Value.DisplayName
            }, StringComparer.OrdinalIgnoreCase);
        }
    }
}
