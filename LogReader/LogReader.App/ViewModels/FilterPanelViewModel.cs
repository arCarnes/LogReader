namespace LogReader.App.ViewModels;

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogReader.App.Services;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public enum FilterTargetMode
{
    CurrentTab,
    CurrentScope
}

public sealed class FilterWarningViewModel
{
    public FilterWarningViewModel(string filePath, string message)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        if (string.IsNullOrWhiteSpace(FileName))
            FileName = filePath;

        Message = message;
    }

    public string FilePath { get; }

    public string FileName { get; }

    public string Message { get; }
}

public partial class FilterPanelViewModel : ObservableObject, IDisposable
{
    private const string CurrentTabStaleStatusText = "Filter output is for a previous tab in this scope. Reapply filter to refresh.";
    private const string CurrentScopeStaleStatusText = "Filter output is for a previous scope membership. Reapply filter to refresh.";
    private const string CurrentTabNoParseableTimestampStatusText = "No parseable timestamps found in this file for the selected time range.";

    private readonly ISearchService _searchService;
    private readonly ILogWorkspaceContext _mainVm;
    private readonly Dictionary<WorkspaceScopeKey, ScopeOwnedFilterState> _scopeStates = new();
    private readonly Dictionary<string, LogFilterSession.FilterSnapshot> _appliedScopeSnapshots = new(StringComparer.OrdinalIgnoreCase);

    private WorkspaceScopeKey _activeScopeKey;
    private WorkspaceScopeSnapshot _activeScopeSnapshot;
    private WorkspaceScopeKey? _pendingScopeKey;
    private CancellationTokenSource? _applyFilterCts;
    private LogTabViewModel? _observedTab;
    private string _baseStatusText = string.Empty;
    private FilterExecutionState? _visibleOutputExecutionState;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private bool _isRegex;

    [ObservableProperty]
    private bool _caseSensitive;

    [ObservableProperty]
    private bool _wholeWord;

    [ObservableProperty]
    private string _fromTimestamp = string.Empty;

    [ObservableProperty]
    private string _toTimestamp = string.Empty;

    [ObservableProperty]
    private FilterTargetMode _targetMode = FilterTargetMode.CurrentTab;

    [ObservableProperty]
    private bool _isApplying;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public ObservableCollection<FilterWarningViewModel> Warnings { get; } = new();

    public bool IsCurrentTabTarget
    {
        get => TargetMode == FilterTargetMode.CurrentTab;
        set
        {
            if (value)
                TargetMode = FilterTargetMode.CurrentTab;
        }
    }

    public bool IsCurrentScopeTarget
    {
        get => TargetMode == FilterTargetMode.CurrentScope;
        set
        {
            if (value)
                TargetMode = FilterTargetMode.CurrentScope;
        }
    }

    public bool HasWarnings => Warnings.Count > 0;

    internal FilterPanelViewModel(ISearchService searchService, ILogWorkspaceContext mainVm)
    {
        _searchService = searchService;
        _mainVm = mainVm;
        Warnings.CollectionChanged += Warnings_CollectionChanged;
        _activeScopeSnapshot = _mainVm.GetActiveScopeSnapshot();
        _activeScopeKey = _activeScopeSnapshot.ScopeKey;
        RestoreScopeState(GetOrCreateScopeState(_activeScopeKey));
        OnSelectedTabChanged(_mainVm.SelectedTab);
    }

    partial void OnTargetModeChanged(FilterTargetMode value)
    {
        OnPropertyChanged(nameof(IsCurrentTabTarget));
        OnPropertyChanged(nameof(IsCurrentScopeTarget));
    }

