namespace LogReader.Tests;

using System.IO;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public sealed partial class DashboardLoadingResponsivenessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverlappingBulkOpenAndDashboardLoad_StayFrozenUntilBothComplete(bool completeDashboardFirst)
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var files = new OverlappingLoadRepository();
            var groups = new StubLogGroupRepository();
            var empty = new LogGroup { Name = "Empty" };
            await groups.AddAsync(empty);
            await using var fixture = await CreateAsync(fileRepository: files, groupRepository: groups);
            var vm = fixture.ViewModel;
            var group = vm.Groups.Single(group => group.Name == (completeDashboardFirst ? "Empty" : "A"));
            var path = Path.Combine(_root, "adhoc.log");
            await File.WriteAllTextAsync(path, "ad hoc");
            files.HeldOpenPath = path;
            if (!completeDashboardFirst)
                files.HeldFileId = group.MemberFiles.Single().FileId;
            fixture.Reader.Release();
            var bulk = vm.OpenFilePathsAsync([path]);
            Task? dashboard = null;
            try
            {
                await files.OpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, vm.DashboardLoadDepth);
                dashboard = vm.HandleDashboardGroupInvokedAsync(group);
                if (completeDashboardFirst)
                {
                    await dashboard.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.False(bulk.IsCompleted);
                }
                else
                {
                    await files.ResolutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.Equal(2, vm.DashboardLoadDepth);
                    files.ReleaseOpen.TrySetResult();
                    await bulk.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.False(dashboard.IsCompleted);
                }

                Assert.Equal(1, vm.DashboardLoadDepth);
                Assert.True(vm.IsDashboardLoading);
                Assert.True(vm.IsLoadAffectingActionFrozen);
                Assert.False(vm.AreLoadAffectingActionsEnabled);
                files.ReleaseOpen.TrySetResult();
                files.ReleaseResolution.TrySetResult();
                await Task.WhenAll(bulk, dashboard).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, vm.DashboardLoadDepth);
                Assert.False(vm.IsDashboardLoading);
                Assert.True(vm.AreLoadAffectingActionsEnabled);
            }
            finally
            {
                files.ReleaseOpen.TrySetResult();
                files.ReleaseResolution.TrySetResult();
                await Task.WhenAll(bulk, dashboard ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
            }
        });
    }

    private sealed class OverlappingLoadRepository : StubLogFileRepository, ILogFileRepository
    {
        public string? HeldOpenPath { get; set; }
        public string? HeldFileId { get; set; }
        public TaskCompletionSource OpenStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResolutionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseOpen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseResolution { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<LogFileEntry> ILogFileRepository.GetOrCreateByPathAsync(string filePath, DateTime? lastOpenedAtUtc)
        {
            if (filePath == HeldOpenPath)
            {
                OpenStarted.TrySetResult();
                await ReleaseOpen.Task;
            }
            return await GetOrCreateByPathAsync(filePath, lastOpenedAtUtc);
        }

        async Task<IReadOnlyDictionary<string, LogFileEntry>> ILogFileRepository.GetByIdsAsync(IEnumerable<string> ids)
        {
            var requested = ids.ToArray();
            if (HeldFileId != null && requested.Contains(HeldFileId))
            {
                ResolutionStarted.TrySetResult();
                await ReleaseResolution.Task;
            }
            return await GetByIdsAsync(requested);
        }
    }
}
