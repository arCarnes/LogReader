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
    private IFileTailService? _tailService;
    private MainViewModel? _mainViewModel;

    public App()
    {
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
        MainWindow? mainWindow = null;

        try
        {
            AppPaths.ValidateStorageConfiguration();
            CleanupIndexCacheDirectory();

            _mainViewModel = await CreateInitializedMainViewModelAsync();
            mainWindow = new MainWindow { DataContext = _mainViewModel };
            mainWindow.Closing += MainWindow_Closing;
            MainWindow = mainWindow;
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                BuildStartupFailureMessage(ex),
                "LogReader Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            CleanupFailedStartup(mainWindow, _mainViewModel, _tailService);
            _mainViewModel = null;
            _tailService = null;
            Shutdown();
        }
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

    internal async Task<MainViewModel> CreateInitializedMainViewModelAsync()
    {
        var composition = await new AppBootstrapper().CreateInitializedAsync();
        _tailService = composition.TailService;
        return composition.MainViewModel;
    }

    internal static string BuildStartupFailureMessage(Exception ex)
    {
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

    internal static void CleanupIndexCacheDirectory()
    {
        var idxDir = AppPaths.IndexDirectory;
        if (Directory.Exists(idxDir))
        {
            try { Directory.Delete(idxDir, true); } catch { }
        }
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
