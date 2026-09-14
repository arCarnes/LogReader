namespace LogReader.App.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LogReader.Core;
using LogReader.Core.Models;

public partial class SearchPanelViewModel
{
    private List<StructuredFieldProfile> _fieldProfiles = new();
    private string _inactiveQuery = string.Empty;
    private bool _inactiveCaseSensitive;
    private WqlQueryPlan? _visibleWqlPlan;
    private string _wqlCoverageText = string.Empty;
    private bool _restoringWqlState;
    [ObservableProperty] private bool _isWql;
    [ObservableProperty] private string? _selectedFieldProfileId;
    [ObservableProperty] private string _wqlStatusText = string.Empty;

    public ObservableCollection<FieldProfileOption> FieldProfileOptions { get; } = new() { new(null, "Built-in fields only") };
    public bool IsPlainSearch => !IsWql;
    public bool AreTailControlsEnabled => !IsWql && AreTargetAndSourceToggleEnabled;

    public void UpdateFieldProfiles(IEnumerable<StructuredFieldProfile>? profiles)
    {
        _fieldProfiles = (profiles ?? []).Select(profile => profile.Copy()).ToList();
        RefreshFieldProfileOptions();
        RefreshWqlOutputStatus();
    }

    private void RefreshFieldProfileOptions()
    {
        var selected = SelectedFieldProfileId;
        FieldProfileOptions.Clear();
        FieldProfileOptions.Add(new(null, "Built-in fields only"));
        foreach (var profile in _fieldProfiles) FieldProfileOptions.Add(new(profile.Id, profile.Name));
        if (selected != null && !_fieldProfiles.Any(profile => profile.Id == selected))
            FieldProfileOptions.Add(new(selected, "Unavailable profile — choose another"));
        SelectedFieldProfileId = selected;
    }

    partial void OnIsWqlChanged(bool value)
    {
        if (_restoringWqlState) return;
        CancelActiveSearchSession(updateUi: false);
        IsSearching = false;
        (Query, _inactiveQuery) = (_inactiveQuery, Query);
        (CaseSensitive, _inactiveCaseSensitive) = (_inactiveCaseSensitive, CaseSensitive);
        OnPropertyChanged(nameof(IsPlainSearch));
        OnPropertyChanged(nameof(SearchDataMode));
        OnPropertyChanged(nameof(IsDiskSnapshotMode));
        OnPropertyChanged(nameof(IsTailMode));
        OnPropertyChanged(nameof(AreTailControlsEnabled));
        RefreshFilterApplicabilityState();
        RaiseMonitoringStateChanged();
        ApplyVisibleOutputInvalidationIfNeeded();
        RefreshWqlOutputStatus();
    }

    partial void OnSelectedFieldProfileIdChanged(string? value) => RefreshWqlOutputStatus();

    private WqlQueryPlan? CompileWqlInput()
    {
        if (!IsWql) return null;
        var profile = _fieldProfiles.FirstOrDefault(profile => profile.Id == SelectedFieldProfileId);
        if (SelectedFieldProfileId != null && profile == null)
            throw new ArgumentException("The selected field profile is unavailable. Choose another profile.");
        return WqlCompiler.Compile(Query, profile, CaseSensitive);
    }

    private void UpdateWqlOutput(WqlQueryPlan plan, IReadOnlyList<SearchResult> results)
    {
        _visibleWqlPlan = plan;
        var evaluated = results.Sum(result => result.WqlEvaluatedLineCount);
        var complete = results.All(result => result.IsEvaluationComplete && result.Error == null && !result.WasCancelled &&
            !result.FileChangedDuringOrAfterScan && result.GenerationEvidence.Correlation == FileGenerationCorrelation.Current);
        var fields = plan.Extractor.Fields.Keys.OrderBy(name => name).Select(name =>
        {
            var stats = results.Select(result => result.FieldStatistics?.GetValueOrDefault(name)).OfType<StructuredFieldStatistics>().ToList();
            return $"{name}: {stats.Sum(stat => stat.ValueCount):N0} values, {stats.Sum(stat => stat.MissingCount):N0} missing, {stats.Sum(stat => stat.InvalidCount):N0} invalid";
        });
        var outputTruncated = results.Any(result => result.HitLimitExceeded || result.Hits.Any(hit =>
            hit.LineTextTruncated || hit.Fields?.Values.Any(value => value.IsTruncated) == true));
        _wqlCoverageText = $"WQL snapshot {(complete ? "complete" : "incomplete or changed")}; {evaluated:N0} eligible lines evaluated." +
            (outputTruncated ? " Output truncated." : string.Empty) + Environment.NewLine + string.Join(Environment.NewLine, fields);
        RefreshWqlOutputStatus();
    }

    private void RefreshWqlOutputStatus()
    {
        var stale = false;
        if (_visibleWqlPlan != null)
        {
            try
            {
                var profile = _fieldProfiles.FirstOrDefault(profile => profile.Id == _visibleWqlPlan.Extractor.ProfileId);
                stale = !IsWql || SelectedFieldProfileId != _visibleWqlPlan.Extractor.ProfileId ||
                    (_visibleWqlPlan.Extractor.ProfileId != null && profile == null) ||
                    StructuredFieldExtractor.Compile(profile).Revision != _visibleWqlPlan.Extractor.Revision;
            }
            catch (ArgumentException) { stale = true; }
        }
        WqlStatusText = (stale ? "These results use a previous profile or query mode. Rerun to refresh." + Environment.NewLine : string.Empty) + _wqlCoverageText;
    }

    private WqlUiState CaptureWqlState() => new(IsWql, SelectedFieldProfileId, _inactiveQuery, _inactiveCaseSensitive, _visibleWqlPlan, _wqlCoverageText);

    private void RestoreWqlState(WqlUiState state)
    {
        SetWqlModeForRestore(state.IsWql);
        SelectedFieldProfileId = state.ProfileId;
        _inactiveQuery = state.InactiveQuery;
        _inactiveCaseSensitive = state.InactiveCaseSensitive;
        _visibleWqlPlan = state.OutputPlan;
        _wqlCoverageText = state.Coverage;
        OnPropertyChanged(nameof(IsWql));
        OnPropertyChanged(nameof(IsPlainSearch));
        OnPropertyChanged(nameof(SelectedFieldProfileId));
        OnPropertyChanged(nameof(AreTailControlsEnabled));
        RefreshFieldProfileOptions();
        RefreshWqlOutputStatus();
    }

    private void SetWqlModeForRestore(bool value)
    {
        _restoringWqlState = true;
        try { IsWql = value; }
        finally { _restoringWqlState = false; }
    }

    private sealed record WqlUiState(bool IsWql = false, string? ProfileId = null, string InactiveQuery = "",
        bool InactiveCaseSensitive = false, WqlQueryPlan? OutputPlan = null, string Coverage = "");
}

public sealed record FieldProfileOption(string? Id, string Name);
