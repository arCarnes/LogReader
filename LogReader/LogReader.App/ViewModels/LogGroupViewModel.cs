namespace LogReader.App.ViewModels;

using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using LogReader.App.Services;
using LogReader.Core.Models;

public partial class LogGroupViewModel : ObservableObject
{
    private const double TreeIndentStep = 15d;
    private const double MemberFileLabelOffset = 22d;
    private const double GuideRailOffset = 9d;
    private readonly Func<LogGroup, Task> _saveCallback;
    private int _erroredMemberFileCount;

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
    public int ErroredMemberFileCount => _erroredMemberFileCount;
    public bool HasErroredMemberFiles => _erroredMemberFileCount > 0;
    public string ErrorCountTag => $"({_erroredMemberFileCount})";

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

    public ObservableCollection<GroupFileMemberViewModel> MemberFiles { get; } = new BulkObservableCollection<GroupFileMemberViewModel>();
    public int BatchSelectedMemberFileCount => MemberFiles.Count(member => member.IsBatchSelected);
    public bool HasBatchSelectedMemberFiles => BatchSelectedMemberFileCount > 0;

    private string? _batchSelectionAnchorFileId;

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
        => RefreshMemberFiles(
            BuildOpenTabsByFileId(allTabs),
            fileIdToPath,
            fileExistenceById,
            selectedFileId,
            showFullPath);

    public void RefreshMemberFiles(
        IReadOnlyDictionary<string, LogTabViewModel> openTabsByFileId,
        IReadOnlyDictionary<string, string> fileIdToPath,
        IReadOnlyDictionary<string, bool> fileExistenceById,
        string? selectedFileId,
        bool showFullPath)
        => RefreshMemberFiles(
            openTabsByFileId,
            fileIdToPath,
            ToProbeResults(fileExistenceById),
            selectedFileId,
            showFullPath);

    public void RefreshMemberFiles(
        IEnumerable<LogTabViewModel> allTabs,
        IReadOnlyDictionary<string, string> fileIdToPath,
        IReadOnlyDictionary<string, DashboardFileProbeResult> fileStatusById,
        string? selectedFileId,
        bool showFullPath)
        => RefreshMemberFiles(
            BuildOpenTabsByFileId(allTabs),
            fileIdToPath,
            fileStatusById,
            selectedFileId,
            showFullPath);

    public void RefreshMemberFiles(
        IReadOnlyDictionary<string, LogTabViewModel> openTabsByFileId,
        IReadOnlyDictionary<string, string> fileIdToPath,
        IReadOnlyDictionary<string, DashboardFileProbeResult> fileStatusById,
        string? selectedFileId,
        bool showFullPath)
    {
        var nextMembers = new List<GroupFileMemberViewModel>();
        foreach (var fileId in Model.FileIds)
        {
            if (openTabsByFileId.TryGetValue(fileId, out var tab))
            {
                nextMembers.Add(new GroupFileMemberViewModel(
                    fileId,
                    tab.FileName,
                    tab.FilePath,
                    showFullPath,
                    isSelected: string.Equals(fileId, selectedFileId, StringComparison.Ordinal),
                    fileSizeText: GroupFileMemberViewModel.CreateFileSizeText(tab)));
            }
            else if (fileIdToPath.TryGetValue(fileId, out var path))
            {
                var fileName = Path.GetFileName(path);
                fileStatusById.TryGetValue(fileId, out var fileStatus);
                nextMembers.Add(new GroupFileMemberViewModel(
                    fileId,
                    fileName,
                    path,
                    showFullPath,
                    fileStatus.ErrorMessage,
                    isSelected: string.Equals(fileId, selectedFileId, StringComparison.Ordinal)));
            }
        }

        ReplaceMemberFiles(nextMembers);
    }

    public void RefreshMemberFile(
        string fileId,
        LogTabViewModel? openTab,
        string? storedFilePath,
        bool fileExists,
        string? selectedFileId,
        bool showFullPath)
        => RefreshMemberFile(
            fileId,
            openTab,
            storedFilePath,
            fileExists ? DashboardFileProbeResult.Found : DashboardFileProbeResult.Missing,
            selectedFileId,
            showFullPath);

