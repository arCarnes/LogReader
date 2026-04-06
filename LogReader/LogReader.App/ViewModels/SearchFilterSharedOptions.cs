namespace LogReader.App.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;

internal sealed partial class SearchFilterSharedOptions : ObservableObject
{
    [ObservableProperty]
    private SearchFilterTargetMode _targetMode = SearchFilterTargetMode.CurrentTab;

    [ObservableProperty]
    private SearchDataMode _dataMode = SearchDataMode.DiskSnapshot;
}
