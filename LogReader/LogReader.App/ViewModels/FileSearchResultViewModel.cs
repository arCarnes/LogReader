namespace LogReader.App.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogReader.App.Services;
using LogReader.Core.Models;

internal sealed record FileSearchResultState(
    string FilePath,
    IReadOnlyList<SearchHit> Hits,
    string? Error,
    bool IsExpanded);

public partial class FileSearchResultViewModel : ObservableObject
{
    private readonly ILogWorkspaceContext _mainVm;
    private readonly Action? _stateChanged;
    private readonly List<SearchHitEntry> _orderedHits = new();
    private readonly Dictionary<string, SearchHitEntry> _hitEntriesByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SearchResultHitRowViewModel> _hitRowsByKey = new(StringComparer.Ordinal);
    private BulkObservableCollection<SearchHitViewModel>? _materializedHits;
    private bool _isInitializing;

    public string FilePath { get; }
    public string FileName => System.IO.Path.GetFileName(FilePath);
    public ObservableCollection<SearchHitViewModel> Hits => GetOrCreateMaterializedHits();
    internal SearchResultFileHeaderRowViewModel HeaderRow { get; }
    internal bool HasMaterializedHits => _materializedHits != null;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private int _hitCount;

    [ObservableProperty]
    private string? _error;

    internal FileSearchResultViewModel(
        SearchResult result,
        ILogWorkspaceContext mainVm,
        Action? stateChanged = null)
    {
        _mainVm = mainVm;
        _stateChanged = stateChanged;
        FilePath = result.FilePath;
        Error = result.Error;
        HeaderRow = new SearchResultFileHeaderRowViewModel(this);
        _isInitializing = true;
        AddHits(result.Hits);
        _isInitializing = false;
    }

    public bool AddHits(IEnumerable<SearchHit> hits)
    {
        var changedAny = false;
        var addedEntries = new List<SearchHitEntry>();
        var lastExistingHit = _orderedHits.Count > 0 ? _orderedHits[^1].Hit : null;

        foreach (var hit in hits)
        {
            var dedupeKey = BuildHitKey(hit);
            var clonedHit = CloneHit(hit);
            if (_hitEntriesByKey.TryGetValue(dedupeKey, out var existingEntry))
            {
                if (MergeMatches(existingEntry.Hit, clonedHit))
                {
                    _hitRowsByKey.Remove(dedupeKey);
                    changedAny = true;
                }

                continue;
            }

            var entry = new SearchHitEntry(dedupeKey, clonedHit);
            _hitEntriesByKey[dedupeKey] = entry;
            _orderedHits.Add(entry);
            addedEntries.Add(entry);
            changedAny = true;
        }

        if (!changedAny)
            return false;

        PublishHits(lastExistingHit, addedEntries);
        return true;
    }

    public void SetError(string? error)
    {
        Error = string.IsNullOrWhiteSpace(error) ? null : error;
    }

    internal FileSearchResultState CaptureState()
    {
        return new FileSearchResultState(
            FilePath,
            _orderedHits.Select(entry => CloneHit(entry.Hit)).ToList(),
            Error,
            IsExpanded);
    }

    [RelayCommand]
    private async Task NavigateToHit(SearchHitViewModel? hit)
    {
        if (hit == null || _mainVm.IsDashboardLoading)
            return;

        await _mainVm.RunViewActionAsync(
            () => _mainVm.NavigateToLineAsync(
                FilePath,
                hit.LineNumber,
                disableAutoScroll: true,
                suppressDuringDashboardLoad: true),
            "Search Result Navigation Failed");
    }

    private void PublishHits(SearchHit? lastExistingHit, IReadOnlyList<SearchHitEntry> addedEntries)
    {
        if (!CanAppendWithoutReordering(lastExistingHit, addedEntries))
        {
            _orderedHits.Sort(static (left, right) => CompareHits(left.Hit, right.Hit));
            PruneStaleHitRows();
        }

        if (_materializedHits != null)
            _materializedHits.ReplaceAll(_orderedHits.Select(entry => new SearchHitViewModel(CloneHit(entry.Hit))));

        HitCount = _orderedHits.Count;
        if (!_isInitializing)
            _stateChanged?.Invoke();
    }

