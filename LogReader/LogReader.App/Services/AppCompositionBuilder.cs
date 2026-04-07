namespace LogReader.App.Services;

using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Infrastructure.Repositories;
using LogReader.Infrastructure.Services;

internal interface IAppCompositionBuilder
{
    AppComposition Build(bool enableLifecycleTimer = true);
}

internal sealed class AppCompositionBuilder : IAppCompositionBuilder
{
    public AppComposition Build(bool enableLifecycleTimer = true)
    {
        ILogFileRepository fileRepo = new JsonLogFileRepository();
        ILogGroupRepository groupRepo = new JsonLogGroupRepository(fileRepo);
        ISettingsRepository settingsRepo = new JsonSettingsRepository();
        ILogReaderService logReader = new ChunkedLogReaderService();
        ISearchService searchService = new SearchService();
        IFileTailService tailService = new FileTailService();
        IEncodingDetectionService encodingDetectionService = new FileEncodingDetectionService();

        var mainViewModel = new MainViewModel(
            fileRepo,
            groupRepo,
            settingsRepo,
            logReader,
            searchService,
            tailService,
            encodingDetectionService,
            enableLifecycleTimer);

        return new AppComposition(mainViewModel, tailService);
    }
}
