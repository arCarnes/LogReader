namespace LogReader.App.Services;

using System.Windows;
using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Infrastructure.Repositories;
using LogReader.Infrastructure.Services;

internal interface IAppBootstrapper
{
    Task<AppComposition> CreateInitializedAsync(bool enableLifecycleTimer = true);
}

internal sealed class AppBootstrapper : IAppBootstrapper
{
    private readonly Func<bool, Task<AppComposition>> _createInitializedAsync;

    public AppBootstrapper()
        : this(CreateDefaultCompositionAsync)
    {
    }

    internal AppBootstrapper(Func<bool, Task<AppComposition>> createInitializedAsync)
    {
        _createInitializedAsync = createInitializedAsync;
    }

    public Task<AppComposition> CreateInitializedAsync(bool enableLifecycleTimer = true)
        => _createInitializedAsync(enableLifecycleTimer);

    private static async Task<AppComposition> CreateDefaultCompositionAsync(bool enableLifecycleTimer)
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

        try
        {
            await mainViewModel.InitializeAsync();
            return new AppComposition(mainViewModel, tailService);
        }
        catch
        {
            App.CleanupFailedStartup((Window?)null, mainViewModel, tailService);
            throw;
        }
    }
}

internal sealed record AppComposition(MainViewModel MainViewModel, IFileTailService TailService);
