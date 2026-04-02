namespace LogReader.App.ViewModels;

using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using LogReader.Core.Models;

public partial class LogGroupViewModel : ObservableObject
{
    private const double TreeIndentStep = 15d;
    private const double MemberFileLabelOffset = 22d;
    private const double GuideRailOffset = 9d;
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

    [ObservableProperty]
    private string _modifierLabel = string.Empty;

    public LogGroupViewModel? Parent { get; set; }
    public ObservableCollection<LogGroupViewModel> Children { get; } = new();

    public Thickness RowIndentMargin => new(Depth * TreeIndentStep, 0, 0, 0);
    public Thickness MemberFilesMargin => new((Depth * TreeIndentStep) + MemberFileLabelOffset, 0, 0, 4);
    public IReadOnlyList<Thickness> GuideRailMargins => Enumerable
        .Range(0, Depth)
        .Select(level => new Thickness((level * TreeIndentStep) + GuideRailOffset, 0, 0, 0))
        .ToArray();
    public LogGroupKind Kind => Model.Kind;
    public bool CanAddChild => Kind == LogGroupKind.Branch;
    public bool CanManageFiles => Kind == LogGroupKind.Dashboard;
    public string DisplayName => string.IsNullOrWhiteSpace(ModifierLabel)
        ? Name
        : $"{Name} [{ModifierLabel}]";
    public bool CanExpand => Kind == LogGroupKind.Branch
        ? Children.Count > 0
        : Model.FileIds.Count > 0 || MemberFiles.Count > 0;
    public int ErroredMemberFileCount => MemberFiles.Count(member => member.HasError);
    public bool HasMemberErrors => ErroredMemberFileCount > 0;
    public bool HasOnlyErroredMembers => MemberFiles.Count > 0 && ErroredMemberFileCount == MemberFiles.Count;

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
        OnPropertyChanged(nameof(RowIndentMargin));
        OnPropertyChanged(nameof(MemberFilesMargin));
        OnPropertyChanged(nameof(GuideRailMargins));
        OnPropertyChanged(nameof(CanAddChild));
    }

    partial void OnNameChanged(string value)
        => OnPropertyChanged(nameof(DisplayName));

    partial void OnIsExpandedChanged(bool value)
    {
        NotifyDescendantsTreeVisibility();
    }

    partial void OnIsFilterVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(IsTreeVisible));
    }

    partial void OnModifierLabelChanged(string value)
        => OnPropertyChanged(nameof(DisplayName));

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

        var nextName = EditName;
        var pendingModel = CloneModelWithName(nextName);
        await _saveCallback(pendingModel);

        Model.Name = nextName;
        Name = nextName;
        EditName = nextName;
        IsEditing = false;
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
        string? selectedFileId,
        bool showFullPath)
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
                    showFullPath,
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
                    showFullPath,
                    error,
                    isSelected: string.Equals(fileId, selectedFileId, StringComparison.Ordinal)));
            }
        }
    }

    public void RefreshMemberFile(
        string fileId,
        LogTabViewModel? openTab,
        string? storedFilePath,
        bool fileExists,
        string? selectedFileId,
        bool showFullPath)
    {
        var targetIndex = Model.FileIds.IndexOf(fileId);
        if (targetIndex < 0)
            return;

        var nextMember = CreateMemberFile(fileId, openTab, storedFilePath, fileExists, selectedFileId, showFullPath);
        var existingIndex = FindMemberFileIndex(fileId);
        if (nextMember == null)
        {
            if (existingIndex >= 0)
                MemberFiles.RemoveAt(existingIndex);

            return;
        }

        if (existingIndex >= 0)
        {
            if (existingIndex == targetIndex)
            {
                MemberFiles[existingIndex] = nextMember;
                return;
            }

            MemberFiles.RemoveAt(existingIndex);
        }

        MemberFiles.Insert(Math.Min(targetIndex, MemberFiles.Count), nextMember);
    }

    public void SetSelectedMemberFile(string? selectedFileId)
    {
        foreach (var member in MemberFiles.ToArray())
            member.IsSelected = string.Equals(member.FileId, selectedFileId, StringComparison.Ordinal);
    }

    public void SetSelectedMemberFilePath(string? selectedFilePath)
    {
        foreach (var member in MemberFiles.ToArray())
            member.IsSelected = string.Equals(member.FilePath, selectedFilePath, StringComparison.OrdinalIgnoreCase);
    }

    public void ReplaceMemberFiles(IEnumerable<GroupFileMemberViewModel> members)
    {
        MemberFiles.Clear();
        foreach (var member in members)
            MemberFiles.Add(member);
    }

    private int FindMemberFileIndex(string fileId)
    {
        for (var i = 0; i < MemberFiles.Count; i++)
        {
            if (string.Equals(MemberFiles[i].FileId, fileId, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    private static GroupFileMemberViewModel? CreateMemberFile(
        string fileId,
        LogTabViewModel? openTab,
        string? storedFilePath,
        bool fileExists,
        string? selectedFileId,
        bool showFullPath)
    {
        if (openTab != null)
        {
            return new GroupFileMemberViewModel(
                fileId,
                openTab.FileName,
                openTab.FilePath,
                showFullPath,
                isSelected: string.Equals(fileId, selectedFileId, StringComparison.Ordinal));
        }

        if (string.IsNullOrWhiteSpace(storedFilePath))
            return null;

        return new GroupFileMemberViewModel(
            fileId,
            Path.GetFileName(storedFilePath),
            storedFilePath,
            showFullPath,
            fileExists ? null : "File not found",
            isSelected: string.Equals(fileId, selectedFileId, StringComparison.Ordinal));
    }

    private LogGroup CloneModelWithName(string name)
    {
        return new LogGroup
        {
            Id = Model.Id,
            Name = name,
            SortOrder = Model.SortOrder,
            ParentGroupId = Model.ParentGroupId,
            Kind = Model.Kind,
            FileIds = Model.FileIds.ToList()
        };
    }

    private void OnStructureCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CanExpand));
        OnPropertyChanged(nameof(ErroredMemberFileCount));
        OnPropertyChanged(nameof(HasMemberErrors));
        OnPropertyChanged(nameof(HasOnlyErroredMembers));
    }
}

public partial class GroupFileMemberViewModel : ObservableObject
{
    public string FileId { get; }
    public string FileName { get; }
    public string FilePath { get; }
    public bool ShowFullPath { get; }
    public string? ErrorMessage { get; }
    public bool HasError => ErrorMessage != null;

    [ObservableProperty]
    private bool _isSelected;

    public GroupFileMemberViewModel(
        string fileId,
        string fileName,
        string filePath,
        bool showFullPath,
        string? errorMessage = null,
        bool isSelected = false)
    {
        FileId = fileId;
        FileName = fileName;
        FilePath = filePath;
        ShowFullPath = showFullPath;
        ErrorMessage = errorMessage;
        _isSelected = isSelected;
    }
}
