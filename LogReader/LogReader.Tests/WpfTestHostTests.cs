namespace LogReader.Tests;

using LogReader.App.ViewModels;
using LogReader.App.Services;
using LogReader.App.Views;
using LogReader.Core.Models;
using LogReader.Core;
using LogReader.Infrastructure.Services;
using LogReader.Testing;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;

public class WpfTestHostTests
{
    [Fact]
    public async Task AppearanceService_UpdatesOpenWindowPaletteAndDashboardSizes()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var service = new WpfLogAppearanceService();
            var window = new Window();
            window.SetResourceReference(Window.BackgroundProperty, "AppBackgroundBrush");
            WpfTestHost.ShowHidden(window);

            service.Apply(new AppSettings { DashboardFontSize = 18, IsDarkMode = true });
            await WpfTestHost.FlushAsync();

            Assert.Equal(Color.FromRgb(0x15, 0x1A, 0x21), Assert.IsType<SolidColorBrush>(window.Background).Color);
            Assert.Equal(18d, Application.Current.Resources["DashboardPrimaryFontSizeResource"]);
            Assert.Equal(17d, Application.Current.Resources["DashboardMemberFontSizeResource"]);
            Assert.Equal(16d, Application.Current.Resources["DashboardDetailFontSizeResource"]);

            service.Apply(new AppSettings { DashboardFontSize = 10, IsDarkMode = true });
            Assert.Equal(10d, Application.Current.Resources["DashboardPrimaryFontSizeResource"]);
            Assert.Equal(9d, Application.Current.Resources["DashboardMemberFontSizeResource"]);
            Assert.Equal(8d, Application.Current.Resources["DashboardDetailFontSizeResource"]);

            service.Apply(new AppSettings());
            await WpfTestHost.FlushAsync();

            Assert.Equal(Color.FromRgb(0xF7, 0xF8, 0xFA), Assert.IsType<SolidColorBrush>(window.Background).Color);
            Assert.Equal(12d, Application.Current.Resources["DashboardPrimaryFontSizeResource"]);
            window.Close();
        });
    }

    [Fact]
    public async Task SettingsWindow_UsesDarkControlSurfaces()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var settings = new SettingsViewModel(new StubSettingsRepository());
            var window = new SettingsWindow
            {
                DataContext = settings
            };
            window.SetResourceReference(Window.BackgroundProperty, "AppBackgroundBrush");
            window.SetResourceReference(Window.ForegroundProperty, "AppTextBrush");
            WpfTestHost.ShowHidden(window);
            var service = new WpfLogAppearanceService();
            service.Apply(new AppSettings { IsDarkMode = true });
            await WpfTestHost.FlushAsync();

            var comboBox = FindVisualChild<System.Windows.Controls.ComboBox>(window);
            Assert.NotNull(comboBox);
            Assert.Equal(Color.FromRgb(0x20, 0x2B, 0x36), Assert.IsType<SolidColorBrush>(comboBox.Background).Color);
            comboBox.IsDropDownOpen = true;
            await WpfTestHost.FlushAsync();
            var option = Assert.IsType<System.Windows.Controls.ComboBoxItem>(comboBox.ItemContainerGenerator.ContainerFromIndex(1));
            Assert.Equal(Color.FromRgb(0x20, 0x2B, 0x36), Assert.IsType<SolidColorBrush>(option.Background).Color);
            comboBox.SelectedIndex = 1;
            Assert.Equal("Cascadia Mono", settings.LogFontFamily);
            comboBox.IsDropDownOpen = false;

            service.Apply(new AppSettings());
            window.Close();
        });
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                return match;
            if (FindVisualChild<T>(child) is { } descendant)
                return descendant;
        }

        return null;
    }

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
    public async Task RunAsync_ActionFailure_ClosesWindowsAndAllowsNextInvocation()
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
    public async Task RunAsync_ReusesApplicationResourcesAndFlowsEachTestStorageScope()
    {
        Application? firstApplication = null;
        var root = Path.Combine(Path.GetTempPath(), $"WeezTailHostTests_{Guid.NewGuid():N}");
        foreach (var name in new[] { "first", "second" })
        {
            var scopedRoot = Path.Combine(root, name);
            using var scope = AppPaths.BeginTestScope(rootPath: scopedRoot);
            await WpfTestHost.RunAsync(async () =>
            {
                await WpfTestHost.FlushAsync();
                Assert.Equal(scopedRoot, AppPaths.RootDirectory);
                Assert.NotNull(Application.Current.FindResource("AppBackgroundBrush"));
                Assert.Empty(Application.Current.Windows.OfType<Window>());
                if (firstApplication != null)
                    Assert.Same(firstApplication, Application.Current);
                firstApplication = Application.Current;
            });
        }
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
