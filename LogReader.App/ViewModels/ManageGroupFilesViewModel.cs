namespace LogReader.App.ViewModels;

using System.IO;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

public partial class FileSelectionItem : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public string? FileId { get; }
    public string FileName { get; }
    public string FilePath { get; }

    public FileSelectionItem(string? fileId, string fileName, string filePath, bool isSelected)
    {
        FileId = fileId;
        FileName = fileName;
        FilePath = filePath;
        _isSelected = isSelected;
    }
}

public class ManageGroupFilesViewModel
{
    public string GroupName { get; }
    public ObservableCollection<FileSelectionItem> Files { get; } = new();

    public ManageGroupFilesViewModel(LogGroupViewModel group, IEnumerable<LogTabViewModel> allTabs)
    {
        GroupName = group.Name;
        foreach (var tab in allTabs)
        {
            Files.Add(new FileSelectionItem(
                tab.FileId,
                tab.FileName,
                tab.FilePath,
                group.Model.FileIds.Contains(tab.FileId)));
        }
    }

    public void AddFiles(IEnumerable<string> filePaths)
    {
        var existingPaths = new HashSet<string>(
            Files.Select(f => f.FilePath),
            StringComparer.OrdinalIgnoreCase);

        foreach (var path in filePaths)
        {
            if (string.IsNullOrWhiteSpace(path) || existingPaths.Contains(path))
            {
                continue;
            }

            Files.Add(new FileSelectionItem(
                fileId: null,
                fileName: Path.GetFileName(path),
                filePath: path,
                isSelected: true));
            existingPaths.Add(path);
        }
    }

    public void SelectAll() { foreach (var f in Files) f.IsSelected = true; }
    public void DeselectAll() { foreach (var f in Files) f.IsSelected = false; }

    public IReadOnlyList<string> GetSelectedFileIds() =>
        Files.Where(f => f.IsSelected && !string.IsNullOrWhiteSpace(f.FileId))
            .Select(f => f.FileId!)
            .ToList()
            .AsReadOnly();

    public IReadOnlyList<string> GetSelectedFilePathsWithoutIds() =>
        Files.Where(f => f.IsSelected && string.IsNullOrWhiteSpace(f.FileId))
            .Select(f => f.FilePath)
            .ToList()
            .AsReadOnly();
}
