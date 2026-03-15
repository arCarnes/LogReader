namespace LogReader.App;

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
    private IFileTailService? _tailService;
    private MainViewModel? _mainViewModel;

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

    protected override void OnExit(ExitEventArgs e)
    {
        if (_mainViewModel != null)
        {
            TrySaveSessionOnExitAsync(_mainViewModel, ShutdownSaveTimeout).GetAwaiter().GetResult();
            _mainViewModel.Dispose();
            _mainViewModel = null;
        }

        _tailService?.Dispose();
        _tailService = null;
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
