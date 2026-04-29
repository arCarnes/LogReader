namespace LogReader.App.Services;

using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using LogReader.App.ViewModels;
using LogReader.App.Views;
using LogReader.Core.Interfaces;
using Microsoft.Win32;

public sealed record OpenFileDialogRequest(
    string Title,
    string Filter,
    bool Multiselect = false,
    string? InitialDirectory = null);

public sealed record OpenFileDialogResult(
    bool Accepted,
    IReadOnlyList<string> FileNames);

public sealed record SaveFileDialogRequest(
    string Title,
    string Filter,
    string DefaultExt,
    bool AddExtension,
    string? InitialDirectory = null,
    string? FileName = null);

public sealed record SaveFileDialogResult(
    bool Accepted,
    string? FileName);

public enum BulkOpenPathsScope
{
    AdHoc,
    Dashboard
}

public sealed record BulkOpenPathsDialogRequest(
    BulkOpenPathsScope Scope,
    string? TargetName = null,
    string Title = "Bulk Open Files");

public sealed record BulkOpenPathsDialogResult(
    bool Accepted,
    string PathsText);

public sealed record FolderDialogRequest(
    string Description,
    string? InitialDirectory = null);

public sealed record FolderDialogResult(
    bool Accepted,
    string? SelectedPath);

public interface IFileDialogService
{
    OpenFileDialogResult ShowOpenFileDialog(OpenFileDialogRequest request);

    SaveFileDialogResult ShowSaveFileDialog(SaveFileDialogRequest request);
}

public interface IFolderDialogService
{
    FolderDialogResult ShowFolderDialog(FolderDialogRequest request);
}

public interface IMessageBoxService
{
    MessageBoxResult Show(string message, string caption, MessageBoxButton buttons, MessageBoxImage image);

    MessageBoxResult Show(Window owner, string message, string caption, MessageBoxButton buttons, MessageBoxImage image);
}

public interface ISettingsDialogService
{
    bool ShowDialog(SettingsViewModel viewModel);
}

public interface IBulkOpenPathsDialogService
{
    BulkOpenPathsDialogResult ShowDialog(BulkOpenPathsDialogRequest request);
}

internal interface IStorageSetupDialogService
{
    bool ShowDialog(StorageSetupViewModel viewModel);
}

internal interface IWindowOwnerProvider
{
    Window? GetOwner();
}

internal interface IUiDispatcher
{
    bool CheckAccess();

    Task InvokeAsync(Action action);

    Task InvokeAsync(Func<Task> action);
}

internal interface ISettingsDialogWindow
{
    object? DataContext { get; set; }

    Window? Owner { get; set; }

    bool? ShowDialog();
}

internal interface ISettingsDialogWindowFactory
{
    ISettingsDialogWindow Create();
}

internal interface IAppWindow
{
    Window? Window { get; }

    event CancelEventHandler? Closing;

    void Show();

    void Close();
}

internal interface IAppWindowFactory
{
    IAppWindow Create(MainViewModel viewModel);
}

internal sealed class FileDialogService : IFileDialogService
{
    public OpenFileDialogResult ShowOpenFileDialog(OpenFileDialogRequest request)
    {
        var dialog = new OpenFileDialog
        {
            Title = request.Title,
            Filter = request.Filter,
            Multiselect = request.Multiselect
        };

        if (!string.IsNullOrWhiteSpace(request.InitialDirectory))
            dialog.InitialDirectory = request.InitialDirectory;

        var accepted = dialog.ShowDialog() == true;
        return new OpenFileDialogResult(accepted, accepted ? dialog.FileNames : Array.Empty<string>());
    }

    public SaveFileDialogResult ShowSaveFileDialog(SaveFileDialogRequest request)
    {
        var dialog = new SaveFileDialog
        {
            Title = request.Title,
            Filter = request.Filter,
            DefaultExt = request.DefaultExt,
            AddExtension = request.AddExtension
        };

        if (!string.IsNullOrWhiteSpace(request.InitialDirectory))
            dialog.InitialDirectory = request.InitialDirectory;

        if (!string.IsNullOrWhiteSpace(request.FileName))
            dialog.FileName = request.FileName;

        var accepted = dialog.ShowDialog() == true;
        return new SaveFileDialogResult(accepted, accepted ? dialog.FileName : null);
    }
}

internal sealed class FolderDialogService : IFolderDialogService
{
    public FolderDialogResult ShowFolderDialog(FolderDialogRequest request)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = request.Description,
            UseDescriptionForTitle = true
        };

        if (!string.IsNullOrWhiteSpace(request.InitialDirectory))
            dialog.InitialDirectory = request.InitialDirectory;

        var accepted = dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK;
        return new FolderDialogResult(accepted, accepted ? dialog.SelectedPath : null);
    }
}

internal sealed class MessageBoxService : IMessageBoxService
{
    public MessageBoxResult Show(string message, string caption, MessageBoxButton buttons, MessageBoxImage image)
        => MessageBox.Show(message, caption, buttons, image);

    public MessageBoxResult Show(Window owner, string message, string caption, MessageBoxButton buttons, MessageBoxImage image)
        => MessageBox.Show(owner, message, caption, buttons, image);
}

internal sealed class CurrentMainWindowOwnerProvider : IWindowOwnerProvider
{
    public Window? GetOwner() => Application.Current?.MainWindow;
}

