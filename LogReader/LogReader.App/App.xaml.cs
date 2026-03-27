namespace LogReader.App;

using System.ComponentModel;
using System.IO;
using System.Windows;
using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.App.Views;

public partial class App : Application
{
    private readonly AppShutdownCoordinator _shutdownCoordinator;
    private readonly Func<AppStartupRunner> _startupRunnerFactory;
    private readonly AppStartupUiCoordinator _startupUiCoordinator;
    private readonly IStartupShutdownModeCoordinator _startupShutdownModeCoordinator;
    private readonly Action _shutdownAction;
    private IFileTailService? _tailService;
    private MainViewModel? _mainViewModel;

    public App()
        : this(null, null, null, null)
    {
    }

    internal App(
        Func<AppStartupRunner>? startupRunnerFactory,
        AppStartupUiCoordinator? startupUiCoordinator,
        Action? shutdownAction,
        IStartupShutdownModeCoordinator? startupShutdownModeCoordinator)
    {
        _startupRunnerFactory = startupRunnerFactory ?? CreateStartupRunner;
        _startupUiCoordinator = startupUiCoordinator ?? CreateStartupUiCoordinator();
        _startupShutdownModeCoordinator = startupShutdownModeCoordinator ?? new StartupShutdownModeCoordinator();
        _shutdownAction = shutdownAction ?? Shutdown;
        _shutdownCoordinator = new AppShutdownCoordinator(
            () => _mainViewModel,
            () => _tailService,
            () =>
            {
                _mainViewModel = null;
                _tailService = null;
            });
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _ = RunStartupAsync();
    }

    private async Task RunStartupAsync()
    {
        await RunStartupAsync(
            _startupRunnerFactory,
            _startupUiCoordinator,
            (mainViewModel, tailService) =>
            {
                _mainViewModel = mainViewModel;
                _tailService = tailService;
            },
            mainWindow => MainWindow = mainWindow,
            _shutdownAction,
            MainWindow_Closing,
            _startupShutdownModeCoordinator);
    }

    internal static async Task RunStartupAsync(
        Func<AppStartupRunner> startupRunnerFactory,
        AppStartupUiCoordinator startupUiCoordinator,
        Action<MainViewModel?, IFileTailService?> setComposition,
        Action<Window?> setMainWindow,
        Action shutdownAction,
        CancelEventHandler closingHandler,
        IStartupShutdownModeCoordinator startupShutdownModeCoordinator)
    {
        startupShutdownModeCoordinator.EnterStartup();

        var result = await startupRunnerFactory().RunAsync();
        if (result.Status != AppStartupStatus.Started || result.MainViewModel == null)
        {
            setComposition(null, null);
            setMainWindow(null);
            shutdownAction();
            return;
        }

        setComposition(result.MainViewModel, result.TailService);
        var uiResult = startupUiCoordinator.ShowMainWindow(result.MainViewModel, result.TailService, closingHandler);
        if (!uiResult.Started)
        {
            setComposition(null, null);
            setMainWindow(null);
            shutdownAction();
            return;
        }

        setMainWindow(uiResult.MainWindow?.Window);
        startupShutdownModeCoordinator.RestoreNormalMode();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _shutdownCoordinator.Prepare();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shutdownCoordinator.Complete();
        base.OnExit(e);
    }

    private static AppStartupRunner CreateStartupRunner()
    {
        return new AppStartupRunner(
            new StartupStorageCoordinator(),
            new AppBootstrapper(),
            new MessageBoxService(),
            CleanupIndexCacheDirectory,
            BuildStartupFailureMessage);
    }

    private static AppStartupUiCoordinator CreateStartupUiCoordinator()
    {
        var messageBoxService = new MessageBoxService();
        return new AppStartupUiCoordinator(
            new AppWindowFactory(),
            messageBoxService,
            BuildStartupFailureMessage);
    }

