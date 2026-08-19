namespace LogReader.App.Services;

using System.Diagnostics;
using System.IO;
using System.Windows;
using LogReader.App.ViewModels;
using LogReader.App.Views;
using LogReader.Core.Models;

public sealed record McpHelpDialogRequest(int SavedDashboardCount);

public interface IMcpHelpDialogService
{
    void ShowDialog(McpHelpDialogRequest request);
}

internal sealed record McpHelpPresentation(
    string ServerExecutablePath,
    bool IsServerAvailable,
    string ServerStatusText,
    string StorageStatusText,
    string DashboardStatusText,
    Uri GuideUri);

internal static class McpHelpPresentationBuilder
{
    internal const string ServerExecutableName = "WeezTail.Mcp.exe";
    internal const string GuideUrl = "https://github.com/arCarnes/LogReader/blob/main/LogReader/docs/McpGettingStarted.md";

    public static McpHelpPresentation Create(
        McpHelpDialogRequest request,
        string baseDirectory,
        Func<string, bool>? fileExists = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var executablePath = Path.GetFullPath(Path.Combine(baseDirectory, ServerExecutableName));
        var isServerAvailable = (fileExists ?? File.Exists)(executablePath);
        var dashboardLabel = request.SavedDashboardCount == 1 ? "dashboard" : "dashboards";

        return new McpHelpPresentation(
            executablePath,
            isServerAvailable,
            isServerAvailable ? "Available beside WeezTail" : "Not found beside this build",
            "Ready (WeezTail startup completed)",
            $"{request.SavedDashboardCount} saved {dashboardLabel}",
            new Uri(GuideUrl, UriKind.Absolute));
    }

    public static int CountSavedDashboards(IEnumerable<LogGroupViewModel> rootGroups)
    {
        ArgumentNullException.ThrowIfNull(rootGroups);

        var count = 0;
        foreach (var group in rootGroups)
        {
            if (group.Kind == LogGroupKind.Dashboard)
                count++;

            count += CountSavedDashboards(group.Children);
        }

        return count;
    }
}

internal interface IMcpHelpDialogWindow
{
    Window? Owner { get; set; }

    bool? ShowDialog();
}

internal interface IMcpHelpDialogWindowFactory
{
    IMcpHelpDialogWindow Create(McpHelpPresentation presentation);
}

internal sealed class McpHelpDialogService : IMcpHelpDialogService
{
    private readonly IWindowOwnerProvider _ownerProvider;
    private readonly IMcpHelpDialogWindowFactory _windowFactory;
    private readonly Func<string> _baseDirectoryProvider;
    private readonly Func<string, bool> _fileExists;

    public McpHelpDialogService()
        : this(
            new CurrentMainWindowOwnerProvider(),
            new McpHelpDialogWindowFactory(),
            static () => AppContext.BaseDirectory,
            File.Exists)
    {
    }

    internal McpHelpDialogService(
        IWindowOwnerProvider ownerProvider,
        IMcpHelpDialogWindowFactory windowFactory,
        Func<string> baseDirectoryProvider,
        Func<string, bool> fileExists)
    {
        _ownerProvider = ownerProvider;
        _windowFactory = windowFactory;
        _baseDirectoryProvider = baseDirectoryProvider;
        _fileExists = fileExists;
    }

    public void ShowDialog(McpHelpDialogRequest request)
    {
        var presentation = McpHelpPresentationBuilder.Create(
            request,
            _baseDirectoryProvider(),
            _fileExists);
        var window = _windowFactory.Create(presentation);
        var owner = _ownerProvider.GetOwner();
        if (owner != null)
            window.Owner = owner;

        window.ShowDialog();
    }
}

internal sealed class McpHelpDialogWindowFactory : IMcpHelpDialogWindowFactory
{
    public IMcpHelpDialogWindow Create(McpHelpPresentation presentation)
        => new McpHelpWindow(
            presentation,
            new McpHelpActions(
                new WpfClipboardService(),
                new ShellExternalLinkLauncher(),
                new MessageBoxService()));
}

internal interface IMcpHelpActions
{
    void CopyServerPath(Window owner, string serverExecutablePath);

    void OpenGuide(Window owner, Uri guideUri);
}

internal interface IClipboardService
{
    void SetText(string text);
}

internal interface IExternalLinkLauncher
{
    void Open(Uri uri);
}

internal sealed class WpfClipboardService : IClipboardService
{
    public void SetText(string text) => Clipboard.SetText(text);
}

internal sealed class ShellExternalLinkLauncher : IExternalLinkLauncher
{
    public void Open(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only absolute HTTPS documentation links can be opened.");

        Process.Start(CreateStartInfo(uri));
    }

    internal static ProcessStartInfo CreateStartInfo(Uri uri)
        => new()
        {
            FileName = uri.AbsoluteUri,
            UseShellExecute = true
        };
}

internal sealed class McpHelpActions : IMcpHelpActions
{
    private readonly IClipboardService _clipboardService;
    private readonly IExternalLinkLauncher _externalLinkLauncher;
    private readonly IMessageBoxService _messageBoxService;

    public McpHelpActions(
        IClipboardService clipboardService,
        IExternalLinkLauncher externalLinkLauncher,
        IMessageBoxService messageBoxService)
    {
        _clipboardService = clipboardService;
        _externalLinkLauncher = externalLinkLauncher;
        _messageBoxService = messageBoxService;
    }

    public void CopyServerPath(Window owner, string serverExecutablePath)
    {
        try
        {
            _clipboardService.SetText(serverExecutablePath);
        }
        catch (Exception ex)
        {
            ShowFailure(owner, "WeezTail could not copy the MCP server path.", ex);
        }
    }

    public void OpenGuide(Window owner, Uri guideUri)
    {
        try
        {
            _externalLinkLauncher.Open(guideUri);
        }
        catch (Exception ex)
        {
            ShowFailure(owner, "WeezTail could not open the MCP guide in your default browser.", ex);
        }
    }

    private void ShowFailure(Window owner, string message, Exception exception)
    {
        var detail = string.IsNullOrWhiteSpace(exception.Message)
            ? string.Empty
            : $"{Environment.NewLine}{Environment.NewLine}{exception.Message}";
        _messageBoxService.Show(
            owner,
            message + detail,
            "MCP Server Help",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