    [RelayCommand]
    private async Task ApplyFilter()
    {
        if (string.IsNullOrWhiteSpace(Query))
        {
            SetBaseStatusText("Enter filter text.");
            return;
        }

        if (!TimestampParser.TryBuildRange(FromTimestamp, ToTimestamp, out _, out var rangeError))
        {
            SetBaseStatusText(rangeError ?? "Invalid timestamp range.");
            return;
        }

        var selectedTab = _mainVm.SelectedTab;
        if (TargetMode == FilterTargetMode.CurrentTab && selectedTab == null)
        {
            SetBaseStatusText("Select a file tab to apply a filter.");
            return;
        }

        CancelActiveApplySession();
        var previousState = CaptureCurrentScopeState();
        _activeScopeSnapshot = _mainVm.GetActiveScopeSnapshot();

        var sessionCts = new CancellationTokenSource();
        _applyFilterCts = sessionCts;
        var ct = sessionCts.Token;
        IsApplying = true;
        RefreshVisibleStatusText();

        try
        {
            if (TargetMode == FilterTargetMode.CurrentScope)
            {
                await ApplyCurrentScopeFilterAsync(
                    previousState,
                    _mainVm.ActiveScopeDashboardId,
                    _activeScopeSnapshot,
                    sessionCts,
                    ct);
            }
            else
            {
                await ApplyCurrentTabFilterAsync(
                    previousState,
                    _mainVm.ActiveScopeDashboardId,
                    selectedTab!,
                    sessionCts,
                    ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Scope exits and user cancellations keep the last committed output intact.
        }
        catch (Exception ex)
        {
            if (IsCurrentSession(sessionCts))
                SetBaseStatusText($"Filter error: {ex.Message}");
        }
        finally
        {
            if (IsCurrentSession(sessionCts))
            {
                _applyFilterCts = null;
                IsApplying = false;
                RefreshVisibleStatusText();
                sessionCts.Dispose();
            }
        }
    }

    [RelayCommand]
    private async Task ClearFilter()
    {
        CancelActiveApplySession();
        IsApplying = false;
        RefreshVisibleStatusText();

        if (TargetMode == FilterTargetMode.CurrentScope)
        {
            if (_visibleOutputExecutionState is not CurrentScopeExecutionState)
                return;

            await ClearCurrentScopeApplicationAsync(_appliedScopeSnapshots.Keys, _mainVm.ActiveScopeDashboardId);
            ClearCommittedOutputState();
            SetBaseStatusText("Filter cleared.");
            return;
        }

        var selectedTab = _mainVm.SelectedTab;
        if (selectedTab == null)
        {
            SetBaseStatusText("Select a file tab to clear filter.");
            return;
        }

        var currentTabExecutionState = _visibleOutputExecutionState as CurrentTabExecutionState;
        var selectedTabMatchesExecution = currentTabExecutionState != null &&
                                          MatchesCurrentTabExecution(selectedTab, currentTabExecutionState);

        await selectedTab.ClearFilterAsync();

        if (!selectedTabMatchesExecution)
        {
            RefreshVisibleStatusText();
            return;
        }

        _mainVm.UpdateRecentTabFilterSnapshot(currentTabExecutionState!.FilePath, _mainVm.ActiveScopeDashboardId, null);
        ClearCommittedOutputState();
        SetBaseStatusText("Filter cleared.");
    }

    internal void OnScopeChanging(WorkspaceScopeKey nextScopeKey)
    {
        if (nextScopeKey.Equals(_activeScopeKey))
            return;

        var activeState = CaptureCurrentScopeState();
        if (IsApplying)
        {
            CancelActiveApplySession();
            IsApplying = false;
        }

        _scopeStates[_activeScopeKey] = activeState;
        _pendingScopeKey = nextScopeKey;
    }

    internal void OnScopeContextChanged()
    {
        var scopeSnapshot = _mainVm.GetActiveScopeSnapshot();
        _activeScopeSnapshot = scopeSnapshot;
        if (scopeSnapshot.ScopeKey.Equals(_activeScopeKey) && _pendingScopeKey == null)
        {
            RefreshVisibleStatusText();
            return;
        }

        _activeScopeKey = scopeSnapshot.ScopeKey;
        _pendingScopeKey = null;
        RestoreScopeState(GetOrCreateScopeState(_activeScopeKey));
    }

    public void OnSelectedTabChanged(LogTabViewModel? selectedTab)
    {
        if (!ReferenceEquals(_observedTab, selectedTab))
        {
            if (_observedTab != null)
                _observedTab.PropertyChanged -= SelectedTab_PropertyChanged;

            _observedTab = selectedTab;
            if (_observedTab != null)
                _observedTab.PropertyChanged += SelectedTab_PropertyChanged;
        }

        if (_pendingScopeKey != null)
            return;

        RefreshVisibleStatusText();
    }

    internal async Task MaterializeStoredFilterStateAsync(LogTabViewModel tab, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tab);

        var scopeState = GetMaterializationState(WorkspaceScopeKey.FromDashboardId(tab.ScopeDashboardId));
        if (scopeState == null || scopeState.ExecutionState is not CurrentScopeExecutionState)
            return;

        if (!scopeState.AppliedScopeSnapshots.TryGetValue(tab.FilePath, out var snapshot))
            return;

        await tab.RestoreFilterSnapshotAsync(snapshot, ct);
    }

    public void Dispose()
    {
        PersistActiveScopeState();
        CancelActiveApplySession();
        IsApplying = false;
        if (_observedTab != null)
            _observedTab.PropertyChanged -= SelectedTab_PropertyChanged;

        Warnings.CollectionChanged -= Warnings_CollectionChanged;
    }

    private async Task ApplyCurrentTabFilterAsync(
        ScopeOwnedFilterState previousState,
        string? scopeDashboardId,
        LogTabViewModel selectedTab,
        CancellationTokenSource sessionCts,
        CancellationToken ct)
    {
        var request = CreateSearchRequest(new[] { selectedTab.FilePath });
        var result = await _searchService.SearchFileAsync(selectedTab.FilePath, request, selectedTab.EffectiveEncoding, ct);
        if (!IsCurrentSession(sessionCts) || ct.IsCancellationRequested)
            return;

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            SetBaseStatusText($"Filter error: {result.Error}");
            return;
        }

        var matchingLineNumbers = BuildMatchingLineNumbers(result);
        var statusText = BuildPerFileStatusText(request, result, matchingLineNumbers.Count);

        await ClearAppliedFilterStateAsync(previousState, scopeDashboardId);
        if (!IsCurrentSession(sessionCts) || ct.IsCancellationRequested)
            return;

        await selectedTab.ApplyFilterAsync(
            matchingLineNumbers,
            statusText,
            request,
            hasParseableTimestamps: result.HasParseableTimestamps);
        if (!IsCurrentSession(sessionCts) || ct.IsCancellationRequested)
            return;

        ClearCommittedOutputState();
        _baseStatusText = statusText;
        _visibleOutputExecutionState = new CurrentTabExecutionState(selectedTab.TabInstanceId, selectedTab.FilePath);
        RefreshVisibleStatusText();
    }

