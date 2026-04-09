namespace LogReader.App.ViewModels;

using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using LogReader.App.Models;
using LogReader.App.Services;
using LogReader.Core;
using LogReader.Core.Models;

public partial class MainViewModel
{
    [RelayCommand]
    private async Task CreateGroup()
    {
        await ExecuteRecoverableCommandAsync(() => _dashboardWorkspace.CreateGroupAsync(LogGroupKind.Dashboard));
    }

    [RelayCommand]
    private async Task CreateContainerGroup()
    {
        await ExecuteRecoverableCommandAsync(() => _dashboardWorkspace.CreateGroupAsync(LogGroupKind.Branch));
    }

    [RelayCommand]
    private Task CreateChildFolder(LogGroupViewModel? parent)
    {
        if (parent == null)
            return Task.CompletedTask;

        return RunViewActionAsync(() => CreateChildGroupAsync(parent, LogGroupKind.Branch));
    }

    [RelayCommand]
    private Task CreateChildDashboard(LogGroupViewModel? parent)
    {
        if (parent == null)
            return Task.CompletedTask;

        return RunViewActionAsync(() => CreateChildGroupAsync(parent, LogGroupKind.Dashboard));
    }

    [RelayCommand]
    private Task OpenDashboardGroup(LogGroupViewModel? group)
    {
        if (group == null)
            return Task.CompletedTask;

        return HandleDashboardGroupInvokedAsync(group);
    }

    public async Task<bool> CreateChildGroupAsync(LogGroupViewModel parent, LogGroupKind kind = LogGroupKind.Dashboard)
    {
        var result = await ExecuteRecoverableCommandAsync(
            () => _dashboardWorkspace.CreateChildGroupAsync(parent, kind),
            false);
        return result.Value;
    }

    [RelayCommand]
    private Task DeleteGroup(LogGroupViewModel? groupVm)
    {
        if (groupVm == null)
            return Task.CompletedTask;

        if (!ConfirmDeleteGroup(groupVm))
            return Task.CompletedTask;

        return RunViewActionAsync(() => DeleteGroupCoreAsync(groupVm));
    }

    private Task DeleteGroupCoreAsync(LogGroupViewModel? groupVm)
        => ExecuteRecoverableCommandAsync(() => _dashboardWorkspace.DeleteGroupAsync(groupVm));

    private bool ConfirmDeleteGroup(LogGroupViewModel groupVm)
    {
        var descendants = EnumerateDescendants(groupVm).ToList();
        var nestedDashboardCount = descendants.Count(group => group.Kind == LogGroupKind.Dashboard);
        var nestedFolderCount = descendants.Count(group => group.Kind == LogGroupKind.Branch);
        var itemLabel = groupVm.Kind == LogGroupKind.Branch ? "folder" : "dashboard";
        var lines = new List<string>
        {
            $"Delete the {itemLabel} \"{groupVm.Name}\"?",
            string.Empty,
            "This removes it from the current dashboard view."
        };

        if (nestedDashboardCount > 0 || nestedFolderCount > 0)
        {
            lines.Add(BuildNestedDeleteImpactMessage(nestedDashboardCount, nestedFolderCount));
        }

        lines.Add("This does not delete any log files from disk.");

        var result = _messageBoxService.Show(
            string.Join(Environment.NewLine, lines),
            groupVm.Kind == LogGroupKind.Branch ? "Delete Folder?" : "Delete Dashboard?",
            MessageBoxButton.YesNo,
            MessageBoxImage.None);
        return result == MessageBoxResult.Yes;
    }

    private static IEnumerable<LogGroupViewModel> EnumerateDescendants(LogGroupViewModel groupVm)
    {
        foreach (var child in groupVm.Children)
        {
            yield return child;
            foreach (var descendant in EnumerateDescendants(child))
                yield return descendant;
        }
    }

