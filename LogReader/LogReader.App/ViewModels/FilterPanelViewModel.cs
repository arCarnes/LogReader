namespace LogReader.App.ViewModels;

using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogReader.App.Services;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public partial class FilterPanelViewModel : ObservableObject, IDisposable
{
    private readonly ISearchService _searchService;
    private readonly ILogWorkspaceContext _mainVm;
    private CancellationTokenSource? _applyFilterCts;
    private LogTabViewModel? _observedTab;

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
    private string _statusText = string.Empty;

    internal FilterPanelViewModel(ISearchService searchService, ILogWorkspaceContext mainVm)
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

        CancelActiveApplySession();
        var sessionCts = new CancellationTokenSource();
        _applyFilterCts = sessionCts;
        var ct = sessionCts.Token;
        IsApplying = true;
        StatusText = "Applying filter to current tab...";

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

            var result = await _searchService.SearchFileAsync(selectedTab.FilePath, request, selectedTab.EffectiveEncoding, ct);
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

            await selectedTab.ApplyFilterAsync(
                matchingLineNumbers,
                StatusText,
                request,
                hasParseableTimestamps: result.HasParseableTimestamps);
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
        CancelActiveApplySession();
        var selectedTab = _mainVm.SelectedTab;
        if (selectedTab == null)
        {
            StatusText = "Select a file tab to clear filter.";
            return;
        }

        await selectedTab.ClearFilterAsync();
        StatusText = "Filter cleared.";
    }

    public void OnSelectedTabChanged(LogTabViewModel? selectedTab)
    {
        if (!ReferenceEquals(_observedTab, selectedTab))
        {
            if (_observedTab != null)
                _observedTab.PropertyChanged -= SelectedTab_PropertyChanged;

            _observedTab = selectedTab;
            if (_observedTab != null)
                _observedTab.PropertyChanged += SelectedTab_PropertyChanged;
        }

        if (selectedTab == null)
        {
            StatusText = string.Empty;
            return;
        }

        if (selectedTab.IsFilterActive)
            StatusText = selectedTab.StatusText;
        else
            StatusText = string.Empty;
    }

    private void SelectedTab_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not LogTabViewModel selectedTab)
            return;

        if (e.PropertyName == nameof(LogTabViewModel.StatusText))
        {
            if (selectedTab.IsFilterActive)
                StatusText = selectedTab.StatusText;
        }
    }

    private bool IsCurrentSession(CancellationTokenSource sessionCts)
        => ReferenceEquals(_applyFilterCts, sessionCts);

    private void CancelActiveApplySession()
    {
        var current = _applyFilterCts;
        _applyFilterCts = null;
        if (current == null)
            return;

        current.Cancel();
        current.Dispose();
    }

    public void Dispose()
    {
        CancelActiveApplySession();
        if (_observedTab != null)
            _observedTab.PropertyChanged -= SelectedTab_PropertyChanged;
    }
}
