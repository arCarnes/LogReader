namespace LogReader.App;

using System.ComponentModel;
using System.IO;
using System.Windows;
using LogReader.App.ViewModels;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Infrastructure.Repositories;
using LogReader.Infrastructure.Services;
using LogReader.App.Views;

public partial class App : Application
{
    internal static readonly TimeSpan ShutdownSaveTimeout = TimeSpan.FromSeconds(2);
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
        _shutdownCoordinator.Complete(ShutdownSaveTimeout);
        base.OnExit(e);
    }

    internal async Task<MainViewModel> CreateInitializedMainViewModelAsync()
    {
        ILogFileRepository fileRepo = new JsonLogFileRepository();
        ILogGroupRepository groupRepo = new JsonLogGroupRepository(fileRepo);
        ISessionRepository sessionRepo = new JsonSessionRepository();
        ISettingsRepository settingsRepo = new JsonSettingsRepository();
        ILogReaderService logReader = new ChunkedLogReaderService();
        ISearchService searchService = new SearchService();
        _tailService = new FileTailService();

        var mainVm = new MainViewModel(fileRepo, groupRepo, sessionRepo, settingsRepo, logReader, searchService, _tailService);
        await mainVm.InitializeAsync();
        return mainVm;
    }

    internal static async Task<bool> TrySaveSessionOnExitAsync(MainViewModel vm, TimeSpan timeout)
    {
        try
        {
            await vm.SaveSessionAsync().WaitAsync(timeout);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    internal static string BuildStartupFailureMessage(Exception ex)
        => $"LogReader could not finish starting.{Environment.NewLine}{Environment.NewLine}{ex.Message}";

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

    public void Complete(TimeSpan saveTimeout)
    {
        if (Interlocked.Exchange(ref _isCompleted, 1) != 0)
            return;

        Prepare();

        var viewModel = _viewModelProvider();
        try
        {
            if (viewModel != null)
            {
                App.TrySaveSessionOnExitAsync(viewModel, saveTimeout).GetAwaiter().GetResult();
                viewModel.Dispose();
            }
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
