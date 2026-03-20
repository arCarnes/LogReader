namespace LogReader.App.ViewModels;

using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogReader.Core;

internal partial class StorageSetupViewModel : ObservableObject
{
    internal Func<System.Windows.Forms.FolderBrowserDialog, System.Windows.Forms.DialogResult> ShowFolderBrowserDialog { get; set; }
        = static dialog => dialog.ShowDialog();

    internal Action<string> ValidateStorageRoot { get; set; }
        = AppPaths.ValidateStorageRoot;

    internal Action<string> SaveStorageSelection { get; set; } = AppPaths.SaveMsiUserStorageSelection;

    public StorageSetupViewModel(string defaultStorageRootPath)
    {
        _storageRootPath = defaultStorageRootPath;
    }

    [ObservableProperty]
    private string _storageRootPath;

    [RelayCommand]
    private void BrowseStorageRoot()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select the folder where LogReader should store Data and Cache for this Windows user.",
            UseDescriptionForTitle = true
        };

        var initialDirectory = GetInitialDirectory();
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            dialog.InitialDirectory = initialDirectory;

        if (ShowFolderBrowserDialog(dialog) == System.Windows.Forms.DialogResult.OK)
            StorageRootPath = dialog.SelectedPath;
    }

    public bool TryComplete(out string errorMessage)
    {
        var candidatePath = (StorageRootPath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            errorMessage = "Choose a storage folder before continuing.";
            return false;
        }

        try
        {
            ValidateStorageRoot(candidatePath);

            var normalizedPath = Path.GetFullPath(candidatePath);
            SaveStorageSelection(normalizedPath);
            StorageRootPath = normalizedPath;

            errorMessage = string.Empty;
            return true;
        }
        catch (ProtectedStorageLocationException ex)
        {
            errorMessage = ex.Message;
            return false;
        }
        catch (StorageValidationException ex)
        {
            errorMessage = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            errorMessage = $"LogReader could not save the storage selection.{Environment.NewLine}{Environment.NewLine}{ex.Message}";
            return false;
        }
    }

    private string? GetInitialDirectory()
    {
        if (Directory.Exists(StorageRootPath))
            return StorageRootPath;

        var parentPath = Path.GetDirectoryName(StorageRootPath);
        if (!string.IsNullOrWhiteSpace(parentPath) && Directory.Exists(parentPath))
            return parentPath;

        return null;
    }
}
