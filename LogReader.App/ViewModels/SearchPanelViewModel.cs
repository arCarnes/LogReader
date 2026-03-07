namespace LogReader.App.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public partial class SearchPanelViewModel : ObservableObject
{
    private readonly ISearchService _searchService;
    private readonly MainViewModel _mainVm;
    private CancellationTokenSource? _searchCts;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private bool _isRegex;

    [ObservableProperty]
    private bool _caseSensitive;

    [ObservableProperty]
    private bool _wholeWord;

    [ObservableProperty]
    private bool _allFiles;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public ObservableCollection<FileSearchResultViewModel> Results { get; } = new();

    public SearchPanelViewModel(ISearchService searchService, MainViewModel mainVm)
    {
        _searchService = searchService;
        _mainVm = mainVm;
    }

    [RelayCommand]
    private async Task ExecuteSearch()
    {
        if (string.IsNullOrWhiteSpace(Query)) return;

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        Results.Clear();
        IsSearching = true;
        StatusText = "Searching...";

        try
        {
            var filePaths = new List<string>();
            var encodings = new Dictionary<string, FileEncoding>();

            if (AllFiles)
            {
                foreach (var tab in _mainVm.GetAllTabs())
                {
                    filePaths.Add(tab.FilePath);
                    encodings[tab.FilePath] = tab.Encoding;
                }
            }
            else if (_mainVm.SelectedTab != null)
            {
                filePaths.Add(_mainVm.SelectedTab.FilePath);
                encodings[_mainVm.SelectedTab.FilePath] = _mainVm.SelectedTab.Encoding;
            }

            if (filePaths.Count == 0)
            {
                StatusText = "No files to search";
                IsSearching = false;
                return;
            }

            var request = new SearchRequest
            {
                Query = Query,
                IsRegex = IsRegex,
                CaseSensitive = CaseSensitive,
                WholeWord = WholeWord,
                FilePaths = filePaths
            };

            var results = await _searchService.SearchFilesAsync(request, encodings, ct);

            int totalHits = 0;
            foreach (var result in results)
            {
                if (result.Hits.Count > 0 || result.Error != null)
                {
                    Results.Add(new FileSearchResultViewModel(result, _mainVm));
                    totalHits += result.Hits.Count;
                }
            }

            StatusText = $"{totalHits:N0} matches in {results.Count(r => r.Hits.Count > 0)} files";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Search cancelled";
        }
        catch (Exception ex)
        {
            StatusText = $"Search error: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private void CancelSearch() => _searchCts?.Cancel();

    [RelayCommand]
    private void ClearResults()
    {
        Results.Clear();
        StatusText = string.Empty;
    }
}
