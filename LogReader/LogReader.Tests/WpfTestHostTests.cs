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
using System.Windows.Controls;

public class WpfTestHostTests
{
    [Fact]
    public async Task MainCommandBar_WrapsCommandsAndFollowsPalette()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var service = new WpfLogAppearanceService();
            service.Apply(new AppSettings());
            var window = new MainWindow { Width = 380, Height = 360 };
            WpfTestHost.ShowHidden(window);
            await WpfTestHost.FlushAsync();

            var commandBar = Assert.IsType<Border>(window.FindName("MainCommandBar"));
            var commandPanel = Assert.IsType<WrapPanel>(window.FindName("MainCommandPanel"));
            var buttons = commandPanel.Children.OfType<Button>().ToArray();
            Assert.Equal(7, buttons.Length);
            var settings = Assert.Single(buttons, button => Equals(button.Content, "Settings"));
            Assert.True(settings.IsVisible);
            Assert.True(settings.TranslatePoint(new Point(), commandPanel).Y > buttons[0].TranslatePoint(new Point(), commandPanel).Y);
            Assert.True(WindowTitleBarTheme.GetIsEnabled(window));

            service.Apply(new AppSettings { IsDarkMode = true });
            await WpfTestHost.FlushAsync();
            Assert.Equal(AppTheme.Dark, WindowTitleBarTheme.GetTheme(window));
            Assert.Equal(Color.FromRgb(0x1C, 0x25, 0x30), Assert.IsType<SolidColorBrush>(commandBar.Background).Color);

            window.Width = 1400;
            await WpfTestHost.FlushAsync();
            Assert.Equal(buttons[0].TranslatePoint(new Point(), commandPanel).Y, settings.TranslatePoint(new Point(), commandPanel).Y);

