namespace LogReader.App.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public partial class FilterPanelViewModel : ObservableObject
{
    private readonly ISearchService _searchService;
    private readonly MainViewModel _mainVm;
    private CancellationTokenSource? _applyFilterCts;

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
    private bool _isApplying;

    [ObservableProperty]
    private string _statusText = "Current tab snapshot only.";

    public FilterPanelViewModel(ISearchService searchService, MainViewModel mainVm)
    {
        _searchService = searchService;
        _mainVm = mainVm;
    }

    [RelayCommand]
    private async Task ApplyFilter()
    {
        var selectedTab = _mainVm.SelectedTab;
        if (selectedTab == null)
        {
            StatusText = "Select a file tab to apply a filter.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Query))
        {
            StatusText = "Enter filter text.";
            return;
        }

        if (!TimestampParser.TryBuildRange(FromTimestamp, ToTimestamp, out _, out var rangeError))
        {
            StatusText = rangeError ?? "Invalid timestamp range.";
            return;
        }

        CancelActiveApplySession(updateUi: false);
        var sessionCts = new CancellationTokenSource();
        _applyFilterCts = sessionCts;
        var ct = sessionCts.Token;
        IsApplying = true;
        StatusText = "Applying filter to current tab snapshot...";

        try
        {
            var request = new SearchRequest
            {
                Query = Query,
                IsRegex = IsRegex,
                CaseSensitive = CaseSensitive,
                WholeWord = WholeWord,
                FilePaths = new List<string> { selectedTab.FilePath },
                FromTimestamp = string.IsNullOrWhiteSpace(FromTimestamp) ? null : FromTimestamp.Trim(),
                ToTimestamp = string.IsNullOrWhiteSpace(ToTimestamp) ? null : ToTimestamp.Trim()
            };

            var result = await _searchService.SearchFileAsync(selectedTab.FilePath, request, selectedTab.Encoding, ct);
            if (!IsCurrentSession(sessionCts))
                return;

            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                StatusText = $"Filter error: {result.Error}";
                return;
            }

            var matchingLineNumbers = result.Hits
                .Select(hit => (int)hit.LineNumber)
                .Distinct()
                .OrderBy(line => line)
                .ToList();

            var rangeActive = request.FromTimestamp != null || request.ToTimestamp != null;
            if (rangeActive && !result.HasParseableTimestamps)
            {
                StatusText = "No parseable timestamps found in the current file for the selected time range.";
            }
            else
            {
                StatusText = $"Filter active: {matchingLineNumbers.Count:N0} matching lines.";
            }

            await selectedTab.ApplySnapshotFilterAsync(matchingLineNumbers, StatusText);
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentSession(sessionCts))
                StatusText = "Filter cancelled";
        }
        catch (Exception ex)
        {
            if (IsCurrentSession(sessionCts))
                StatusText = $"Filter error: {ex.Message}";
        }
        finally
        {
            if (IsCurrentSession(sessionCts))
                IsApplying = false;
        }
    }

    [RelayCommand]
    private async Task ClearFilter()
    {
        CancelActiveApplySession(updateUi: false);
        var selectedTab = _mainVm.SelectedTab;
        if (selectedTab == null)
        {
            StatusText = "Select a file tab to clear filter.";
            return;
        }

        await selectedTab.ClearSnapshotFilterAsync();
        StatusText = "Filter cleared.";
    }

    public void OnSelectedTabChanged(LogTabViewModel? selectedTab)
    {
        if (selectedTab == null)
        {
            StatusText = "Current tab snapshot only.";
            return;
        }

        if (selectedTab.IsFilterActive)
            StatusText = $"Filter active on current tab: {selectedTab.FilteredLineCount:N0} matching lines.";
        else
            StatusText = "Current tab snapshot only.";
    }

    private bool IsCurrentSession(CancellationTokenSource sessionCts)
        => ReferenceEquals(_applyFilterCts, sessionCts);

    private void CancelActiveApplySession(bool updateUi)
    {
        var current = _applyFilterCts;
        _applyFilterCts = null;
        if (current == null)
            return;

        current.Cancel();
        current.Dispose();

        if (updateUi)
        {
            IsApplying = false;
            StatusText = "Filter cancelled";
        }
    }
}
