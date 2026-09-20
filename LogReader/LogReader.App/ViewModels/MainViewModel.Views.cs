namespace LogReader.App.ViewModels;

using System.Collections.ObjectModel;
using LogReader.App.Services;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public sealed record ViewSourceLabel(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record ViewChoice(ViewIdentity Identity, string Source, string Name)
{
    public string DisplayName => Identity.IsLocal ? Name : $"{Name} (read-only)";
    public string ManagementName => $"{Source} / {DisplayName}";
    public ViewSourceLabel SourceGroup => new(Identity.SourceId, Source);
}

public partial class MainViewModel
{
    internal ViewLibraryService? ViewLibrary { get; private set; }
    public ObservableCollection<ViewChoice> ViewChoices { get; } = new();
    private ViewChoice? _activeViewChoice;
    public ViewChoice? ActiveViewChoice { get => _activeViewChoice; private set => SetProperty(ref _activeViewChoice, value); }
    private bool _isViewOperationRunning;
    public bool IsViewOperationRunning
    {
        get => _isViewOperationRunning;
        private set
        {
            if (!SetProperty(ref _isViewOperationRunning, value)) return;
            OnPropertyChanged(nameof(AreLoadAffectingActionsEnabled));
            OnPropertyChanged(nameof(IsLoadAffectingActionFrozen));
            OnPropertyChanged(nameof(CanEditCurrentView));
            SearchPanel.RefreshLoadFreezeState();
            FilterPanel.RefreshLoadFreezeState();
        }
    }
    public bool CanEditCurrentView => AreLoadAffectingActionsEnabled && ViewLibrary?.IsReadOnly != true && ViewLibrary?.NeedsRecovery != true;

    internal void EnableViewLibrary(IViewLibraryRepository store) => ViewLibrary = _dashboardWorkspace.CreateViewLibrary(store);

    internal async Task RefreshViewChoicesAsync()
    {
        if (ViewLibrary == null) return;
        var choices = await ViewLibrary.ListAsync();
        ViewChoices.Clear();
        foreach (var (identity, source, name) in choices) ViewChoices.Add(new(identity, source, name));
        ActiveViewChoice = ViewChoices.Single(v => v.Identity == ViewLibrary.Library!.Active);
        OnPropertyChanged(nameof(ActiveViewChoice));
        OnPropertyChanged(nameof(CanEditCurrentView));
    }

    internal async Task RunViewLibraryActionAsync(Func<ViewLibraryService, Task> action, bool activates = true)
    {
        if (ViewLibrary == null || IsLoadAffectingActionFrozen) return;
        IsViewOperationRunning = true;
        var previousScopes = CaptureDashboardScopeIds();
        try
        {
            await action(ViewLibrary);
            if (activates)
            {
                _dashboardActivation.LeaveActiveDashboardScope();
                _dashboardWorkspace.RebuildGroupsCollection(await _groupRepo.GetAllAsync());
                ClearDashboardMemberBatchSelection();
                await FlushPreviousDashboardScopesAfterImportAsync(previousScopes);
                await _dashboardActivation.RefreshAllMemberFilesAsync();
                NotifyFilteredTabsChanged();
            }
        }
        finally
        {
            try { await RefreshViewChoicesAsync(); }
            finally { IsViewOperationRunning = false; }
        }
    }
}
