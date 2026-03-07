namespace LogReader.App.ViewModels;

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public enum SearchScopeType { CurrentFile, AllFiles, Dashboard, Separator }

public class SearchScopeItem
{
    public string Label { get; }
    public SearchScopeType Type { get; }
    public string? DashboardId { get; }
    public bool IsSelectable => Type != SearchScopeType.Separator;

    public SearchScopeItem(string label, SearchScopeType type, string? dashboardId = null)
    {
        Label = label;
        Type = type;
        DashboardId = dashboardId;
    }
}

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
    private SearchScopeItem? _selectedScope;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public ObservableCollection<SearchScopeItem> ScopeItems { get; } = new();
    public ObservableCollection<FileSearchResultViewModel> Results { get; } = new();

    public SearchPanelViewModel(ISearchService searchService, MainViewModel mainVm)
    {
        _searchService = searchService;
        _mainVm = mainVm;

        RebuildScopeItems();
        SelectedScope = ScopeItems[0]; // "Current file"

        _mainVm.Groups.CollectionChanged += Groups_CollectionChanged;
        foreach (var g in _mainVm.Groups)
            g.PropertyChanged += Group_PropertyChanged;
    }

    private void Groups_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (LogGroupViewModel g in e.NewItems)
                g.PropertyChanged += Group_PropertyChanged;
        if (e.OldItems != null)
            foreach (LogGroupViewModel g in e.OldItems)
                g.PropertyChanged -= Group_PropertyChanged;

        RebuildPreservingSelection();
    }

    private void Group_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogGroupViewModel.Name))
            RebuildPreservingSelection();
    }

    private void RebuildPreservingSelection()
    {
        var prevType = SelectedScope?.Type;
        var prevDashboardId = SelectedScope?.DashboardId;

        RebuildScopeItems();

        var match = prevType switch
        {
            SearchScopeType.AllFiles => ScopeItems.FirstOrDefault(s => s.Type == SearchScopeType.AllFiles),
            SearchScopeType.Dashboard => ScopeItems.FirstOrDefault(s => s.DashboardId == prevDashboardId),
            _                        => ScopeItems.FirstOrDefault(s => s.Type == SearchScopeType.CurrentFile),
        };
        SelectedScope = match ?? ScopeItems[0];
    }

    private void RebuildScopeItems()
    {
        ScopeItems.Clear();
        ScopeItems.Add(new SearchScopeItem("Current file", SearchScopeType.CurrentFile));
        ScopeItems.Add(new SearchScopeItem("All open files", SearchScopeType.AllFiles));

        var groups = _mainVm.Groups;
        if (groups.Count > 0)
        {
            ScopeItems.Add(new SearchScopeItem("─────────────", SearchScopeType.Separator));
            foreach (var g in groups.Where(g => g.Kind == LogGroupKind.Dashboard))
                ScopeItems.Add(new SearchScopeItem($"Dashboard: {g.Name}", SearchScopeType.Dashboard, g.Id));
        }
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
            var scope = SelectedScope;

            if (scope?.Type == SearchScopeType.Dashboard && scope.DashboardId != null)
            {
                var paths = await _mainVm.GetGroupFilePathsAsync(scope.DashboardId);
                filePaths.AddRange(paths);
                foreach (var p in paths)
                {
                    var tab = _mainVm.GetAllTabs().FirstOrDefault(t =>
                        string.Equals(t.FilePath, p, StringComparison.OrdinalIgnoreCase));
                    encodings[p] = tab?.Encoding ?? FileEncoding.Utf8;
                }
            }
            else if (scope?.Type == SearchScopeType.AllFiles)
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