            service.Apply(new AppSettings());
            await WpfTestHost.FlushAsync();
            Assert.Equal(Color.FromRgb(0xF4, 0xF6, 0xF8), Assert.IsType<SolidColorBrush>(commandBar.Background).Color);
            Assert.Equal(AppTheme.Default, WindowTitleBarTheme.GetTheme(window));
            window.Close();
        });
    }

    [Fact]
    public async Task AppearanceService_UpdatesTitleBarForExistingAndNewWindows()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var service = new WpfLogAppearanceService();
            service.Apply(new AppSettings());
            var window = CreateThemedWindow();
            WpfTestHost.ShowHidden(window);

            service.Apply(new AppSettings { IsDarkMode = true });
            await WpfTestHost.FlushAsync();
            Assert.Equal(AppTheme.Dark, WindowTitleBarTheme.GetTheme(window));
            Assert.True(WindowTitleBarTheme.GetIsEnabled(window));

            var laterWindow = CreateThemedWindow();
            WpfTestHost.ShowHidden(laterWindow);
            Assert.Equal(AppTheme.Dark, WindowTitleBarTheme.GetTheme(laterWindow));
            Assert.True(WindowTitleBarTheme.GetIsEnabled(laterWindow));

            service.Apply(new AppSettings { Theme = AppTheme.EasyReading });
            await WpfTestHost.FlushAsync();
            Assert.Equal(AppTheme.EasyReading, WindowTitleBarTheme.GetTheme(window));
            Assert.Equal(AppTheme.EasyReading, WindowTitleBarTheme.GetTheme(laterWindow));
            var easyReadingWindow = CreateThemedWindow();
            WpfTestHost.ShowHidden(easyReadingWindow);
            Assert.Equal(AppTheme.EasyReading, WindowTitleBarTheme.GetTheme(easyReadingWindow));

            service.Apply(new AppSettings());
            await WpfTestHost.FlushAsync();
            Assert.Equal(AppTheme.Default, WindowTitleBarTheme.GetTheme(window));
            Assert.Equal(AppTheme.Default, WindowTitleBarTheme.GetTheme(laterWindow));
            Assert.Equal(AppTheme.Default, WindowTitleBarTheme.GetTheme(easyReadingWindow));
            easyReadingWindow.Close();
            laterWindow.Close();
            window.Close();
        });
    }

    private static Window CreateThemedWindow()
    {
        var window = new Window { Style = new Style(typeof(Window)), Width = 320, Height = 180 };
        window.SetResourceReference(WindowTitleBarTheme.ThemeProperty, "AppThemeResource");
        WindowTitleBarTheme.SetIsEnabled(window, true);
        return window;
    }

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
    public async Task ThemeModes_UpdateMainPanesAndPreserveAccents()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var service = new WpfLogAppearanceService();
            service.Apply(new AppSettings());
            var window = new MainWindow { Width = 1100, Height = 700 };
            WpfTestHost.ShowHidden(window);
            await WpfTestHost.FlushAsync();

            var dashboard = Assert.IsType<Border>(Assert.IsType<DashboardTreeView>(FindVisualChild<DashboardTreeView>(window)).Content);
            var search = Assert.IsType<SearchWorkspaceView>(FindVisualChild<SearchWorkspaceView>(window));
            var searchSurface = Assert.IsType<Border>(search.Content);
            var results = Assert.IsType<ListBox>(search.FindName("SearchResultsList"));

            Assert.Equal(AppTheme.Default, WindowTitleBarTheme.GetTheme(window));
            Assert.Equal(Color.FromRgb(0xF7, 0xF8, 0xFA), BrushColor(window.Background));
            Assert.Equal(Color.FromRgb(0xFC, 0xFD, 0xFE), BrushColor(Application.Current.Resources["AppViewportContentBrush"]));
            Assert.Equal(Color.FromRgb(0xF7, 0xF8, 0xFA), BrushColor(dashboard.Background));
            Assert.Equal(Color.FromRgb(0xF7, 0xF8, 0xFA), BrushColor(searchSurface.Background));
            Assert.Equal(Color.FromRgb(0xF7, 0xF8, 0xFA), BrushColor(results.Background));
            Assert.Equal(Colors.White, BrushColor(Application.Current.Resources["AppControlSurfaceBrush"]));
            Assert.Equal(Color.FromRgb(0xEA, 0xF4, 0xFE), BrushColor(Application.Current.Resources["AppSelectedRowBrush"]));
            Assert.Equal(Color.FromRgb(0xB0, 0xD4, 0xFF), BrushColor(Application.Current.Resources["AppViewportSelectionBrush"]));
            Assert.Equal(Color.FromRgb(0xAE, 0xBE, 0xCB), BrushColor(Application.Current.Resources["AppScrollBarThumbBrush"]));

            service.Apply(new AppSettings { Theme = AppTheme.EasyReading });
            await WpfTestHost.FlushAsync();
            Assert.Equal(AppTheme.EasyReading, WindowTitleBarTheme.GetTheme(window));
            Assert.Equal(Color.FromRgb(0xE0, 0xE5, 0xEA), BrushColor(window.Background));
            Assert.Equal(Color.FromRgb(0xE0, 0xE5, 0xEA), BrushColor(Application.Current.Resources["AppViewportContentBrush"]));
            Assert.Equal(Color.FromRgb(0xD9, 0xE0, 0xE6), BrushColor(dashboard.Background));
            Assert.Equal(Color.FromRgb(0xE0, 0xE5, 0xEA), BrushColor(results.Background));
            Assert.Equal(Color.FromRgb(0xF1, 0xF3, 0xF6), BrushColor(Application.Current.Resources["AppControlSurfaceBrush"]));
            Assert.Equal(Color.FromRgb(0xEA, 0xF4, 0xFE), BrushColor(Application.Current.Resources["AppSelectedRowBrush"]));
            Assert.Equal(Color.FromRgb(0xB0, 0xD4, 0xFF), BrushColor(Application.Current.Resources["AppViewportSelectionBrush"]));

            service.Apply(new AppSettings { IsDarkMode = true });
            await WpfTestHost.FlushAsync();
            Assert.Equal(AppTheme.Dark, WindowTitleBarTheme.GetTheme(window));
            Assert.Equal(Color.FromRgb(0x15, 0x1A, 0x21), BrushColor(window.Background));
            Assert.Equal(Color.FromRgb(0x15, 0x1A, 0x21), BrushColor(dashboard.Background));
            Assert.Equal(Color.FromRgb(0x15, 0x1A, 0x21), BrushColor(results.Background));
            Assert.Equal(Color.FromRgb(0x11, 0x18, 0x20), BrushColor(Application.Current.Resources["AppViewportContentBrush"]));
            Assert.Equal(Color.FromRgb(0x53, 0x67, 0x79), BrushColor(Application.Current.Resources["AppScrollBarThumbBrush"]));

            service.Apply(new AppSettings());
            await WpfTestHost.FlushAsync();
            Assert.Equal(AppTheme.Default, WindowTitleBarTheme.GetTheme(window));
            Assert.Equal(Color.FromRgb(0xF7, 0xF8, 0xFA), BrushColor(dashboard.Background));
            Assert.Equal(Color.FromRgb(0xF7, 0xF8, 0xFA), BrushColor(results.Background));
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
            WpfTestHost.ShowHidden(window);
            var service = new WpfLogAppearanceService();
            service.Apply(new AppSettings { IsDarkMode = true });
            await WpfTestHost.FlushAsync();

            Assert.True(WindowTitleBarTheme.GetIsEnabled(window));
            Assert.Equal(AppTheme.Dark, WindowTitleBarTheme.GetTheme(window));
            Assert.Equal(Color.FromRgb(0x15, 0x1A, 0x21), BrushColor(window.Background));
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

            var themeSelector = Assert.IsType<ComboBox>(window.FindName("ThemeSelector"));
            Assert.Equal(3, themeSelector.Items.Count);
            Assert.Equal(AppTheme.Default, Assert.IsType<ThemeOption>(themeSelector.Items[0]).Value);
            Assert.Equal("Easy Reading", Assert.IsType<ThemeOption>(themeSelector.Items[1]).Label);
            themeSelector.SelectedValue = AppTheme.EasyReading;
            Assert.Equal(AppTheme.EasyReading, settings.Theme);
            Assert.Equal("Easy Reading", themeSelector.SelectionBoxItem.ToString());

            service.Apply(new AppSettings { Theme = AppTheme.EasyReading });
            await WpfTestHost.FlushAsync();
            Assert.Equal(AppTheme.EasyReading, WindowTitleBarTheme.GetTheme(window));
            Assert.Equal(Color.FromRgb(0xE0, 0xE5, 0xEA), BrushColor(window.Background));
            service.Apply(new AppSettings());
            await WpfTestHost.FlushAsync();
            Assert.Equal(Color.FromRgb(0xF7, 0xF8, 0xFA), BrushColor(window.Background));
            window.Close();
        });
    }

    private static Color BrushColor(object? brush)
        => Assert.IsType<SolidColorBrush>(brush).Color;

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
