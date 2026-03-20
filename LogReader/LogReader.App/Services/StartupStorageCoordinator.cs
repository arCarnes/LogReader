namespace LogReader.App.Services;

using LogReader.App.ViewModels;
using LogReader.App.Views;
using LogReader.Core;

internal enum StartupStorageResult
{
    Ready,
    Canceled
}

internal sealed class StartupStorageCoordinator
{
    internal Func<StorageSetupViewModel, bool> ShowStorageSetupDialog { get; set; } = static viewModel =>
    {
        var window = new StorageSetupWindow
        {
            DataContext = viewModel
        };

        return window.ShowDialog() == true;
    };

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
                var viewModel = new StorageSetupViewModel(ex.SuggestedStorageRootPath);
                if (!ShowStorageSetupDialog(viewModel))
                    return StartupStorageResult.Canceled;
            }
        }
    }
}
