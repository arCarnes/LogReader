namespace LogReader.App;

using System.IO;
using System.Windows;
using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Infrastructure.Repositories;
using LogReader.Infrastructure.Services;
using LogReader.App.Views;

public partial class App : Application
{
    private IFileTailService? _tailService;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Clean up stale index temp files from previous crashes
        var idxDir = Path.Combine(Path.GetTempPath(), "LogReader", "idx");
        if (Directory.Exists(idxDir))
        {
            try { Directory.Delete(idxDir, true); } catch { }
        }

        // Create services
        ILogFileRepository fileRepo = new JsonLogFileRepository();
        ILogGroupRepository groupRepo = new JsonLogGroupRepository(fileRepo);
        ISessionRepository sessionRepo = new JsonSessionRepository();
        ISettingsRepository settingsRepo = new JsonSettingsRepository();
        ILogReaderService logReader = new ChunkedLogReaderService();
        ISearchService searchService = new SearchService();
        _tailService = new FileTailService();

        var mainVm = new MainViewModel(fileRepo, groupRepo, sessionRepo, settingsRepo, logReader, searchService, _tailService);

        var mainWindow = new MainWindow { DataContext = mainVm };
        mainWindow.Show();

        await mainVm.InitializeAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (MainWindow?.DataContext is MainViewModel vm)
        {
            vm.SaveSessionAsync().GetAwaiter().GetResult();
            vm.Dispose();
        }
        _tailService?.Dispose();
        base.OnExit(e);
    }
}