    private static string BuildNestedDeleteImpactMessage(int nestedDashboardCount, int nestedFolderCount)
    {
        var parts = new List<string>();
        if (nestedDashboardCount > 0)
            parts.Add(FormatCount(nestedDashboardCount, "nested dashboard"));

        if (nestedFolderCount > 0)
            parts.Add(FormatCount(nestedFolderCount, "nested folder"));

        return $"It also removes {string.Join(" and ", parts)}.";
    }

    private static string FormatCount(int count, string singularLabel)
        => count == 1 ? $"1 {singularLabel}" : $"{count} {singularLabel}s";

    [RelayCommand]
    private async Task ExportView()
    {
        await TryExportCurrentViewAsync();
    }

    [RelayCommand]
    private async Task ImportView()
    {
        var result = _fileDialogService.ShowOpenFileDialog(CreateImportViewDialogRequest());
        if (result.Accepted && result.FileNames.Count > 0)
        {
            ViewExport? export;
            try
            {
                export = await _dashboardWorkspace.ImportViewAsync(result.FileNames[0]);
            }
            catch (InvalidDataException ex)
            {
                _messageBoxService.Show(
                    ex.Message,
                    "Import Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }
            catch (IOException ex)
            {
                _messageBoxService.Show(
                    $"Could not read the selected view file: {ex.Message}",
                    "Import Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            if (export == null)
                return;
            if (!ConfirmImportPathTrust(export))
                return;
            if (!await ConfirmImportViewReplacementAsync())
                return;

            await ExecuteRecoverableCommandAsync(() => _dashboardWorkspace.ApplyImportedViewAsync(export));
        }
    }

    private async Task<bool> TryExportCurrentViewAsync()
    {
        var result = _fileDialogService.ShowSaveFileDialog(CreateExportViewDialogRequest());
        if (!result.Accepted || string.IsNullOrWhiteSpace(result.FileName))
            return false;

        var exportResult = await ExecuteRecoverableCommandAsync(
            async () =>
            {
                await _dashboardWorkspace.ExportViewAsync(result.FileName);
                return true;
            },
            false);
        return exportResult.Value;
    }

    private async Task<bool> ConfirmImportViewReplacementAsync()
    {
        if (Groups.Count == 0)
            return true;

        var result = _messageBoxService.Show(
            "Importing a view will replace your current dashboard view. Do you want to export it first?",
            "Export Current View?",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        return result switch
        {
            MessageBoxResult.Yes => await TryExportCurrentViewAsync(),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    private bool ConfirmImportPathTrust(ViewExport export)
    {
        var assessment = ImportedViewPathTrustAnalyzer.Assess(export);
        if (!assessment.RequiresConfirmation)
            return true;

        var result = _messageBoxService.Show(
            BuildImportPathTrustWarningMessage(assessment),
            "Import Non-Local Paths?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        return result == MessageBoxResult.Yes;
    }

    private static string BuildImportPathTrustWarningMessage(ImportedViewPathTrustAssessment assessment)
    {
        var pathLabel = assessment.SuspiciousPathCount == 1 ? "file path" : "file paths";
        var lines = new List<string>
        {
            $"The imported dashboard view contains {assessment.SuspiciousPathCount} {pathLabel} that are not normal local drive-qualified Windows paths.",
            string.Empty,
            "Opening dashboards from this import can trigger network or other non-local file-system access."
        };

        if (assessment.SamplePaths.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Examples:");
            foreach (var path in assessment.SamplePaths)
                lines.Add($"- {path}");

            var remainingCount = assessment.SuspiciousPathCount - assessment.SamplePaths.Count;
            if (remainingCount > 0)
                lines.Add($"...and {remainingCount} more.");
        }

        lines.Add(string.Empty);
        lines.Add("Do you want to import this view anyway?");
        return string.Join(Environment.NewLine, lines);
    }

    private SaveFileDialogRequest CreateExportViewDialogRequest()
    {
        return new SaveFileDialogRequest(
            "Export View",
            "LogReader View (*.json)|*.json",
            ".json",
            AddExtension: true,
            InitialDirectory: GetViewsDirectory(),
            FileName: CreateDefaultViewExportFileName());
    }

    private OpenFileDialogRequest CreateImportViewDialogRequest()
    {
        return new OpenFileDialogRequest(
            "Import View",
            "LogReader View (*.json)|*.json",
            InitialDirectory: GetViewsDirectory());
    }

    private static string GetViewsDirectory() => AppPaths.EnsureDirectory(AppPaths.ViewsDirectory);

    private static string CreateDefaultViewExportFileName() => $"logreader-view-{DateTime.Now:yyyy-MM-dd-HHmmss}.json";

    internal async Task ApplyImportedViewAsync(ViewExport export)
    {
        await ExecuteRecoverableCommandAsync(() => _dashboardWorkspace.ApplyImportedViewAsync(export));
    }

    [RelayCommand]
    private Task AddDashboardFiles(LogGroupViewModel? groupVm)
    {
        if (groupVm == null)
            return Task.CompletedTask;

        return RunViewActionAsync(() => AddFilesToDashboardAsync(groupVm));
    }

    public async Task AddFilesToDashboardAsync(LogGroupViewModel groupVm)
    {
        if (!groupVm.CanManageFiles)
            return;

        var result = _fileDialogService.ShowOpenFileDialog(
            new OpenFileDialogRequest(
                "Add Files to Dashboard",
                "Log Files (*.log;*.txt)|*.log;*.txt|All Files (*.*)|*.*",
                Multiselect: true,
                InitialDirectory: GetDefaultOpenDirectory()));

        if (!result.Accepted || result.FileNames.Count == 0)
            return;

        await ExecuteRecoverableCommandAsync(() => _dashboardWorkspace.AddFilesToDashboardAsync(groupVm, result.FileNames));
    }

    [RelayCommand]
    private Task BulkAddDashboardFiles(LogGroupViewModel? groupVm)
    {
        if (groupVm == null)
            return Task.CompletedTask;

        return RunViewActionAsync(() => BulkAddFilesToDashboardAsync(groupVm));
    }

    public async Task BulkAddFilesToDashboardAsync(LogGroupViewModel groupVm)
    {
        if (!groupVm.CanManageFiles)
            return;

        var result = _bulkOpenPathsDialogService.ShowDialog(
            new BulkOpenPathsDialogRequest(BulkOpenPathsScope.Dashboard, groupVm.Name));
        if (!result.Accepted)
            return;

        var filePaths = DashboardWorkspaceService.ParseBulkFilePaths(result.PathsText);
        if (filePaths.Count == 0)
            return;

        await ExecuteRecoverableCommandAsync(() => _dashboardWorkspace.AddFilesToDashboardAsync(groupVm, filePaths));
    }

    [RelayCommand]
    private async Task BulkOpenAdHocFiles()
    {
        var result = _bulkOpenPathsDialogService.ShowDialog(
            new BulkOpenPathsDialogRequest(BulkOpenPathsScope.AdHoc));
        if (!result.Accepted)
            return;

        var filePaths = DashboardWorkspaceService.ParseBulkFilePaths(result.PathsText);
        if (filePaths.Count == 0)
            return;

        foreach (var filePath in filePaths)
            await OpenFilePathAsync(filePath);
    }

    [RelayCommand]
    private async Task BulkAddFilesToActiveDashboard()
    {
        var activeDashboard = GetActiveDashboard();
        if (activeDashboard == null)
            return;

        await BulkAddFilesToDashboardAsync(activeDashboard);
    }

    public async Task RemoveFileFromDashboardAsync(LogGroupViewModel groupVm, string fileId)
    {
        await ExecuteRecoverableCommandAsync(() => _dashboardWorkspace.RemoveFileFromDashboardAsync(groupVm, fileId));
    }

    public async Task ReorderDashboardFileAsync(
        LogGroupViewModel groupVm,
        string draggedFileId,
        string targetFileId,
        DropPlacement placement)
    {
        await ExecuteRecoverableCommandAsync(() =>
            _dashboardWorkspace.ReorderFileInDashboardAsync(groupVm, draggedFileId, targetFileId, placement));
    }

    public async Task MoveDashboardFileAsync(
        LogGroupViewModel sourceGroupVm,
        LogGroupViewModel targetGroupVm,
        string draggedFileId,
        string? targetFileId,
        DropPlacement placement)
    {
        await ExecuteRecoverableCommandAsync(() =>
            _dashboardWorkspace.MoveFileBetweenDashboardsAsync(
                sourceGroupVm,
                targetGroupVm,
                draggedFileId,
                targetFileId,
                placement));
    }

    [RelayCommand]
    private Task MoveDashboardGroupUp(LogGroupViewModel? group)
    {
        if (group == null)
            return Task.CompletedTask;

        return RunViewActionAsync(() => MoveGroupUpAsync(group));
    }

    public async Task MoveGroupUpAsync(LogGroupViewModel group)
    {
        await ExecuteRecoverableCommandAsync(() => _dashboardWorkspace.MoveGroupUpAsync(group));
    }

    [RelayCommand]
    private Task MoveDashboardGroupDown(LogGroupViewModel? group)
    {
        if (group == null)
            return Task.CompletedTask;

        return RunViewActionAsync(() => MoveGroupDownAsync(group));
    }

    public async Task MoveGroupDownAsync(LogGroupViewModel group)
    {
        await ExecuteRecoverableCommandAsync(() => _dashboardWorkspace.MoveGroupDownAsync(group));
    }

    public bool CanMoveGroupTo(LogGroupViewModel source, LogGroupViewModel target, DropPlacement placement)
        => _dashboardWorkspace.CanMoveGroupTo(source, target, placement);

    public async Task MoveGroupToAsync(LogGroupViewModel source, LogGroupViewModel target, DropPlacement placement)
    {
        await ExecuteRecoverableCommandAsync(() => _dashboardWorkspace.MoveGroupToAsync(source, target, placement));
    }

    internal Task HandleDashboardGroupInvokedAsync(LogGroupViewModel group)
    {
        return RunViewActionAsync(async () =>
        {
            if (group.Kind == LogGroupKind.Dashboard)
            {
                var wasActiveDashboard = string.Equals(ActiveDashboardId, group.Id, StringComparison.Ordinal);
                if (!wasActiveDashboard)
                    ToggleGroupSelection(group);

                await OpenGroupFilesAsync(group);
                return;
            }

            ToggleGroupSelection(group);
        });
    }

    internal void BeginDashboardTreeRename(LogGroupViewModel? group)
    {
        if (group == null || group.IsEditing)
            return;

        group.BeginEdit();
    }

    internal async Task CommitDashboardTreeRenameAsync(LogGroupViewModel group)
    {
        if (!group.IsEditing)
            return;

        if (_isDashboardTreeRenameCommitPending && ReferenceEquals(_pendingDashboardTreeRenameGroup, group))
            return;

        _isDashboardTreeRenameCommitPending = true;
        _pendingDashboardTreeRenameGroup = group;
        try
        {
            await RunViewActionAsync(() => group.CommitEditAsync());
        }
        finally
        {
            if (ReferenceEquals(_pendingDashboardTreeRenameGroup, group))
            {
                _pendingDashboardTreeRenameGroup = null;
                _isDashboardTreeRenameCommitPending = false;
            }
        }
    }

    internal void CancelDashboardTreeRename(LogGroupViewModel group)
    {
        group.CancelEdit();
    }

    internal Task ApplyDashboardTreeModifierAsync(
        LogGroupViewModel? group,
        int daysBack,
        IReadOnlyList<ReplacementPattern> patterns,
        bool isAdHoc)
    {
        return RunViewActionAsync(async () =>
        {
            if (isAdHoc)
                await ApplyAdHocModifierAsync(daysBack, patterns);
            else if (group != null)
                await ApplyDashboardModifierAsync(group, daysBack, patterns);
        });
    }

    internal Task ClearDashboardTreeModifierAsync(LogGroupViewModel? group, bool isAdHoc)
    {
        return RunViewActionAsync(async () =>
        {
            if (isAdHoc)
                await ClearAdHocModifierAsync();
            else if (group != null)
                await ClearDashboardModifierAsync(group);
        });
    }

    internal Task OpenDashboardMemberFileAsync(LogGroupViewModel groupVm, GroupFileMemberViewModel fileVm)
    {
        return RunViewActionAsync(async () =>
        {
            if (groupVm.Kind != LogGroupKind.Dashboard)
            {
                await OpenFilePathAsync(fileVm.FilePath);
                return;
            }

            if (ActiveDashboardId != groupVm.Id)
                ToggleGroupSelection(groupVm);

            await OpenFilePathAsync(fileVm.FilePath);
            await _dashboardWorkspace.EnsureGroupFilesLoadedAsync(groupVm, new[] { fileVm.FilePath });
        });
    }

    internal Task ReloadDashboardAsync(LogGroupViewModel groupVm)
    {
        return RunViewActionAsync(async () =>
        {
            if (groupVm.Kind != LogGroupKind.Dashboard)
                return;

            if (ActiveDashboardId != groupVm.Id)
                ToggleGroupSelection(groupVm);

            var memberPaths = groupVm.MemberFiles
                .Select(member => member.FilePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var memberPath in memberPaths)
            {
                var tab = FindTabInScope(memberPath, groupVm.Id);
                if (tab != null)
                    await ForceReloadTabAsync(tab);
            }

            foreach (var memberPath in memberPaths)
            {
                if (FindTabInScope(memberPath, groupVm.Id) != null)
                    continue;

                await OpenFilePathInScopeAsync(
                    memberPath,
                    groupVm.Id,
                    reloadIfLoadError: true,
                    activateTab: false,
                    deferVisibilityRefresh: true);
            }
        });
    }

    internal Task ReloadDashboardMemberFileAsync(LogGroupViewModel groupVm, GroupFileMemberViewModel fileVm)
    {
        return RunViewActionAsync(async () =>
        {
            if (groupVm.Kind != LogGroupKind.Dashboard)
            {
                await OpenFilePathAsync(fileVm.FilePath);
                return;
            }

            if (ActiveDashboardId != groupVm.Id)
                ToggleGroupSelection(groupVm);

            await OpenFilePathAsync(fileVm.FilePath);

            var tab = FindTabInScope(fileVm.FilePath, groupVm.Id);
            if (tab != null)
                await ForceReloadTabAsync(tab);
        });
    }

    private static async Task ForceReloadTabAsync(LogTabViewModel tab)
    {
        await tab.ResetLineIndexAsync();
        await tab.LoadAsync();
    }

    internal Task RemoveDashboardMemberFileAsync(LogGroupViewModel groupVm, GroupFileMemberViewModel fileVm)
        => RunViewActionAsync(() => RemoveFileFromDashboardAsync(groupVm, fileVm.FileId));

    internal bool CanDropDashboardFileOnFile(
        LogGroupViewModel sourceGroupVm,
        LogGroupViewModel targetGroupVm,
        string draggedFileId,
        string targetFileId,
        DropPlacement placement)
    {
        return _dashboardWorkspace.CanDropDashboardFileOnFile(
            sourceGroupVm,
            targetGroupVm,
            draggedFileId,
            targetFileId,
            placement);
    }

    internal bool CanDropDashboardFileOnGroup(
        LogGroupViewModel sourceGroupVm,
        LogGroupViewModel targetGroupVm,
        string draggedFileId)
    {
        return _dashboardWorkspace.CanDropDashboardFileOnGroup(
            sourceGroupVm,
            targetGroupVm,
            draggedFileId);
    }

    internal Task ApplyDashboardFileDropAsync(
        LogGroupViewModel sourceGroupVm,
        LogGroupViewModel targetGroupVm,
        string draggedFileId,
        string? targetFileId,
        DropPlacement placement)
    {
        return ExecuteRecoverableCommandAsync(() => _dashboardWorkspace.ApplyDashboardFileDropAsync(
            sourceGroupVm,
            targetGroupVm,
            draggedFileId,
            targetFileId,
            placement));
    }

    public void ToggleGroupSelection(LogGroupViewModel group)
    {
        var previousActiveDashboardId = ActiveDashboardId;
        var nextActiveDashboardId = _dashboardScope.ToggleGroupSelection(Groups, previousActiveDashboardId, group);
        if (!string.Equals(previousActiveDashboardId, nextActiveDashboardId, StringComparison.Ordinal))
            _dashboardWorkspace.CancelDashboardLoad();

        ActiveDashboardId = nextActiveDashboardId;
        NotifyFilteredTabsChanged();
    }

    public async Task OpenGroupFilesAsync(LogGroupViewModel group)
    {
        await ExecuteRecoverableCommandAsync(() => _dashboardWorkspace.OpenGroupFilesAsync(group));
    }

    public Task<IReadOnlyList<ReplacementPattern>> LoadReplacementPatternsAsync()
        => Task.FromResult<IReadOnlyList<ReplacementPattern>>(
            _settings.DateRollingPatterns);

    public bool HasDashboardModifier(LogGroupViewModel group)
        => _dashboardWorkspace.HasDashboardModifier(group.Id);

    public bool HasAdHocModifier()
        => _dashboardWorkspace.HasAdHocModifier();

    public async Task ApplyDashboardModifierAsync(LogGroupViewModel group, int daysBack, ReplacementPattern pattern)
        => await ApplyDashboardModifierAsync(group, daysBack, new[] { pattern });

    public async Task ApplyDashboardModifierAsync(LogGroupViewModel group, int daysBack, IReadOnlyList<ReplacementPattern> patterns)
    {
        _dashboardScope.SelectDashboard(Groups, group);
        ActiveDashboardId = group.Id;
        await ExecuteRecoverableCommandAsync(async () =>
        {
            await _dashboardWorkspace.SetDashboardModifierAsync(group, daysBack, patterns);
            NotifyFilteredTabsChanged();
            await OpenGroupFilesAsync(group);
        });
    }

    public async Task ClearDashboardModifierAsync(LogGroupViewModel group)
    {
        var wasActiveScope = string.Equals(ActiveDashboardId, group.Id, StringComparison.Ordinal);
        await ExecuteRecoverableCommandAsync(async () =>
        {
            await _dashboardWorkspace.ClearDashboardModifierAsync(group);
            NotifyFilteredTabsChanged();
            if (wasActiveScope)
                await OpenGroupFilesAsync(group);
        });
    }

    public async Task ApplyAdHocModifierAsync(int daysBack, ReplacementPattern pattern)
        => await ApplyAdHocModifierAsync(daysBack, new[] { pattern });

    public async Task ApplyAdHocModifierAsync(int daysBack, IReadOnlyList<ReplacementPattern> patterns)
    {
        ActivateAdHocScope();
        await ExecuteRecoverableCommandAsync(async () =>
        {
            await _dashboardWorkspace.SetAdHocModifierAsync(daysBack, patterns);
            NotifyFilteredTabsChanged();
            if (_dashboardWorkspace.TryGetAdHocEffectivePaths(out var effectivePaths))
                await OpenPathsInCurrentScopeAsync(effectivePaths);
        });
    }

    public async Task ClearAdHocModifierAsync()
    {
        var basePaths = _dashboardWorkspace.GetAdHocBasePathsSnapshot();
        var wasAdHocScope = IsAdHocScopeActive;
        await ExecuteRecoverableCommandAsync(async () =>
        {
            await _dashboardWorkspace.ClearAdHocModifierAsync();
            NotifyFilteredTabsChanged();
            if (wasAdHocScope)
                await OpenPathsInCurrentScopeAsync(basePaths);
        });
    }

    private LogGroupViewModel? GetActiveDashboard()
    {
        if (string.IsNullOrEmpty(ActiveDashboardId))
            return null;

        return Groups.FirstOrDefault(group =>
            string.Equals(group.Id, ActiveDashboardId, StringComparison.Ordinal) &&
            group.CanManageFiles);
    }
}
