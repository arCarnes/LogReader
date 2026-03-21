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
    private SearchResultLineOrder _lineOrder;

    public string FilePath { get; }
    public string FileName => System.IO.Path.GetFileName(FilePath);
    public ObservableCollection<SearchHitViewModel> Hits { get; } = new();

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
        ApplyLineOrder(lineOrder);

        foreach (var hit in hits)
        {
            var dedupeKey = $"{hit.LineNumber}:{hit.MatchStart}:{hit.MatchLength}:{hit.LineText}";
            if (!_seenHitKeys.Add(dedupeKey))
                continue;

            var hitVm = new SearchHitViewModel(hit);
            var insertIndex = FindInsertIndex(hitVm);
            Hits.Insert(insertIndex, hitVm);
            HitCount++;
        }
    }

    public void ApplyLineOrder(SearchResultLineOrder lineOrder)
    {
        if (_lineOrder == lineOrder)
            return;

        _lineOrder = lineOrder;
        if (Hits.Count <= 1)
            return;

        var sorted = Hits.ToList();
        sorted.Sort(CompareHits);
        Hits.Clear();
        foreach (var hit in sorted)
            Hits.Add(hit);
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

    private int FindInsertIndex(SearchHitViewModel hit)
    {
        var low = 0;
        var high = Hits.Count;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (CompareHits(Hits[mid], hit) <= 0)
                low = mid + 1;
            else
                high = mid;
        }

        return low;
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