    public void RefreshMemberFile(
        string fileId,
        LogTabViewModel? openTab,
        string? storedFilePath,
        DashboardFileProbeResult fileStatus,
        string? selectedFileId,
        bool showFullPath)
    {
        var targetIndex = Model.FileIds.IndexOf(fileId);
        if (targetIndex < 0)
            return;

        var nextMember = CreateMemberFile(fileId, openTab, storedFilePath, fileStatus, selectedFileId, showFullPath);
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

    public void ClearBatchSelectedMemberFiles()
    {
        var changed = false;
        foreach (var member in MemberFiles.ToArray())
        {
            if (!member.IsBatchSelected)
                continue;

            member.IsBatchSelected = false;
            changed = true;
        }

        if (_batchSelectionAnchorFileId != null)
        {
            _batchSelectionAnchorFileId = null;
            changed = true;
        }

        if (changed)
            NotifyBatchSelectionChanged();
    }

    public void SelectOnlyBatchMemberFile(GroupFileMemberViewModel fileVm)
    {
        var changed = false;
        foreach (var member in MemberFiles.ToArray())
        {
            var shouldSelect = ReferenceEquals(member, fileVm);
            if (member.IsBatchSelected == shouldSelect)
                continue;

            member.IsBatchSelected = shouldSelect;
            changed = true;
        }

        if (!string.Equals(_batchSelectionAnchorFileId, fileVm.FileId, StringComparison.Ordinal))
        {
            _batchSelectionAnchorFileId = fileVm.FileId;
            changed = true;
        }

        if (changed)
            NotifyBatchSelectionChanged();
    }

    public void ToggleBatchMemberFile(GroupFileMemberViewModel fileVm)
    {
        fileVm.IsBatchSelected = !fileVm.IsBatchSelected;
        _batchSelectionAnchorFileId = fileVm.FileId;
        NotifyBatchSelectionChanged();
    }

    public void SelectBatchMemberFileRange(GroupFileMemberViewModel fileVm)
    {
        var targetIndex = MemberFiles.IndexOf(fileVm);
        if (targetIndex < 0)
            return;

        var anchorIndex = string.IsNullOrWhiteSpace(_batchSelectionAnchorFileId)
            ? -1
            : FindMemberFileIndex(_batchSelectionAnchorFileId);
        if (anchorIndex < 0)
        {
            SelectOnlyBatchMemberFile(fileVm);
            return;
        }

        var start = Math.Min(anchorIndex, targetIndex);
        var end = Math.Max(anchorIndex, targetIndex);
        var changed = false;
        for (var i = 0; i < MemberFiles.Count; i++)
        {
            var shouldSelect = i >= start && i <= end;
            if (MemberFiles[i].IsBatchSelected == shouldSelect)
                continue;

            MemberFiles[i].IsBatchSelected = shouldSelect;
            changed = true;
        }

        if (changed)
            NotifyBatchSelectionChanged();
    }

    public IReadOnlyList<GroupFileMemberViewModel> GetBatchSelectedMemberFiles()
        => MemberFiles.Where(member => member.IsBatchSelected).ToList();

    public void ReplaceMemberFiles(IEnumerable<GroupFileMemberViewModel> members)
    {
        var nextMembers = members as IList<GroupFileMemberViewModel> ?? members.ToList();
        var selectedFileIds = MemberFiles
            .Where(member => member.IsBatchSelected)
            .Select(member => member.FileId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var member in nextMembers)
            member.IsBatchSelected = selectedFileIds.Contains(member.FileId);

        if (_batchSelectionAnchorFileId != null && !nextMembers.Any(member => string.Equals(member.FileId, _batchSelectionAnchorFileId, StringComparison.Ordinal)))
            _batchSelectionAnchorFileId = null;

        UpdateErroredMemberFileCount(nextMembers.Count(member => member.HasError));

        if (MemberFiles is BulkObservableCollection<GroupFileMemberViewModel> bulkMemberFiles)
        {
            bulkMemberFiles.ReplaceAll(nextMembers);
            NotifyBatchSelectionChanged();
            return;
        }

        MemberFiles.Clear();
        foreach (var member in nextMembers)
            MemberFiles.Add(member);

        NotifyBatchSelectionChanged();
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
        DashboardFileProbeResult fileStatus,
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
                isSelected: string.Equals(fileId, selectedFileId, StringComparison.Ordinal),
                fileSizeText: GroupFileMemberViewModel.CreateFileSizeText(openTab));
        }

