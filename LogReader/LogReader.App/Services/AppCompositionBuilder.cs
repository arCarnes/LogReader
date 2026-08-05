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
        var logReader = new ChunkedLogReaderService();
        var searchService = new SearchService();
        IFileTailService tailService = new FileTailService();
        var encodingDetectionService = new FileEncodingDetectionService();

        var mainViewModel = new MainViewModel(
            fileRepo,
            groupRepo,
            settingsRepo,
            logReader,
            searchService,
            tailService,
            encodingDetectionService,
            enableLifecycleTimer);

        var liveLogEndpoint = new LiveLogEndpoint(
            logReader,
            searchService,
            encodingDetectionService,
            mainViewModel.FileSessionRegistry);

        return new AppComposition(mainViewModel, tailService, liveLogEndpoint);
    }
}
