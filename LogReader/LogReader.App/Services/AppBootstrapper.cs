namespace LogReader.App.Services;

using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Infrastructure.Repositories;
using LogReader.Infrastructure.Services;

internal sealed class AppBootstrapper
{
    public async Task<AppComposition> CreateInitializedAsync(bool enableLifecycleTimer = true)
    {
        ILogFileRepository fileRepo = new JsonLogFileRepository();
        ILogGroupRepository groupRepo = new JsonLogGroupRepository(fileRepo);
        ISettingsRepository settingsRepo = new JsonSettingsRepository();
        ILogReaderService logReader = new ChunkedLogReaderService();
        ISearchService searchService = new SearchService();
        IFileTailService tailService = new FileTailService();
        IEncodingDetectionService encodingDetectionService = new FileEncodingDetectionService();
        ILogTimestampNavigationService timestampNavigationService = new LogTimestampNavigationService();

        var mainViewModel = new MainViewModel(
            fileRepo,
            groupRepo,
            settingsRepo,
            logReader,
            searchService,
            tailService,
            encodingDetectionService,
            timestampNavigationService,
            enableLifecycleTimer);
        await mainViewModel.InitializeAsync();

        return new AppComposition(mainViewModel, tailService);
    }
}

internal sealed record AppComposition(MainViewModel MainViewModel, IFileTailService TailService);
