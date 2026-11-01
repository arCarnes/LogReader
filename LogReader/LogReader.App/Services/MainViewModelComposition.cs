namespace LogReader.App.Services;

using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;

internal sealed record MainViewModelComposition(
    IFileDialogService FileDialogService,
    IMessageBoxService MessageBoxService,
    ISettingsDialogService SettingsDialogService,
    IBulkOpenPathsDialogService BulkOpenPathsDialogService,
    IDashboardTargetPickerDialogService DashboardTargetPickerDialogService,
    IMcpHelpDialogService McpHelpDialogService,
    Func<ISettingsRepository, SettingsViewModel> SettingsViewModelFactory,
    ILogAppearanceService LogAppearanceService,
    ITabLifecycleScheduler TabLifecycleScheduler,
    IWorkspaceServicesFactory WorkspaceFactory,
    IPersistedStateRecoveryCoordinator RecoveryCoordinator,
    IUiDispatcher UiDispatcher,
    IUiStateRepository UiStateRepository);

internal static class MainViewModelCompositionBuilder
{
    internal static MainViewModelComposition Create(
        ILogFileRepository fileRepo, ILogGroupRepository groupRepo, ISettingsRepository settingsRepo,
        ILogReaderService logReader, IFileTailService tailService, IEncodingDetectionService encodingDetectionService,
        IFileDialogService? fileDialogService, IMessageBoxService? messageBoxService,
        ISettingsDialogService? settingsDialogService, IBulkOpenPathsDialogService? bulkOpenPathsDialogService,
        IDashboardTargetPickerDialogService? dashboardTargetPickerDialogService, IMcpHelpDialogService? mcpHelpDialogService,
        Func<ISettingsRepository, SettingsViewModel>? settingsViewModelFactory,
        IWorkspaceServicesFactory? workspaceFactory, ILogAppearanceService? logAppearanceService,
        ITabLifecycleScheduler? tabLifecycleScheduler, LogFileCatalogService? fileCatalogService,
        TabWorkspaceService? tabWorkspace, DashboardWorkspaceService? dashboardWorkspace,
        DashboardActivationService? dashboardActivation, IPersistedStateRecoveryCoordinator? recoveryCoordinator,
        IUiDispatcher? uiDispatcher, IUiStateRepository? uiStateRepository)
    {
        var files = fileDialogService ?? new FileDialogService();
        var messages = messageBoxService ?? new MessageBoxService();
        var dispatcher = uiDispatcher ?? WpfUiDispatcher.Instance;
        return new MainViewModelComposition(
            files, messages,
            settingsDialogService ?? new SettingsDialogService(),
            bulkOpenPathsDialogService ?? new BulkOpenPathsDialogService(),
            dashboardTargetPickerDialogService ?? new DashboardTargetPickerDialogService(),
            mcpHelpDialogService ?? new McpHelpDialogService(),
            settingsViewModelFactory ?? (repo => new SettingsViewModel(repo, fileDialogService: files, messageBoxService: messages)),
            logAppearanceService ?? new WpfLogAppearanceService(),
            tabLifecycleScheduler ?? new WpfTabLifecycleScheduler(),
            workspaceFactory ?? new WorkspaceServicesFactory(fileRepo, groupRepo, logReader, tailService, encodingDetectionService,
                dispatcher, fileCatalogService, tabWorkspace, dashboardWorkspace, dashboardActivation),
            recoveryCoordinator ?? new PersistedStateRecoveryCoordinator(), dispatcher,
            uiStateRepository ?? new MemoryUiStateRepository());
    }
}
