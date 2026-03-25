namespace LogReader.App.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogReader.App.Services;
using LogReader.Core.Models;

public partial class FileSearchResultViewModel : ObservableObject
{
    private readonly ILogWorkspaceContext _mainVm;
    private readonly HashSet<string> _seenHitKeys = new(StringComparer.Ordinal);
    private readonly List<SearchHitViewModel> _orderedHits = new();
    private readonly BulkObservableCollection<SearchHitViewModel> _hits = new();
    private SearchResultLineOrder _lineOrder;

    public string FilePath { get; }
    public string FileName => System.IO.Path.GetFileName(FilePath);
    public ObservableCollection<SearchHitViewModel> Hits => _hits;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private int _hitCount;

    [ObservableProperty]
    private string? _error;

    internal FileSearchResultViewModel(SearchResult result, ILogWorkspaceContext mainVm, SearchResultLineOrder lineOrder = SearchResultLineOrder.Ascending)
    {
        _mainVm = mainVm;
        _lineOrder = lineOrder;
        FilePath = result.FilePath;
        Error = result.Error;
        AddHits(result.Hits, lineOrder);
    }

    public void AddHits(IEnumerable<SearchHit> hits, SearchResultLineOrder lineOrder)
    {
        var reorderRequired = _lineOrder != lineOrder;
        _lineOrder = lineOrder;
        var addedAny = false;

        foreach (var hit in hits)
        {
            var dedupeKey = $"{hit.LineNumber}:{hit.MatchStart}:{hit.MatchLength}:{hit.LineText}";
            if (!_seenHitKeys.Add(dedupeKey))
                continue;

            _orderedHits.Add(new SearchHitViewModel(hit));
            addedAny = true;
        }

        if (!addedAny && !reorderRequired)
            return;

        PublishHits();
    }

    public void ApplyLineOrder(SearchResultLineOrder lineOrder)
    {
        if (_lineOrder == lineOrder)
            return;

        _lineOrder = lineOrder;
        if (_orderedHits.Count <= 1)
            return;

        PublishHits();
    }

    public void SetError(string? error)
    {
        Error = string.IsNullOrWhiteSpace(error) ? null : error;
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
        _hits.ReplaceAll(_orderedHits);
        HitCount = _orderedHits.Count;
    }

    private int CompareHits(SearchHitViewModel left, SearchHitViewModel right)
    {
        var lineComparison = left.LineNumber.CompareTo(right.LineNumber);
        if (_lineOrder == SearchResultLineOrder.Descending)
            lineComparison = -lineComparison;

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
}