    internal SearchResultHitRowViewModel GetHitRow(int hitIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(hitIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(hitIndex, _orderedHits.Count);

        var hitEntry = _orderedHits[hitIndex];
        if (_hitRowsByKey.TryGetValue(hitEntry.Key, out var existingRow))
        {
            existingRow.UpdateHitIndex(hitIndex);
            return existingRow;
        }

        var row = new SearchResultHitRowViewModel(this, hitIndex, new SearchHitViewModel(CloneHit(hitEntry.Hit)));
        _hitRowsByKey[hitEntry.Key] = row;
        return row;
    }

    partial void OnIsExpandedChanged(bool value)
    {
        _stateChanged?.Invoke();
    }

    private BulkObservableCollection<SearchHitViewModel> GetOrCreateMaterializedHits()
    {
        if (_materializedHits != null)
            return _materializedHits;

        _materializedHits = new BulkObservableCollection<SearchHitViewModel>();
        _materializedHits.ReplaceAll(_orderedHits.Select(entry => new SearchHitViewModel(CloneHit(entry.Hit))));
        return _materializedHits;
    }

    private void PruneStaleHitRows()
    {
        var activeKeys = _orderedHits
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var staleKey in _hitRowsByKey.Keys.Where(key => !activeKeys.Contains(key)).ToList())
            _hitRowsByKey.Remove(staleKey);
    }

    private static string BuildHitKey(SearchHit hit)
        => $"{hit.LineNumber}:{hit.LineText}";

    private static int CompareHits(SearchHit left, SearchHit right)
    {
        var lineComparison = left.LineNumber.CompareTo(right.LineNumber);
        if (lineComparison != 0)
            return lineComparison;

        return string.Compare(left.LineText, right.LineText, StringComparison.Ordinal);
    }

    private static bool CanAppendWithoutReordering(SearchHit? lastExistingHit, IReadOnlyList<SearchHitEntry> addedEntries)
    {
        if (addedEntries.Count == 0)
            return true;

        if (lastExistingHit != null && CompareHits(lastExistingHit, addedEntries[0].Hit) > 0)
            return false;

        for (var i = 1; i < addedEntries.Count; i++)
        {
            if (CompareHits(addedEntries[i - 1].Hit, addedEntries[i].Hit) > 0)
                return false;
        }

        return true;
    }

    private static SearchHit CloneHit(SearchHit hit)
    {
        return new SearchHit
        {
            LineNumber = hit.LineNumber,
            LineText = hit.LineText,
            MatchStart = hit.MatchStart,
            MatchLength = hit.MatchLength,
            OriginalMatchStart = hit.OriginalMatchStart,
            OriginalMatchLength = hit.OriginalMatchLength,
            Matches = CloneMatches(hit)
        };
    }

    private static List<SearchMatchSpan> CloneMatches(SearchHit hit)
        => hit.Matches.Count > 0
            ? hit.Matches.Select(CloneMatch).ToList()
            : new List<SearchMatchSpan>
            {
                new()
                {
                    MatchStart = hit.MatchStart,
                    MatchLength = hit.MatchLength,
                    OriginalMatchStart = hit.OriginalMatchStart,
                    OriginalMatchLength = hit.OriginalMatchLength
                }
            };

    private static SearchMatchSpan CloneMatch(SearchMatchSpan match)
        => new()
        {
            MatchStart = match.MatchStart,
            MatchLength = match.MatchLength,
            OriginalMatchStart = match.OriginalMatchStart,
            OriginalMatchLength = match.OriginalMatchLength
        };

    private static bool MergeMatches(SearchHit target, SearchHit source)
    {
        var seen = target.Matches
            .Select(BuildMatchKey)
            .ToHashSet(StringComparer.Ordinal);
        var changed = false;
        foreach (var match in CloneMatches(source))
        {
            if (!seen.Add(BuildMatchKey(match)))
                continue;

            target.Matches.Add(match);
            changed = true;
        }

        if (!changed)
            return false;

        target.Matches.Sort(static (left, right) =>
        {
            var startComparison = left.MatchStart.CompareTo(right.MatchStart);
            if (startComparison != 0)
                return startComparison;

            var originalStartComparison = (left.OriginalMatchStart ?? left.MatchStart)
                .CompareTo(right.OriginalMatchStart ?? right.MatchStart);
            if (originalStartComparison != 0)
                return originalStartComparison;

            return left.MatchLength.CompareTo(right.MatchLength);
        });
        var firstMatch = target.Matches[0];
        target.MatchStart = firstMatch.MatchStart;
        target.MatchLength = firstMatch.MatchLength;
        target.OriginalMatchStart = firstMatch.OriginalMatchStart;
        target.OriginalMatchLength = firstMatch.OriginalMatchLength;
        return true;
    }

    private static string BuildMatchKey(SearchMatchSpan match)
        => $"{match.MatchStart}:{match.MatchLength}:{match.OriginalMatchStart}:{match.OriginalMatchLength}";

    private sealed record SearchHitEntry(string Key, SearchHit Hit);
}
