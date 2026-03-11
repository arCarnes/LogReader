namespace LogReader.App.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogReader.Core.Models;

public partial class FileSearchResultViewModel : ObservableObject
{
    private readonly MainViewModel _mainVm;
    private readonly HashSet<string> _seenHitKeys = new(StringComparer.Ordinal);

    public string FilePath { get; }
    public string FileName => System.IO.Path.GetFileName(FilePath);
    public ObservableCollection<SearchHitViewModel> Hits { get; } = new();

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private int _hitCount;

    [ObservableProperty]
    private string? _error;

    public FileSearchResultViewModel(SearchResult result, MainViewModel mainVm)
    {
        _mainVm = mainVm;
        FilePath = result.FilePath;
        Error = result.Error;
        AddHits(result.Hits);
    }

    public void AddHits(IEnumerable<SearchHit> hits)
    {
        foreach (var hit in hits)
        {
            var dedupeKey = $"{hit.LineNumber}:{hit.MatchStart}:{hit.MatchLength}:{hit.LineText}";
            if (!_seenHitKeys.Add(dedupeKey))
                continue;

            Hits.Add(new SearchHitViewModel(hit));
            HitCount++;
        }
    }

    public void SetError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return;

        Error = error;
    }

    [RelayCommand]
    private async Task NavigateToHit(SearchHitViewModel? hit)
    {
        if (hit == null) return;
        await _mainVm.NavigateToLineAsync(FilePath, hit.LineNumber);
    }
}
