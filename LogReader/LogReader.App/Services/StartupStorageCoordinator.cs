namespace LogReader.App.Services;

using LogReader.App.ViewModels;
using LogReader.Core;

internal enum StartupStorageResult
{
    Ready,
    Canceled
}

internal interface IStartupStorageCoordinator
{
    StartupStorageResult EnsureStorageReady();
}

internal sealed class StartupStorageCoordinator : IStartupStorageCoordinator
{
    private readonly IStorageSetupDialogService _storageSetupDialogService;
    private readonly IFolderDialogService _folderDialogService;

    public StartupStorageCoordinator(
        IStorageSetupDialogService? storageSetupDialogService = null,
        IFolderDialogService? folderDialogService = null)
    {
        _storageSetupDialogService = storageSetupDialogService ?? new StorageSetupDialogService(new MessageBoxService());
        _folderDialogService = folderDialogService ?? new FolderDialogService();
    }

    public StartupStorageResult EnsureStorageReady()
    {
        while (true)
        {
            try
            {
                AppPaths.ValidateStorageConfiguration();
                return StartupStorageResult.Ready;
            }
            catch (StorageSetupRequiredException ex)
            {
                var viewModel = new StorageSetupViewModel(ex.SuggestedStorageRootPath, _folderDialogService);
                if (!_storageSetupDialogService.ShowDialog(viewModel))
                    return StartupStorageResult.Canceled;
            }
        }
    }
}
