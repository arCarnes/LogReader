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
    private readonly HashSet<string> _seenHitKeys = new(StringComparer.Ordinal);
    private readonly List<SearchHit> _orderedHits = new();
    private readonly Dictionary<int, SearchResultHitRowViewModel> _hitRowsByIndex = new();
    private BulkObservableCollection<SearchHitViewModel>? _materializedHits;

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
        AddHits(result.Hits);
    }

    public void AddHits(IEnumerable<SearchHit> hits)
    {
        var addedAny = false;

        foreach (var hit in hits)
        {
            var dedupeKey = $"{hit.LineNumber}:{hit.MatchStart}:{hit.MatchLength}:{hit.LineText}";
            if (!_seenHitKeys.Add(dedupeKey))
                continue;

            _orderedHits.Add(new SearchHit
            {
                LineNumber = hit.LineNumber,
                LineText = hit.LineText,
                MatchStart = hit.MatchStart,
                MatchLength = hit.MatchLength
            });
            addedAny = true;
        }

        if (!addedAny)
            return;

        PublishHits();
    }

    public void SetError(string? error)
    {
        Error = string.IsNullOrWhiteSpace(error) ? null : error;
    }

    internal FileSearchResultState CaptureState()
    {
        return new FileSearchResultState(
            FilePath,
            _orderedHits.Select(CloneHit).ToList(),
            Error,
            IsExpanded);
    }

    [RelayCommand]
    private async Task NavigateToHit(SearchHitViewModel? hit)
    {
        if (hit == null) return;
        await _mainVm.NavigateToLineAsync(FilePath, hit.LineNumber, disableAutoScroll: true);
    }

    private void PublishHits()
    {
        _orderedHits.Sort(CompareHits);
        _hitRowsByIndex.Clear();
        if (_materializedHits != null)
            _materializedHits.ReplaceAll(_orderedHits.Select(hit => new SearchHitViewModel(CloneHit(hit))));

        HitCount = _orderedHits.Count;
        _stateChanged?.Invoke();
    }

    internal SearchResultHitRowViewModel GetHitRow(int hitIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(hitIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(hitIndex, _orderedHits.Count);

        if (_hitRowsByIndex.TryGetValue(hitIndex, out var existingRow))
            return existingRow;

        var row = new SearchResultHitRowViewModel(this, hitIndex, new SearchHitViewModel(CloneHit(_orderedHits[hitIndex])));
        _hitRowsByIndex[hitIndex] = row;
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
        _materializedHits.ReplaceAll(_orderedHits.Select(hit => new SearchHitViewModel(CloneHit(hit))));
        return _materializedHits;
    }

    private int CompareHits(SearchHit left, SearchHit right)
    {
        var lineComparison = left.LineNumber.CompareTo(right.LineNumber);
        if (lineComparison != 0)
            return lineComparison;

        var matchStartComparison = left.MatchStart.CompareTo(right.MatchStart);
        if (matchStartComparison != 0)
            return matchStartComparison;

        var matchLengthComparison = left.MatchLength.CompareTo(right.MatchLength);
        if (matchLengthComparison != 0)
            return matchLengthComparison;

        return string.Compare(left.LineText, right.LineText, StringComparison.Ordinal);
    }

    private static SearchHit CloneHit(SearchHit hit)
    {
        return new SearchHit
        {
            LineNumber = hit.LineNumber,
            LineText = hit.LineText,
            MatchStart = hit.MatchStart,
            MatchLength = hit.MatchLength
        };
    }
}
