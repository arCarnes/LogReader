namespace LogReader.Tests;

using System.Diagnostics;
using System.Windows;
using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.Core.Models;

public sealed class McpHelpTests
{
    [Fact]
    public void PresentationBuilder_ResolvesSiblingExecutableAndAvailableState()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "WeezTail Help Tests", "app");
        var expectedPath = Path.GetFullPath(Path.Combine(baseDirectory, "WeezTail.Mcp.exe"));

        var presentation = McpHelpPresentationBuilder.Create(
            new McpHelpDialogRequest(2),
            baseDirectory,
            path => string.Equals(path, expectedPath, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(expectedPath, presentation.ServerExecutablePath);
        Assert.True(presentation.IsServerAvailable);
        Assert.Equal("Available beside WeezTail", presentation.ServerStatusText);
        Assert.Equal("2 saved dashboards", presentation.DashboardStatusText);
        Assert.Equal(Uri.UriSchemeHttps, presentation.GuideUri.Scheme);
    }

    [Fact]
    public void PresentationBuilder_ReportsMissingDevelopmentSidecarWithoutFailure()
    {
        var presentation = McpHelpPresentationBuilder.Create(
            new McpHelpDialogRequest(0),
            AppContext.BaseDirectory,
            static _ => false);

        Assert.False(presentation.IsServerAvailable);
        Assert.Equal("Not found beside this build", presentation.ServerStatusText);
        Assert.Equal("0 saved dashboards", presentation.DashboardStatusText);
    }

    [Fact]
    public void CountSavedDashboards_CountsNestedDashboardsOnly()
    {
        var root = CreateGroup("root", LogGroupKind.Branch);
        var nestedFolder = CreateGroup("nested", LogGroupKind.Branch);
        var firstDashboard = CreateGroup("one", LogGroupKind.Dashboard);
        var secondDashboard = CreateGroup("two", LogGroupKind.Dashboard);
        root.AddChild(firstDashboard);
        root.AddChild(nestedFolder);
        nestedFolder.AddChild(secondDashboard);

        var count = McpHelpPresentationBuilder.CountSavedDashboards([root]);

        Assert.Equal(2, count);
    }

    [Fact]
    public void MainViewModel_OpenMcpHelpPassesCurrentNestedDashboardCount()
    {
        var dialogService = new StubMcpHelpDialogService();
        using var viewModel = TestMainViewModelFactory.Create(
            new StubLogFileRepository(),
            new StubLogGroupRepository(),
            new StubSettingsRepository(),
            new StubLogReaderService(),
            new StubSearchService(),
            new StubFileTailService(),
            new StubEncodingDetectionService(),
            mcpHelpDialogService: dialogService);
        var root = CreateGroup("root", LogGroupKind.Branch);
        root.AddChild(CreateGroup("one", LogGroupKind.Dashboard));
        var nestedFolder = CreateGroup("nested", LogGroupKind.Branch);
        nestedFolder.AddChild(CreateGroup("two", LogGroupKind.Dashboard));
        root.AddChild(nestedFolder);
        viewModel.Groups.Add(root);

        viewModel.OpenMcpHelp();

        Assert.Equal(1, dialogService.ShowDialogCallCount);
        Assert.Equal(2, dialogService.LastRequest?.SavedDashboardCount);
    }

    [Fact]
    public void ShellExternalLinkLauncher_CreatesDefaultBrowserStartInfo()
    {
        var uri = new Uri(McpHelpPresentationBuilder.GuideUrl);

        ProcessStartInfo startInfo = ShellExternalLinkLauncher.CreateStartInfo(uri);

        Assert.Equal(uri.AbsoluteUri, startInfo.FileName);
        Assert.True(startInfo.UseShellExecute);
    }

    [Fact]
    public void McpHelpActions_ClipboardFailureShowsOwnedError()
    {
        WpfTestHost.Run(() =>
        {
            var messageBox = new StubMessageBoxService();
            var actions = new McpHelpActions(
                new ThrowingClipboardService(),
                new RecordingExternalLinkLauncher(),
                messageBox);
            var owner = new Window();

            actions.CopyServerPath(owner, @"C:\Program Files\WeezTail\WeezTail.Mcp.exe");

            Assert.Equal("MCP Server Help", messageBox.LastCaption);
            Assert.Contains("could not copy", messageBox.LastMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("clipboard unavailable", messageBox.LastMessage, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void McpHelpActions_GuideFailureShowsOwnedError()
    {
        WpfTestHost.Run(() =>
        {
            var messageBox = new StubMessageBoxService();
            var actions = new McpHelpActions(
                new RecordingClipboardService(),
                new ThrowingExternalLinkLauncher(),
                messageBox);
            var owner = new Window();

            actions.OpenGuide(owner, new Uri(McpHelpPresentationBuilder.GuideUrl));

            Assert.Equal("MCP Server Help", messageBox.LastCaption);
            Assert.Contains("could not open", messageBox.LastMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("browser unavailable", messageBox.LastMessage, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void McpHelpDialogService_RebuildsPresentationAndAssignsOwner()
    {
        WpfTestHost.Run(() =>
        {
            var owner = new Window();
            var ownerProvider = new StubWindowOwnerProvider { Owner = owner };
            var windowFactory = new StubMcpHelpDialogWindowFactory();
            var service = new McpHelpDialogService(
                ownerProvider,
                windowFactory,
                static () => @"C:\Apps\WeezTail",
                static _ => true);

            service.ShowDialog(new McpHelpDialogRequest(3));

            Assert.Equal(1, windowFactory.CreateCallCount);
            Assert.Equal(1, windowFactory.Window.ShowDialogCallCount);
            Assert.Same(owner, windowFactory.Window.Owner);
            Assert.Equal("3 saved dashboards", windowFactory.LastPresentation?.DashboardStatusText);
        });
    }

    [Fact]
    public void McpHelpWindowXaml_ContainsThreeSectionsAndActions()
    {
        var xaml = File.ReadAllText(GetRepoFilePath(@"LogReader.App\Views\McpHelpWindow.xaml"));

        Assert.Contains("Header=\"Getting started\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"How agent log access works\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"Technical details\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Copy server path\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Open full guide\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Close\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowXaml_ExposesMcpServerToolbarEntry()
    {
        var xaml = File.ReadAllText(GetRepoFilePath(@"LogReader.App\Views\MainWindow.xaml"));

        Assert.Contains("Content=\"MCP Server\" Click=\"OpenMcpHelp\"", xaml, StringComparison.Ordinal);
    }

    private static LogGroupViewModel CreateGroup(string name, LogGroupKind kind)
        => new(
            new LogGroup
            {
                Id = name,
                Name = name,
                Kind = kind
            },
            static _ => Task.CompletedTask);

    private static string GetRepoFilePath(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "LogReader.sln")))
            current = current.Parent;

        Assert.NotNull(current);
        return Path.Combine(current!.FullName, relativePath);
    }

    private sealed class ThrowingClipboardService : IClipboardService
    {
        public void SetText(string text) => throw new InvalidOperationException("clipboard unavailable");
    }

    private sealed class RecordingClipboardService : IClipboardService
    {
        public void SetText(string text)
        {
        }
    }

    private sealed class RecordingExternalLinkLauncher : IExternalLinkLauncher
    {
        public void Open(Uri uri)
        {
        }
    }

    private sealed class ThrowingExternalLinkLauncher : IExternalLinkLauncher
    {
        public void Open(Uri uri) => throw new InvalidOperationException("browser unavailable");
    }

    private sealed class StubMcpHelpDialogWindow : IMcpHelpDialogWindow
    {
        public Window? Owner { get; set; }

        public int ShowDialogCallCount { get; private set; }

        public bool? ShowDialog()
        {
            ShowDialogCallCount++;
            return true;
        }
    }

    private sealed class StubMcpHelpDialogWindowFactory : IMcpHelpDialogWindowFactory
    {
        public StubMcpHelpDialogWindow Window { get; } = new();

        public int CreateCallCount { get; private set; }

        public McpHelpPresentation? LastPresentation { get; private set; }

        public IMcpHelpDialogWindow Create(McpHelpPresentation presentation)
        {
            CreateCallCount++;
            LastPresentation = presentation;
            return Window;
        }
    }
}
