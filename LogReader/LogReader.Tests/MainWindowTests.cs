namespace LogReader.Tests;

using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.App.Views;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Testing;

public sealed class MainWindowTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "LogReaderMainWindowTests_" + Guid.NewGuid().ToString("N")[..8]);

    public MainWindowTests()
    {
        AppPaths.SetRootPathForTests(_testRoot);
    }

    public void Dispose()
    {
        AppPaths.SetRootPathForTests(null);

        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, true);
    }

    [Fact]
    public async Task PanelLayout_ReflectsInitialAndUpdatedViewModelState()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            using var viewModel = CreateViewModel();
            await viewModel.InitializeAsync();
            var window = CreateWindow(viewModel);

            Assert.Equal(220, window.GroupsPanelColumn.Width.Value, 3);
            Assert.Equal(260, window.SearchPanelRow.Height.Value, 3);

            viewModel.IsGroupsPanelOpen = false;
            viewModel.SearchPanelHeight = 180;
            viewModel.GroupsPanelWidth = 312;

            Assert.Equal(29, window.GroupsPanelColumn.Width.Value, 3);
            Assert.Equal(180, window.SearchPanelRow.Height.Value, 3);

            viewModel.IsGroupsPanelOpen = true;

            Assert.Equal(312, window.GroupsPanelColumn.Width.Value, 3);
        });
    }

    [Fact]
    public async Task PersistPanelSizes_RemembersResizedDimensions()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            using var viewModel = CreateViewModel();
            await viewModel.InitializeAsync();
            var window = CreateWindow(viewModel);

            window.PersistGroupsPanelWidth(336);
            window.PersistSearchPanelHeight(192);

            Assert.InRange(viewModel.GroupsPanelWidth, 335, 337);
            Assert.InRange(viewModel.SearchPanelHeight, 191, 193);
        });
    }

    [Fact]
    public async Task HandlePreviewShortcut_CtrlArrowNavigatesTabsOutsideEditableInputs()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            using var viewModel = CreateViewModel();
            await viewModel.InitializeAsync();
            await viewModel.OpenFilePathAsync(@"C:\test\a.log");
            await viewModel.OpenFilePathAsync(@"C:\test\b.log");
            var window = CreateWindow(viewModel);

            viewModel.SelectedTab = viewModel.Tabs[0];

            var handledRight = window.HandlePreviewShortcut(Key.Right, ModifierKeys.Control, new TextBlock());
            Assert.True(handledRight);
            Assert.Equal(@"C:\test\b.log", viewModel.SelectedTab?.FilePath);

            var handledLeft = window.HandlePreviewShortcut(Key.Left, ModifierKeys.Control, new TextBlock());
            Assert.True(handledLeft);
            Assert.Equal(@"C:\test\a.log", viewModel.SelectedTab?.FilePath);
        });
    }

    [Fact]
    public async Task HandlePreviewShortcut_CtrlArrowIgnoresEditableTextInputs()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            using var viewModel = CreateViewModel();
            await viewModel.InitializeAsync();
            await viewModel.OpenFilePathAsync(@"C:\test\a.log");
            await viewModel.OpenFilePathAsync(@"C:\test\b.log");
            var window = CreateWindow(viewModel);

            viewModel.SelectedTab = viewModel.Tabs[0];

            var handled = window.HandlePreviewShortcut(Key.Right, ModifierKeys.Control, new TextBox());

            Assert.False(handled);
            Assert.Equal(@"C:\test\a.log", viewModel.SelectedTab?.FilePath);
        });
    }

    [Fact]
    public async Task HandlePreviewShortcut_CtrlFQueuesSearchFocusAction()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            using var viewModel = CreateViewModel();
            await viewModel.InitializeAsync();
            var window = CreateWindow(viewModel);
            var focusCallCount = 0;

            var handled = window.HandlePreviewShortcut(
                Key.F,
                ModifierKeys.Control,
                new TextBlock(),
                () => focusCallCount++);

            Assert.True(handled);
            Assert.Equal(0, focusCallCount);

            await WpfTestHost.FlushAsync();

            Assert.Equal(1, focusCallCount);
        });
    }

    [Fact]
    public async Task GetDragOverEffects_ReturnsCopyOnlyForFileDropData()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            using var viewModel = CreateViewModel();
            await viewModel.InitializeAsync();
            var window = CreateWindow(viewModel);

            var fileDropData = new DataObject(DataFormats.FileDrop, new[] { @"C:\test\a.log" });
            var textData = new DataObject(DataFormats.Text, "hello");

            Assert.Equal(DragDropEffects.Copy, window.GetDragOverEffects(fileDropData));
            Assert.Equal(DragDropEffects.None, window.GetDragOverEffects(textData));
        });
    }

    [Fact]
    public async Task HandleFileDropAsync_OpensDroppedFiles()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var messageBoxService = new StubMessageBoxService();
            using var viewModel = CreateViewModel(messageBoxService: messageBoxService);
            await viewModel.InitializeAsync();
            var window = CreateWindow(viewModel);
            var data = new DataObject(DataFormats.FileDrop, new[] { @"C:\test\a.log", @"C:\test\b.log" });

            var handled = await window.HandleFileDropAsync(data);

            Assert.True(handled);
            Assert.Equal(new[] { @"C:\test\a.log", @"C:\test\b.log" }, viewModel.Tabs.Select(tab => tab.FilePath).ToArray());
            Assert.Null(messageBoxService.LastMessage);
        });
    }

    [Fact]
    public async Task HandleOpenSettingsAsync_DelegatesIntoSettingsFlow()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var messageBoxService = new StubMessageBoxService();
            var settingsDialogService = new StubSettingsDialogService
            {
                OnShowDialog = static _ => false
            };
            using var viewModel = CreateViewModel(
                messageBoxService: messageBoxService,
                settingsDialogService: settingsDialogService);
            await viewModel.InitializeAsync();
            var window = CreateWindow(viewModel);

            var handled = await window.HandleOpenSettingsAsync();

            Assert.True(handled);
            Assert.NotNull(settingsDialogService.LastViewModel);
            Assert.Null(messageBoxService.LastMessage);
        });
    }

    private static MainViewModel CreateViewModel(
        IMessageBoxService? messageBoxService = null,
        ISettingsDialogService? settingsDialogService = null,
        IFileDialogService? fileDialogService = null,
        ILogReaderService? logReaderService = null,
        IFileTailService? tailService = null)
    {
        return new MainViewModel(
            new StubLogFileRepository(),
            new StubLogGroupRepository(),
            new StubSettingsRepository(),
            logReaderService ?? new StubLogReaderService(),
            new StubSearchService(),
            tailService ?? new StubFileTailService(),
            new StubEncodingDetectionService(),
            enableLifecycleTimer: false,
            fileDialogService: fileDialogService ?? new StubFileDialogService(),
            messageBoxService: messageBoxService ?? new StubMessageBoxService(),
            settingsDialogService: settingsDialogService ?? new StubSettingsDialogService());
    }

    private static MainWindow CreateWindow(MainViewModel viewModel)
    {
        var window = new MainWindow
        {
            DataContext = viewModel
        };

        PrepareWindow(window);
        return window;
    }

    private static void PrepareWindow(Window window)
    {
        var size = new Size(window.Width, window.Height);
        window.Measure(size);
        window.Arrange(new Rect(new Point(0, 0), size));
        window.UpdateLayout();
    }
}