    private async Task ApplyCurrentScopeFilterAsync(
        ScopeOwnedFilterState previousState,
        string? scopeDashboardId,
        WorkspaceScopeSnapshot scopeSnapshot,
        CancellationTokenSource sessionCts,
        CancellationToken ct)
    {
        var targetPaths = scopeSnapshot.EffectiveMembership
            .Select(member => member.FilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (targetPaths.Count == 0)
        {
            await ClearAppliedFilterStateAsync(previousState, scopeDashboardId);
            if (!IsCurrentSession(sessionCts) || ct.IsCancellationRequested)
                return;

            ClearCommittedOutputState();
            _baseStatusText = "No files to filter in the current scope.";
            _visibleOutputExecutionState = CreateCurrentScopeExecutionState(scopeSnapshot);
            RefreshVisibleStatusText();
            return;
        }

        var encodings = new Dictionary<string, FileEncoding>(StringComparer.OrdinalIgnoreCase);
        foreach (var filePath in targetPaths)
        {
            encodings[filePath] = await _mainVm.ResolveFilterFileEncodingAsync(filePath, scopeDashboardId, ct);
            if (!IsCurrentSession(sessionCts) || ct.IsCancellationRequested)
                return;
        }

        var request = CreateSearchRequest(targetPaths);
        var results = await _searchService.SearchFilesAsync(request, encodings, ct);
        if (!IsCurrentSession(sessionCts) || ct.IsCancellationRequested)
            return;

        var resultsByPath = results
            .Where(result => !string.IsNullOrWhiteSpace(result.FilePath))
            .GroupBy(result => result.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

        var warnings = new List<FilterWarningState>();
        var appliedSnapshots = new Dictionary<string, LogFilterSession.FilterSnapshot>(StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in targetPaths)
        {
            if (!resultsByPath.TryGetValue(filePath, out var result))
                result = new SearchResult { FilePath = filePath };

            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                warnings.Add(new FilterWarningState(filePath, $"Filter error: {result.Error}"));
                continue;
            }

            var matchingLineNumbers = BuildMatchingLineNumbers(result);
            var statusText = BuildPerFileStatusText(request, result, matchingLineNumbers.Count);
            if (HasTimestampRange(request) && !result.HasParseableTimestamps)
                warnings.Add(new FilterWarningState(filePath, "No parseable timestamps found for the selected time range."));

            appliedSnapshots[filePath] = CreateFilterSnapshot(
                matchingLineNumbers,
                statusText,
                request,
                result.HasParseableTimestamps);
        }

        await ClearAppliedFilterStateAsync(previousState, scopeDashboardId);
        if (!IsCurrentSession(sessionCts) || ct.IsCancellationRequested)
            return;

        foreach (var (filePath, snapshot) in appliedSnapshots)
        {
            _mainVm.UpdateRecentTabFilterSnapshot(filePath, scopeDashboardId, snapshot);
            foreach (var openTab in GetOpenTabsInScope(filePath, scopeDashboardId))
                await openTab.RestoreFilterSnapshotAsync(snapshot, ct);
        }

        if (!IsCurrentSession(sessionCts) || ct.IsCancellationRequested)
            return;

        ClearCommittedOutputState();
        foreach (var (filePath, snapshot) in appliedSnapshots)
            _appliedScopeSnapshots[filePath] = LogFilterSession.CloneSnapshot(snapshot);

        RestoreWarnings(warnings);
        _baseStatusText = BuildScopeSummary(appliedSnapshots.Count, appliedSnapshots.Values.Sum(snapshot => snapshot.MatchingLineNumbers.Count), warnings.Count);
        _visibleOutputExecutionState = CreateCurrentScopeExecutionState(scopeSnapshot);
        RefreshVisibleStatusText();
    }

    private async Task ClearAppliedFilterStateAsync(ScopeOwnedFilterState state, string? scopeDashboardId)
    {
        switch (state.ExecutionState)
        {
            case CurrentTabExecutionState currentTabExecutionState:
                await ClearCurrentTabApplicationAsync(currentTabExecutionState.FilePath, scopeDashboardId);
                break;

            case CurrentScopeExecutionState:
                await ClearCurrentScopeApplicationAsync(state.AppliedScopeSnapshots.Keys, scopeDashboardId);
                break;
        }
    }

    private async Task ClearCurrentTabApplicationAsync(string filePath, string? scopeDashboardId)
    {
        foreach (var tab in GetOpenTabsInScope(filePath, scopeDashboardId))
        {
            if (tab.IsFilterActive)
                await tab.ClearFilterAsync();
        }

        _mainVm.UpdateRecentTabFilterSnapshot(filePath, scopeDashboardId, null);
    }

    private async Task ClearCurrentScopeApplicationAsync(IEnumerable<string> filePaths, string? scopeDashboardId)
    {
        var normalizedPaths = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (normalizedPaths.Count == 0)
            return;

        foreach (var path in normalizedPaths)
            _mainVm.UpdateRecentTabFilterSnapshot(path, scopeDashboardId, null);

        foreach (var tab in _mainVm.GetAllTabs().Where(tab =>
                     string.Equals(tab.ScopeDashboardId, scopeDashboardId, StringComparison.Ordinal) &&
                     normalizedPaths.Contains(tab.FilePath)))
        {
            if (tab.IsFilterActive)
                await tab.ClearFilterAsync();
        }
    }

    private IEnumerable<LogTabViewModel> GetOpenTabsInScope(string filePath, string? scopeDashboardId)
    {
        return _mainVm.GetAllTabs().Where(tab =>
            string.Equals(tab.ScopeDashboardId, scopeDashboardId, StringComparison.Ordinal) &&
            string.Equals(tab.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
    }

    private SearchRequest CreateSearchRequest(IReadOnlyList<string> filePaths)
    {
        return new SearchRequest
        {
            Query = Query,
            IsRegex = IsRegex,
            CaseSensitive = CaseSensitive,
            WholeWord = WholeWord,
            FilePaths = filePaths.ToList(),
            FromTimestamp = string.IsNullOrWhiteSpace(FromTimestamp) ? null : FromTimestamp.Trim(),
            ToTimestamp = string.IsNullOrWhiteSpace(ToTimestamp) ? null : ToTimestamp.Trim()
        };
    }

    private static List<int> BuildMatchingLineNumbers(SearchResult result)
    {
        return result.Hits
            .Select(hit => (int)hit.LineNumber)
            .Distinct()
            .OrderBy(line => line)
            .ToList();
    }

    private static string BuildPerFileStatusText(SearchRequest request, SearchResult result, int matchingLineCount)
    {
        if (HasTimestampRange(request) && !result.HasParseableTimestamps)
            return CurrentTabNoParseableTimestampStatusText;

        return $"Filter active: {matchingLineCount:N0} matching lines.";
    }

    private static string BuildScopeSummary(int appliedFileCount, int totalMatches, int warningCount)
    {
        if (appliedFileCount == 0)
        {
            return warningCount > 0
                ? $"Filter completed with {warningCount:N0} warning(s). No files were filtered."
                : "No files were filtered.";
        }

        var summary = $"Filter active across {appliedFileCount:N0} file(s): {totalMatches:N0} matching lines.";
        if (warningCount > 0)
            summary += $" {warningCount:N0} warning(s).";

        return summary;
    }

    private static bool HasTimestampRange(SearchRequest request)
        => request.FromTimestamp != null || request.ToTimestamp != null;

    private static LogFilterSession.FilterSnapshot CreateFilterSnapshot(
        IReadOnlyList<int> matchingLineNumbers,
        string statusText,
        SearchRequest request,
        bool hasParseableTimestamps)
    {
        return new LogFilterSession.FilterSnapshot
        {
            MatchingLineNumbers = matchingLineNumbers.ToList(),
            StatusText = statusText,
            FilterRequest = CloneSearchRequest(request),
            HasSeenParseableTimestamp = hasParseableTimestamps
        };
    }

    private ScopeOwnedFilterState GetOrCreateScopeState(WorkspaceScopeKey scopeKey)
    {
        if (_scopeStates.TryGetValue(scopeKey, out var existingState))
            return CloneScopeState(existingState);

        return new ScopeOwnedFilterState();
    }

    private ScopeOwnedFilterState? GetMaterializationState(WorkspaceScopeKey scopeKey)
    {
        if (_pendingScopeKey != null && scopeKey.Equals(_activeScopeKey))
            return null;

        if (scopeKey.Equals(_activeScopeKey))
            return CaptureCurrentScopeState();

        if (_scopeStates.TryGetValue(scopeKey, out var state))
            return CloneScopeState(state);

        return null;
    }

    private void PersistActiveScopeState()
    {
        _scopeStates[_activeScopeKey] = CaptureCurrentScopeState();
    }

    private ScopeOwnedFilterState CaptureCurrentScopeState()
    {
        return new ScopeOwnedFilterState
        {
            Query = Query,
            IsRegex = IsRegex,
            CaseSensitive = CaseSensitive,
            WholeWord = WholeWord,
            FromTimestamp = FromTimestamp,
            ToTimestamp = ToTimestamp,
            TargetMode = TargetMode,
            BaseStatusText = _baseStatusText,
            ExecutionState = CloneExecutionState(_visibleOutputExecutionState),
            Warnings = Warnings
                .Select(warning => new FilterWarningState(warning.FilePath, warning.Message))
                .ToList(),
            AppliedScopeSnapshots = _appliedScopeSnapshots.ToDictionary(
                entry => entry.Key,
                entry => LogFilterSession.CloneSnapshot(entry.Value),
                StringComparer.OrdinalIgnoreCase)
        };
    }

    private void RestoreScopeState(ScopeOwnedFilterState state)
    {
        Query = state.Query;
        IsRegex = state.IsRegex;
        CaseSensitive = state.CaseSensitive;
        WholeWord = state.WholeWord;
        FromTimestamp = state.FromTimestamp;
        ToTimestamp = state.ToTimestamp;
        TargetMode = state.TargetMode;

        ClearCommittedOutputState();
        _baseStatusText = state.BaseStatusText;
        _visibleOutputExecutionState = CloneExecutionState(state.ExecutionState);
        foreach (var (filePath, snapshot) in state.AppliedScopeSnapshots)
            _appliedScopeSnapshots[filePath] = LogFilterSession.CloneSnapshot(snapshot);

        RestoreWarnings(state.Warnings);
        IsApplying = false;
        RefreshVisibleStatusText();
    }

    private void ClearCommittedOutputState()
    {
        _baseStatusText = string.Empty;
        _visibleOutputExecutionState = null;
        _appliedScopeSnapshots.Clear();
        RestoreWarnings(Array.Empty<FilterWarningState>());
    }

    private void RestoreWarnings(IEnumerable<FilterWarningState> warnings)
    {
        Warnings.Clear();
        foreach (var warning in warnings)
            Warnings.Add(new FilterWarningViewModel(warning.FilePath, warning.Message));
    }

    private void SetBaseStatusText(string statusText)
    {
        _baseStatusText = statusText;
        RefreshVisibleStatusText();
    }

    private void RefreshVisibleStatusText()
    {
        StatusText = GetVisibleStatusText();
    }

    private string GetVisibleStatusText()
    {
        if (IsApplying)
            return TargetMode == FilterTargetMode.CurrentScope
                ? "Applying filter to current scope..."
                : "Applying filter to current tab...";

        if (_visibleOutputExecutionState is CurrentTabExecutionState currentTabExecutionState &&
            !MatchesCurrentTabExecution(currentTabExecutionState))
        {
            return CurrentTabStaleStatusText;
        }

        if (_visibleOutputExecutionState is CurrentScopeExecutionState currentScopeExecutionState &&
            !MatchesCurrentScopeExecution(currentScopeExecutionState))
        {
            return CurrentScopeStaleStatusText;
        }

        if (_visibleOutputExecutionState is CurrentTabExecutionState visibleCurrentTabExecution &&
            _mainVm.SelectedTab != null &&
            MatchesCurrentTabExecution(_mainVm.SelectedTab, visibleCurrentTabExecution) &&
            _mainVm.SelectedTab.IsFilterActive)
        {
            return _mainVm.SelectedTab.StatusText;
        }

        return _baseStatusText;
    }

    private bool MatchesCurrentTabExecution(CurrentTabExecutionState executionState)
        => _mainVm.SelectedTab != null && MatchesCurrentTabExecution(_mainVm.SelectedTab, executionState);

    private static bool MatchesCurrentTabExecution(LogTabViewModel tab, CurrentTabExecutionState executionState)
    {
        return string.Equals(tab.TabInstanceId, executionState.TabInstanceId, StringComparison.Ordinal) &&
               string.Equals(tab.FilePath, executionState.FilePath, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesCurrentScopeExecution(CurrentScopeExecutionState executionState)
    {
        var currentMembership = NormalizeMembershipPaths(_activeScopeSnapshot.EffectiveMembership);
        if (currentMembership.Count != executionState.EffectiveMembershipPaths.Count)
            return false;

        for (var i = 0; i < currentMembership.Count; i++)
        {
            if (!string.Equals(currentMembership[i], executionState.EffectiveMembershipPaths[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static CurrentScopeExecutionState CreateCurrentScopeExecutionState(WorkspaceScopeSnapshot scopeSnapshot)
        => new(NormalizeMembershipPaths(scopeSnapshot.EffectiveMembership));

    private static IReadOnlyList<string> NormalizeMembershipPaths(IReadOnlyList<WorkspaceScopeMemberSnapshot> membership)
    {
        return membership
            .Select(member => member.FilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(NormalizeFilePath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeFilePath(string filePath)
        => Path.GetFullPath(filePath);

    private void SelectedTab_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not LogTabViewModel)
            return;

        if (e.PropertyName is nameof(LogTabViewModel.StatusText) or nameof(LogTabViewModel.IsFilterActive))
            RefreshVisibleStatusText();
    }

    private bool IsCurrentSession(CancellationTokenSource sessionCts)
        => ReferenceEquals(_applyFilterCts, sessionCts);

    private void CancelActiveApplySession()
    {
        var current = _applyFilterCts;
        _applyFilterCts = null;
        if (current == null)
            return;

        current.Cancel();
        current.Dispose();
    }

    private void Warnings_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => OnPropertyChanged(nameof(HasWarnings));

    private static SearchRequest CloneSearchRequest(SearchRequest request)
    {
        return new SearchRequest
        {
            Query = request.Query,
            IsRegex = request.IsRegex,
            CaseSensitive = request.CaseSensitive,
            WholeWord = request.WholeWord,
            FilePaths = request.FilePaths.ToList(),
            StartLineNumber = request.StartLineNumber,
            EndLineNumber = request.EndLineNumber,
            FromTimestamp = request.FromTimestamp,
            ToTimestamp = request.ToTimestamp
        };
    }

    private static ScopeOwnedFilterState CloneScopeState(ScopeOwnedFilterState state)
    {
        return new ScopeOwnedFilterState
        {
            Query = state.Query,
            IsRegex = state.IsRegex,
            CaseSensitive = state.CaseSensitive,
            WholeWord = state.WholeWord,
            FromTimestamp = state.FromTimestamp,
            ToTimestamp = state.ToTimestamp,
            TargetMode = state.TargetMode,
            BaseStatusText = state.BaseStatusText,
            ExecutionState = CloneExecutionState(state.ExecutionState),
            Warnings = state.Warnings
                .Select(warning => new FilterWarningState(warning.FilePath, warning.Message))
                .ToList(),
            AppliedScopeSnapshots = state.AppliedScopeSnapshots.ToDictionary(
                entry => entry.Key,
                entry => LogFilterSession.CloneSnapshot(entry.Value),
                StringComparer.OrdinalIgnoreCase)
        };
    }

    private static FilterExecutionState? CloneExecutionState(FilterExecutionState? executionState)
    {
        return executionState switch
        {
            CurrentTabExecutionState currentTab => new CurrentTabExecutionState(currentTab.TabInstanceId, currentTab.FilePath),
            CurrentScopeExecutionState currentScope => new CurrentScopeExecutionState(currentScope.EffectiveMembershipPaths.ToList()),
            _ => null
        };
    }

    private abstract record FilterExecutionState;

    private sealed record CurrentTabExecutionState(string TabInstanceId, string FilePath) : FilterExecutionState;

    private sealed record CurrentScopeExecutionState(IReadOnlyList<string> EffectiveMembershipPaths) : FilterExecutionState;

    private sealed record FilterWarningState(string FilePath, string Message);

    private sealed class ScopeOwnedFilterState
    {
        public string Query { get; init; } = string.Empty;

        public bool IsRegex { get; init; }

        public bool CaseSensitive { get; init; }

        public bool WholeWord { get; init; }

        public string FromTimestamp { get; init; } = string.Empty;

        public string ToTimestamp { get; init; } = string.Empty;

        public FilterTargetMode TargetMode { get; init; } = FilterTargetMode.CurrentTab;

        public string BaseStatusText { get; init; } = string.Empty;

        public FilterExecutionState? ExecutionState { get; init; }

        public List<FilterWarningState> Warnings { get; init; } = new();

        public Dictionary<string, LogFilterSession.FilterSnapshot> AppliedScopeSnapshots { get; init; }
            = new(StringComparer.OrdinalIgnoreCase);
    }
}
