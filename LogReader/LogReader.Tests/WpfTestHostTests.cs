namespace LogReader.Tests;

using LogReader.App.ViewModels;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;
using LogReader.Testing;
using System.Windows;
using System.Windows.Threading;

public class WpfTestHostTests
{
    [Fact]
    public async Task RunAsync_DispatcherException_IsReturnedToTheTestRunner()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => WpfTestHost.RunAsync(async () =>
        {
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(
                () => throw new InvalidOperationException("dispatcher failure"));
            await WpfTestHost.FlushAsync();
        }));

        Assert.Equal("dispatcher failure", exception.Message);
    }

    [Fact]
    public async Task RunAsync_ActionFailure_ClosesWindowsAndAllowsNextApplication()
    {
        var windowClosed = false;
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => WpfTestHost.RunAsync(() =>
        {
            var window = new Window();
            window.Closed += (_, _) => windowClosed = true;
            WpfTestHost.ShowHidden(window);
            throw new InvalidOperationException("action failure");
        }));

        Assert.Equal("action failure", exception.Message);
        Assert.True(windowClosed);

        await WpfTestHost.RunAsync(() =>
        {
            Assert.NotNull(Application.Current);
            Assert.Empty(Application.Current.Windows.OfType<Window>());
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ShowHidden_RealizesAnInvisibleOffscreenWindow()
    {
        await WpfTestHost.RunAsync(() =>
        {
            var window = new Window { Width = 320, Height = 180 };
            WpfTestHost.ShowHidden(window);

            Assert.True(window.IsVisible);
            Assert.Equal(1, window.Opacity);
            Assert.False(window.ShowInTaskbar);
            Assert.True(window.Left < SystemParameters.VirtualScreenLeft);
            Assert.True(window.Top < SystemParameters.VirtualScreenTop);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task QueuedMemberRefresh_MutatesMemberCollectionOnWpfDispatcher()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var fileRepo = new StubLogFileRepository();
            var groupRepo = new StubLogGroupRepository();
            var tailService = new StubFileTailService();
            const string fileId = "file-1";
            const string filePath = @"C:\test\dispatcher.log";
            await fileRepo.AddAsync(new LogFileEntry { Id = fileId, FilePath = filePath });
            await groupRepo.AddAsync(new LogGroup
            {
                Id = "dashboard-1",
                Name = "Dashboard",
                Kind = LogGroupKind.Dashboard,
                FileIds = new List<string> { fileId }
            });
            using var viewModel = TestMainViewModelFactory.Create(
                fileRepo,
                groupRepo,
                new StubSettingsRepository(),
                new StubLogReaderService(),
                new StubSearchService(),
                tailService,
                new FileEncodingDetectionService(),
                enableLifecycleTimer: false);
            await viewModel.InitializeAsync();

            var group = Assert.Single(viewModel.Groups);
            var mutationCount = 0;
            group.MemberFiles.CollectionChanged += (_, _) =>
            {
                Assert.True(dispatcher.CheckAccess());
                mutationCount++;
            };

            viewModel.BeginTabCollectionNotificationSuppression();
            viewModel.Tabs.Add(new LogTabViewModel(
                fileId,
                filePath,
                new StubLogReaderService(),
                tailService,
                new FileEncodingDetectionService(),
                new AppSettings()));
            await viewModel.EndTabCollectionNotificationSuppressionAsync();

            Assert.True(mutationCount > 0);
        });
    }
}