        if (string.IsNullOrWhiteSpace(storedFilePath))
            return null;

        return new GroupFileMemberViewModel(
            fileId,
            Path.GetFileName(storedFilePath),
            storedFilePath,
            showFullPath,
            fileStatus.ErrorMessage,
            isSelected: string.Equals(fileId, selectedFileId, StringComparison.Ordinal));
    }

    private static IReadOnlyDictionary<string, DashboardFileProbeResult> ToProbeResults(
        IReadOnlyDictionary<string, bool> fileExistenceById)
    {
        return fileExistenceById.ToDictionary(
            entry => entry.Key,
            entry => entry.Value ? DashboardFileProbeResult.Found : DashboardFileProbeResult.Missing,
            StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, LogTabViewModel> BuildOpenTabsByFileId(IEnumerable<LogTabViewModel> allTabs)
    {
        var openTabsByFileId = new Dictionary<string, LogTabViewModel>(StringComparer.Ordinal);
        foreach (var tab in allTabs)
        {
            if (!openTabsByFileId.ContainsKey(tab.FileId))
                openTabsByFileId.Add(tab.FileId, tab);
        }

        return openTabsByFileId;
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
        if (!ReferenceEquals(sender, MemberFiles))
            return;

        UpdateErroredMemberFileCount(MemberFiles.Count(member => member.HasError));
        NotifyBatchSelectionChanged();
    }

    private void NotifyBatchSelectionChanged()
    {
        OnPropertyChanged(nameof(BatchSelectedMemberFileCount));
        OnPropertyChanged(nameof(HasBatchSelectedMemberFiles));
    }

    private void UpdateErroredMemberFileCount(int nextCount)
    {
        if (_erroredMemberFileCount == nextCount)
            return;

        _erroredMemberFileCount = nextCount;
        OnPropertyChanged(nameof(ErroredMemberFileCount));
        OnPropertyChanged(nameof(HasErroredMemberFiles));
        OnPropertyChanged(nameof(ErrorCountTag));
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
    public string? HostName { get; }
    public bool HasHostName => !string.IsNullOrWhiteSpace(HostName);
    public string? FileSizeText { get; }
    public bool HasFileSize => !string.IsNullOrWhiteSpace(FileSizeText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHighlighted))]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHighlighted))]
    private bool _isBatchSelected;

    public bool IsHighlighted => IsSelected || IsBatchSelected;

    public GroupFileMemberViewModel(
        string fileId,
        string fileName,
        string filePath,
        bool showFullPath,
        string? errorMessage = null,
        bool isSelected = false,
        string? fileSizeText = null)
    {
        FileId = fileId;
        FileName = fileName;
        FilePath = filePath;
        ShowFullPath = showFullPath;
        ErrorMessage = errorMessage;
        HostName = CreateHostNameText(filePath);
        FileSizeText = fileSizeText;
        _isSelected = isSelected;
    }

    public static string? CreateFileSizeText(LogTabViewModel tab)
        => tab.FileSizeBytes == null ? null : FormatFileSize(tab.FileSizeBytes.Value);

    public static string CreateFileSizeText(long fileSizeBytes)
        => FormatFileSize(fileSizeBytes);

    public static string? CreateHostNameText(string filePath)
        => PathHostDisplayResolver.Shared.CreateHostNameText(filePath);

    public static string FormatFileSize(long bytes)
    {
        const decimal OneKb = 1024m;
        const decimal OneMb = OneKb * 1024m;
        const decimal OneGb = OneMb * 1024m;

        if (bytes < 0)
            bytes = 0;

        if (bytes < 1024)
            return string.Format(CultureInfo.CurrentCulture, "{0:N0} bytes", bytes);

        if (bytes < OneMb)
            return string.Format(CultureInfo.CurrentCulture, "{0:N1} KB", bytes / OneKb);

        if (bytes < OneGb)
            return string.Format(CultureInfo.CurrentCulture, "{0:N1} MB", bytes / OneMb);

        return string.Format(CultureInfo.CurrentCulture, "{0:N1} GB", bytes / OneGb);
    }
}
