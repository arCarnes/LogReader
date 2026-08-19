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
        IDashboardTargetPickerDialogService? dashboardTargetPickerDialogService = null,
        IMcpHelpDialogService? mcpHelpDialogService = null)
    {
        var forbiddenUi = ForbiddenUiService.Instance;
        var resolvedViewModelReference = workspaceViewModelReference ?? new MainViewModelReference();
        var resolvedFileCatalogService = fileCatalogService ?? new LogFileCatalogService(fileRepo);
        var resolvedTabWorkspace = tabWorkspace ?? new TabWorkspaceService(
            new TabWorkspaceHostAdapter(resolvedViewModelReference),
            fileRepo,
            logReader,
            tailService,
            encodingDetectionService,
            resolvedFileCatalogService,
            ImmediateUiDispatcher.Instance);
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
            resolvedViewModelReference,
            logAppearanceService ?? new StubLogAppearanceService(),
            tabLifecycleScheduler ?? new StubTabLifecycleScheduler(),
            resolvedFileCatalogService,
            resolvedTabWorkspace,
            dashboardWorkspace,
            dashboardActivation,
            dashboardTargetPickerDialogService ?? forbiddenUi,
            mcpHelpDialogService ?? forbiddenUi);
    }

    private sealed class ImmediateUiDispatcher : IUiDispatcher
    {
        public static ImmediateUiDispatcher Instance { get; } = new();

        public bool CheckAccess() => true;

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task InvokeAsync(Func<Task> action) => action();
    }
}