internal sealed class WpfUiDispatcher : IUiDispatcher
{
    public static WpfUiDispatcher Instance { get; } = new();

    private WpfUiDispatcher()
    {
    }

    public bool CheckAccess()
    {
        var dispatcher = Application.Current?.Dispatcher;
        return dispatcher == null || dispatcher.CheckAccess();
    }

    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task;
    }

    public Task InvokeAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            return action();

        return dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task.Unwrap();
    }
}

internal sealed class SettingsDialogWindowFactory : ISettingsDialogWindowFactory
{
    public ISettingsDialogWindow Create() => new WpfSettingsDialogWindow(new SettingsWindow());
}

internal sealed class WpfSettingsDialogWindow : ISettingsDialogWindow
{
    private readonly SettingsWindow _window;

    public WpfSettingsDialogWindow(SettingsWindow window)
    {
        _window = window;
    }

    public object? DataContext
    {
        get => _window.DataContext;
        set => _window.DataContext = value;
    }

    public Window? Owner
    {
        get => _window.Owner;
        set => _window.Owner = value;
    }

    public bool? ShowDialog() => _window.ShowDialog();
}

internal sealed class SettingsDialogService : ISettingsDialogService
{
    private readonly IWindowOwnerProvider _ownerProvider;
    private readonly ISettingsDialogWindowFactory _windowFactory;

    public SettingsDialogService()
        : this(new CurrentMainWindowOwnerProvider(), new SettingsDialogWindowFactory())
    {
    }

    internal SettingsDialogService(
        IWindowOwnerProvider ownerProvider,
        ISettingsDialogWindowFactory windowFactory)
    {
        _ownerProvider = ownerProvider;
        _windowFactory = windowFactory;
    }

    public bool ShowDialog(SettingsViewModel viewModel)
    {
        var settingsWindow = _windowFactory.Create();
        settingsWindow.DataContext = viewModel;

        var owner = _ownerProvider.GetOwner();
        if (owner != null)
            settingsWindow.Owner = owner;

        return settingsWindow.ShowDialog() == true;
    }
}

internal sealed class BulkOpenPathsDialogService : IBulkOpenPathsDialogService
{
    private readonly IWindowOwnerProvider _ownerProvider;

    public BulkOpenPathsDialogService()
        : this(new CurrentMainWindowOwnerProvider())
    {
    }

    internal BulkOpenPathsDialogService(IWindowOwnerProvider ownerProvider)
    {
        _ownerProvider = ownerProvider;
    }

    public BulkOpenPathsDialogResult ShowDialog(BulkOpenPathsDialogRequest request)
    {
        var window = new BulkOpenDashboardPathsWindow(request);
        var owner = _ownerProvider.GetOwner();
        if (owner != null)
            window.Owner = owner;

        var accepted = window.ShowDialog() == true;
        return new BulkOpenPathsDialogResult(
            accepted,
            accepted ? window.PathsText : string.Empty);
    }
}

internal sealed class StorageSetupDialogService : IStorageSetupDialogService
{
    private readonly IMessageBoxService _messageBoxService;

    public StorageSetupDialogService(IMessageBoxService messageBoxService)
    {
        _messageBoxService = messageBoxService;
    }

    public bool ShowDialog(StorageSetupViewModel viewModel)
    {
        var window = new StorageSetupWindow(_messageBoxService)
        {
            DataContext = viewModel
        };

        return window.ShowDialog() == true;
    }
}

internal sealed class AppWindowFactory : IAppWindowFactory
{
    public IAppWindow Create(MainViewModel viewModel)
        => new WpfAppWindow(new MainWindow { DataContext = viewModel });
}

internal sealed class WpfAppWindow : IAppWindow
{
    private readonly MainWindow _window;

    public WpfAppWindow(MainWindow window)
    {
        _window = window;
    }

    public Window? Window => _window;

    public event CancelEventHandler? Closing
    {
        add => _window.Closing += value;
        remove => _window.Closing -= value;
    }

    public void Show() => _window.Show();

    public void Close() => _window.Close();
}

internal sealed record AppStartupUiResult(
    bool Started,
    IAppWindow? MainWindow = null);

internal sealed class AppStartupUiCoordinator
{
    private readonly IAppWindowFactory _windowFactory;
    private readonly IMessageBoxService _messageBoxService;
    private readonly Func<Exception, string> _buildStartupFailureMessage;

    public AppStartupUiCoordinator(
        IAppWindowFactory windowFactory,
        IMessageBoxService messageBoxService,
        Func<Exception, string> buildStartupFailureMessage)
    {
        _windowFactory = windowFactory;
        _messageBoxService = messageBoxService;
        _buildStartupFailureMessage = buildStartupFailureMessage;
    }

    public AppStartupUiResult ShowMainWindow(
        MainViewModel mainViewModel,
        IFileTailService? tailService,
        CancelEventHandler closingHandler)
    {
        IAppWindow? mainWindow = null;

        try
        {
            mainWindow = _windowFactory.Create(mainViewModel);
            mainWindow.Closing += closingHandler;
            mainWindow.Show();
            return new AppStartupUiResult(true, mainWindow);
        }
        catch (Exception ex)
        {
            _messageBoxService.Show(
                _buildStartupFailureMessage(ex),
                "LogReader Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            App.CleanupFailedStartup(mainWindow, mainViewModel, tailService);
            return new AppStartupUiResult(false);
        }
    }
}
