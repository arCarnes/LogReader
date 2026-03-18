namespace LogReader.App.ViewModels;

using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using LogReader.Core.Models;

public partial class LogGroupViewModel : ObservableObject
{
    private readonly Func<LogGroup, Task> _saveCallback;

    public LogGroup Model { get; }
    public string Id => Model.Id;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editName = string.Empty;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private int _depth;

    [ObservableProperty]
    private bool _isFilterVisible = true;

    public LogGroupViewModel? Parent { get; set; }
    public ObservableCollection<LogGroupViewModel> Children { get; } = new();

    public Thickness IndentMargin => new(Depth * 20, 0, 0, 0);
    public LogGroupKind Kind => Model.Kind;
    public bool CanAddChild => Kind == LogGroupKind.Branch;
    public bool CanManageFiles => Kind == LogGroupKind.Dashboard;
    public bool CanExpand => Kind == LogGroupKind.Branch
        ? Children.Count > 0
        : MemberFiles.Count > 0;

    public bool IsTreeVisible
    {
        get
        {
            if (!IsFilterVisible) return false;
            var current = Parent;
            while (current != null)
            {
                if (!current.IsExpanded) return false;
                current = current.Parent;
            }
            return true;
        }
    }

    public ObservableCollection<GroupFileMemberViewModel> MemberFiles { get; } = new();

    public LogGroupViewModel(LogGroup model, Func<LogGroup, Task> saveCallback)
    {
        Model = model;
        _name = model.Name;
        _saveCallback = saveCallback;
        Children.CollectionChanged += OnStructureCollectionChanged;
        MemberFiles.CollectionChanged += OnStructureCollectionChanged;
    }

    partial void OnDepthChanged(int value)
    {
        OnPropertyChanged(nameof(IndentMargin));
        OnPropertyChanged(nameof(CanAddChild));
    }

    partial void OnIsExpandedChanged(bool value)
    {
        NotifyDescendantsTreeVisibility();
    }

    partial void OnIsFilterVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(IsTreeVisible));
    }

    private void NotifyDescendantsTreeVisibility()
    {
        foreach (var child in Children)
        {
            child.OnPropertyChanged(nameof(IsTreeVisible));
            child.NotifyDescendantsTreeVisibility();
        }
    }

    public void AddChild(LogGroupViewModel child)
    {
        Children.Add(child);
        NotifyStructureChanged();
    }

    public void NotifyStructureChanged()
    {
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(CanAddChild));
        OnPropertyChanged(nameof(CanManageFiles));
        OnPropertyChanged(nameof(CanExpand));
    }

    public void BeginEdit()
    {
        EditName = Name;
        IsEditing = true;
    }

    public async Task CommitEditAsync()
    {
        if (string.IsNullOrWhiteSpace(EditName))
        {
            CancelEdit();
            return;
        }
        IsEditing = false;
        Name = EditName;
        Model.Name = EditName;
        await _saveCallback(Model);
    }

    public void CancelEdit()
    {
        IsEditing = false;
        EditName = Name;
    }

    public void RefreshMemberFiles(
        IEnumerable<LogTabViewModel> allTabs,
        IReadOnlyDictionary<string, string> fileIdToPath,
        IReadOnlyDictionary<string, bool> fileExistenceById,
        string? selectedFileId)
    {
        MemberFiles.Clear();
        foreach (var fileId in Model.FileIds)
        {
            var tab = allTabs.FirstOrDefault(t => t.FileId == fileId);
            if (tab != null)
            {
                MemberFiles.Add(new GroupFileMemberViewModel(
                    fileId,
                    tab.FileName,
                    tab.FilePath,
                    isSelected: string.Equals(fileId, selectedFileId, StringComparison.Ordinal)));
            }
            else if (fileIdToPath.TryGetValue(fileId, out var path))
            {
                var fileName = Path.GetFileName(path);
                var error = fileExistenceById.TryGetValue(fileId, out var fileExists) && !fileExists
                    ? "File not found"
                    : null;
                MemberFiles.Add(new GroupFileMemberViewModel(
                    fileId,
                    fileName,
                    path,
                    error,
                    isSelected: string.Equals(fileId, selectedFileId, StringComparison.Ordinal)));
            }
        }
    }

    public void SetSelectedMemberFile(string? selectedFileId)
    {
        foreach (var member in MemberFiles)
            member.IsSelected = string.Equals(member.FileId, selectedFileId, StringComparison.Ordinal);
    }

    private void OnStructureCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CanExpand));
    }
}

public partial class GroupFileMemberViewModel : ObservableObject
{
    public string FileId { get; }
    public string FileName { get; }
    public string FilePath { get; }
    public string? ErrorMessage { get; }
    public bool HasError => ErrorMessage != null;

    [ObservableProperty]
    private bool _isSelected;

    public GroupFileMemberViewModel(
        string fileId,
        string fileName,
        string filePath,
        string? errorMessage = null,
        bool isSelected = false)
    {
        FileId = fileId;
        FileName = fileName;
        FilePath = filePath;
        ErrorMessage = errorMessage;
        _isSelected = isSelected;
    }
}
