namespace LogReader.App.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogReader.Core.Models;

public partial class FileSearchResultViewModel : ObservableObject
{
    private readonly MainViewModel _mainVm;

    public string FilePath { get; }
    public string FileName => System.IO.Path.GetFileName(FilePath);
    public int HitCount { get; }
    public string? Error { get; }
    public ObservableCollection<SearchHitViewModel> Hits { get; } = new();

    [ObservableProperty]
    private bool _isExpanded = true;

    public FileSearchResultViewModel(SearchResult result, MainViewModel mainVm)
    {
        _mainVm = mainVm;
        FilePath = result.FilePath;
        HitCount = result.Hits.Count;
        Error = result.Error;

        foreach (var hit in result.Hits)
        {
            Hits.Add(new SearchHitViewModel(hit));
        }
    }

    [RelayCommand]
    private async Task NavigateToHit(SearchHitViewModel? hit)
    {
        if (hit == null) return;
        await _mainVm.NavigateToLineAsync(FilePath, hit.LineNumber);
    }
}
