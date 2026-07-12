namespace LogReader.Tests;

using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;

internal static class TestMainViewModelFactory
{
    public static MainViewModel Create(
        ILogFileRepository fileRepo,
        ILogGroupRepository groupRepo,
        ISettingsRepository settingsRepo,
        ILogReaderService logReader,
        ISearchService searchService,
        IFileTailService tailService,
        IEncodingDetectionService encodingDetectionService,
        bool enableLifecycleTimer = false,
        IFileDialogService? fileDialogService = null,
        IMessageBoxService? messageBoxService = null,
        ISettingsDialogService? settingsDialogService = null,
        IBulkOpenPathsDialogService? bulkOpenPathsDialogService = null,
        Func<ISettingsRepository, SettingsViewModel>? settingsViewModelFactory = null,
        IPersistedStateRecoveryCoordinator? persistedStateRecoveryCoordinator = null,
        MainViewModelReference? workspaceViewModelReference = null,
        ILogAppearanceService? logAppearanceService = null,
        ITabLifecycleScheduler? tabLifecycleScheduler = null,
        LogFileCatalogService? fileCatalogService = null,
        TabWorkspaceService? tabWorkspace = null,
        DashboardWorkspaceService? dashboardWorkspace = null,
        DashboardActivationService? dashboardActivation = null,
        IDashboardTargetPickerDialogService? dashboardTargetPickerDialogService = null)
    {
        var forbiddenUi = ForbiddenUiService.Instance;
        return new MainViewModel(
            fileRepo,
            groupRepo,
            settingsRepo,
            logReader,
            searchService,
            tailService,
            encodingDetectionService,
            enableLifecycleTimer,
            fileDialogService ?? forbiddenUi,
            messageBoxService ?? forbiddenUi,
            settingsDialogService ?? forbiddenUi,
            bulkOpenPathsDialogService ?? forbiddenUi,
            settingsViewModelFactory,
            persistedStateRecoveryCoordinator,
            workspaceViewModelReference,
            logAppearanceService ?? new StubLogAppearanceService(),
            tabLifecycleScheduler ?? new StubTabLifecycleScheduler(),
            fileCatalogService,
            tabWorkspace,
            dashboardWorkspace,
            dashboardActivation,
            dashboardTargetPickerDialogService ?? forbiddenUi);
    }
}
