namespace LogReader.App.ViewModels;

using System.IO;
using System.Windows;
using LogReader.App.Services;
using LogReader.Core;
using LogReader.Core.Models;

public partial class MainViewModel
{
    private async Task<bool> ExecuteRecoverableCommandAsync(Func<Task> operation)
    {
        try
        {
            await _runtimeRecoveryExecutor.ExecuteAsync(operation);
            return true;
        }
        catch (RuntimePersistedStateRecoveryFailedException ex)
        {
            ShowRecoveryFailure(ex);
            return false;
        }
    }

    private async Task<(bool Succeeded, T Value)> ExecuteRecoverableCommandAsync<T>(Func<Task<T>> operation, T failureValue)
    {
        try
        {
            return (true, await _runtimeRecoveryExecutor.ExecuteAsync(operation));
        }
        catch (RuntimePersistedStateRecoveryFailedException ex)
        {
            ShowRecoveryFailure(ex);
            return (false, failureValue);
        }
    }

    private void RunRecoverableBackgroundCommand(Func<Task> operation)
    {
        _ = RunRecoverableBackgroundCommandCoreAsync(operation);
    }

    private async Task RunRecoverableBackgroundCommandCoreAsync(Func<Task> operation)
    {
        try
        {
            await _runtimeRecoveryExecutor.ExecuteAsync(operation);
        }
        catch (RuntimePersistedStateRecoveryFailedException ex)
        {
            ShowRecoveryFailure(ex);
        }
    }

    private void ShowRecoveryFailure(RuntimePersistedStateRecoveryFailedException exception)
    {
        _messageBoxService.Show(
            exception.Message,
            "WeezTail Recovery Failed",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    public async Task RunViewActionAsync(Func<Task> operation, string failureCaption = "WeezTail Error")
    {
        ArgumentNullException.ThrowIfNull(operation);

        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
        }
        catch (RuntimePersistedStateRecoveryFailedException ex)
        {
            ShowRecoveryFailure(ex);
        }
        catch (Exception ex)
        {
            _messageBoxService.Show(
                $"The requested action could not be completed.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                failureCaption,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task<IReadOnlyList<string>> GetRecoverableGroupFilePathsAsync(string groupId)
    {
        var result = await ExecuteRecoverableCommandAsync(
            () => _dashboardActivation.GetGroupFilePathsAsync(groupId),
            Array.Empty<string>());
        return result.Value;
    }

    private async Task RefreshRecoveredStoreStateAsync(PersistedStateRecoveryResult recovery)
    {
        var storeFileName = Path.GetFileName(recovery.StorePath);
        if (string.Equals(storeFileName, "settings.json", StringComparison.OrdinalIgnoreCase))
        {
            await ReloadSettingsStateAsync();
            return;
        }

        if (string.Equals(storeFileName, "loggroups.json", StringComparison.OrdinalIgnoreCase))
        {
            var groups = await _groupRepo.GetAllAsync();
            _dashboardWorkspace.RebuildGroupsCollection(groups);
            await _dashboardActivation.RefreshAllMemberFilesAsync();
            _dashboardActivation.UpdateSelectedMemberFileHighlights();
            NotifyFilteredTabsChanged();
            return;
        }

        if (string.Equals(storeFileName, "logfiles.json", StringComparison.OrdinalIgnoreCase))
        {
            var knownPathsByOldId = CaptureKnownFilePathsById();
            await _tabWorkspace.RebindOpenTabsAsync();
            await RepairDashboardFileIdsAsync(knownPathsByOldId);
            await _dashboardActivation.RefreshAllMemberFilesAsync();
            _dashboardActivation.UpdateSelectedMemberFileHighlights();
            NotifyFilteredTabsChanged();
        }
    }

    private async Task ReloadSettingsStateAsync()
    {
        _settings = await _settingsRepo.LoadAsync();
        _logAppearanceService.Apply(_settings);
        await _dashboardActivation.RefreshAllMemberFilesAsync();
        foreach (var tab in Tabs)
        {
            tab.UpdateSettings(_settings);
            await tab.RefreshViewportAsync();
            if (tab.IsVisible)
                tab.OnBecameVisible();
            else
                tab.OnBecameHidden();
        }

        ViewportRefreshVersion++;

        _dashboardActivation.UpdateSelectedMemberFileHighlights();
        NotifyFilteredTabsChanged();
    }

    private Dictionary<string, string> CaptureKnownFilePathsById()
    {
        var knownPathsById = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var tab in Tabs)
        {
            if (!string.IsNullOrWhiteSpace(tab.FileId) && !string.IsNullOrWhiteSpace(tab.FilePath))
                knownPathsById[tab.FileId] = tab.FilePath;
        }

        foreach (var group in Groups)
        {
            foreach (var member in group.MemberFiles)
            {
                if (!string.IsNullOrWhiteSpace(member.FileId) && !string.IsNullOrWhiteSpace(member.FilePath))
                    knownPathsById.TryAdd(member.FileId, member.FilePath);
            }
        }

        return knownPathsById;
    }

    private async Task RepairDashboardFileIdsAsync(IReadOnlyDictionary<string, string> knownPathsByOldId)
    {
        await _dashboardWorkspace.RepairDashboardFileIdsAsync(knownPathsByOldId);
    }

    public async Task InitializeAsync()
    {
        _settings = await _settingsRepo.LoadAsync();
        _logAppearanceService.Apply(_settings);

        var groups = await _groupRepo.GetAllAsync();
        _dashboardWorkspace.RebuildGroupsCollection(groups);

        await _dashboardActivation.RefreshAllMemberFilesAsync();
        NotifyFilteredTabsChanged();
        _dashboardWorkspace.ApplyDashboardTreeFilter();
    }
}