    internal static string BuildStartupFailureMessage(Exception ex)
    {
        if (FindException<StorageSetupRequiredException>(ex) is { } storageSetupRequiredException)
        {
            return
                $"LogReader could not finish starting.{Environment.NewLine}{Environment.NewLine}" +
                $"This MSI install still needs a storage folder for the current Windows user:{Environment.NewLine}{storageSetupRequiredException.SelectionFilePath}{Environment.NewLine}{Environment.NewLine}" +
                "Restart LogReader to complete the storage setup.";
        }

        if (FindException<InstallConfigurationException>(ex) is { } installConfigException)
        {
            var configPath = installConfigException.ConfigurationPath ?? AppPaths.InstallConfigFileName;
            return
                $"LogReader could not finish starting.{Environment.NewLine}{Environment.NewLine}" +
                $"The install configuration is missing or invalid:{Environment.NewLine}{configPath}{Environment.NewLine}{Environment.NewLine}" +
                $"{installConfigException.Message}{Environment.NewLine}{Environment.NewLine}" +
                "Rebuild the portable or MSI package, or run a Debug build from source.";
        }

        if (FindException<ProtectedStorageLocationException>(ex) is { } protectedStorageException)
        {
            return
                $"LogReader could not finish starting.{Environment.NewLine}{Environment.NewLine}" +
                $"The configured storage location is protected:{Environment.NewLine}{protectedStorageException.StoragePath}{Environment.NewLine}{Environment.NewLine}" +
                "Choose a writable folder outside Program Files and Windows directories.";
        }

        if (FindException<StorageValidationException>(ex) is { } storageValidationException)
        {
            return
                $"LogReader could not finish starting.{Environment.NewLine}{Environment.NewLine}" +
                $"The app couldn't use its configured storage location:{Environment.NewLine}{storageValidationException.StoragePath}{Environment.NewLine}{Environment.NewLine}" +
                $"{storageValidationException.Message}{Environment.NewLine}{Environment.NewLine}" +
                "Choose a folder that is available and writable, then try again.";
        }

        var storageException = FindStartupStorageException(ex);
        if (storageException == null)
            return $"LogReader could not finish starting.{Environment.NewLine}{Environment.NewLine}{ex.Message}";

        var dataPath = AppPaths.TryGetDataDirectoryForMessage();
        var locationMessage = dataPath == null
            ? "The app couldn't access its saved data."
            : $"The app couldn't access its saved data in:{Environment.NewLine}{dataPath}";

        return
            $"LogReader could not finish starting.{Environment.NewLine}{Environment.NewLine}" +
            $"{locationMessage}{Environment.NewLine}{Environment.NewLine}" +
            $"{storageException.Message}{Environment.NewLine}{Environment.NewLine}" +
            "Check that the folder is available, the files are not locked, and that you have permission to read and write there.";
    }

    private static Exception? FindStartupStorageException(Exception ex)
    {
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            if (current is IOException or UnauthorizedAccessException)
                return current;
        }

        return null;
    }

    private static T? FindException<T>(Exception ex) where T : Exception
    {
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            if (current is T typedException)
                return typedException;
        }

        return null;
    }

    internal static void CleanupFailedStartup(Window? mainWindow, MainViewModel? mainVm, IFileTailService? tailService)
    {
        mainWindow?.Close();
        mainVm?.BeginShutdown();
        mainVm?.Dispose();
        tailService?.Dispose();
    }

    internal static void CleanupFailedStartup(IAppWindow? mainWindow, MainViewModel? mainVm, IFileTailService? tailService)
    {
        mainWindow?.Close();
        mainVm?.BeginShutdown();
        mainVm?.Dispose();
        tailService?.Dispose();
    }

    internal static void CleanupIndexCacheDirectory()
    {
        var idxDir = AppPaths.IndexDirectory;
        if (Directory.Exists(idxDir))
        {
            try { Directory.Delete(idxDir, true); } catch { }
        }
    }
}

internal interface IStartupShutdownModeCoordinator
{
    void EnterStartup();

    void RestoreNormalMode();
}

internal sealed class StartupShutdownModeCoordinator : IStartupShutdownModeCoordinator
{
    private readonly Func<Application?> _applicationProvider;

    public StartupShutdownModeCoordinator(Func<Application?>? applicationProvider = null)
    {
        _applicationProvider = applicationProvider ?? (() => Application.Current);
    }

    public void EnterStartup()
    {
        var application = _applicationProvider();
        if (application != null)
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    public void RestoreNormalMode()
    {
        var application = _applicationProvider();
        if (application != null)
            application.ShutdownMode = ShutdownMode.OnMainWindowClose;
    }
}

internal sealed class AppShutdownCoordinator
{
    private readonly Func<MainViewModel?> _viewModelProvider;
    private readonly Func<IFileTailService?> _tailServiceProvider;
    private readonly Action _clearReferences;
    private int _isPrepared;
    private int _isCompleted;

    public AppShutdownCoordinator(
        Func<MainViewModel?> viewModelProvider,
        Func<IFileTailService?> tailServiceProvider,
        Action clearReferences)
    {
        _viewModelProvider = viewModelProvider;
        _tailServiceProvider = tailServiceProvider;
        _clearReferences = clearReferences;
    }

    public void Prepare()
    {
        if (Interlocked.Exchange(ref _isPrepared, 1) != 0)
            return;

        var viewModel = _viewModelProvider();
        if (viewModel != null)
        {
            viewModel.BeginShutdown();
            return;
        }

        _tailServiceProvider()?.StopAll();
    }

    public void Complete()
    {
        if (Interlocked.Exchange(ref _isCompleted, 1) != 0)
            return;

        Prepare();

        var viewModel = _viewModelProvider();
        try
        {
            if (viewModel != null)
                viewModel.Dispose();
        }
        finally
        {
            try
            {
                _tailServiceProvider()?.Dispose();
            }
            finally
            {
                _clearReferences();
            }
        }
    }
}
