namespace LogReader.App.Services;

using LogReader.Core.Models;

internal sealed partial class DashboardTreeService
{
    private int _expansionNotificationDepth;
    private Dictionary<string, bool> _lastExpansionSnapshot = new(StringComparer.Ordinal);

    internal event Action? ExpansionStateChanged;

    internal UiState CaptureExpansionState()
        => new()
        {
            GroupExpansionById = _host.Groups.ToDictionary(group => group.Id,
                group => _filterExpansionStateById != null && _filterExpansionStateById.TryGetValue(group.Id, out var baseline)
                    ? baseline : group.IsExpanded,
                StringComparer.Ordinal)
        };

    internal void RestoreExpansionState(UiState state)
    {
        _expansionNotificationDepth++;
        try
        {
            foreach (var group in _host.Groups)
                if (state.GroupExpansionById.TryGetValue(group.Id, out var expanded))
                {
                    group.IsExpanded = expanded;
                    if (_filterExpansionStateById != null)
                        _filterExpansionStateById[group.Id] = expanded;
                }
            _lastExpansionSnapshot = CaptureExpansionState().GroupExpansionById;
        }
        finally { _expansionNotificationDepth--; }
    }

    private void NotifyExpansionChanged()
    {
        if (_expansionNotificationDepth != 0)
            return;
        var snapshot = CaptureExpansionState().GroupExpansionById;
        if (snapshot.Count == _lastExpansionSnapshot.Count && snapshot.All(pair =>
                _lastExpansionSnapshot.TryGetValue(pair.Key, out var value) && value == pair.Value))
            return;
        _lastExpansionSnapshot = snapshot;
        ExpansionStateChanged?.Invoke();
    }

    private void UpdateExpansion(Action action)
    {
        _expansionNotificationDepth++;
        try { action(); }
        finally { _expansionNotificationDepth--; }
        NotifyExpansionChanged();
    }

    private void ApplyCommittedGroups(List<LogGroup> groups)
        => UpdateExpansion(() => ApplyCommittedGroupsCore(groups));

    public void RebuildGroupsCollection(List<LogGroup> groups)
        => UpdateExpansion(() => RebuildGroupsCollectionCore(groups));

    public void ApplyDashboardTreeFilter()
        => UpdateExpansion(ApplyDashboardTreeFilterCore);
}
