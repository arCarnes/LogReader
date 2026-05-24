namespace LogReader.App.ViewModels;

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using LogReader.App.Services;

internal partial class DashboardTargetPickerViewModel : ObservableObject
{
    private readonly ICollectionView _dashboardView;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private DashboardTargetPickerRow? _selectedDashboard;

    public DashboardTargetPickerViewModel(DashboardTargetPickerRequest request)
    {
        Title = request.Title;
        ConfirmText = request.ConfirmText;
        Dashboards = new ObservableCollection<DashboardTargetPickerRow>(request.Dashboards);
        _dashboardView = CollectionViewSource.GetDefaultView(Dashboards);
        _dashboardView.Filter = FilterDashboard;
        SelectedDashboard = Dashboards.FirstOrDefault(row => row.IsEnabled);
    }

    public string Title { get; }

    public string ConfirmText { get; }

    public ObservableCollection<DashboardTargetPickerRow> Dashboards { get; }

    public ICollectionView DashboardView => _dashboardView;

    public bool CanConfirm => SelectedDashboard?.IsEnabled == true;

    partial void OnFilterTextChanged(string value)
    {
        _dashboardView.Refresh();
        if (SelectedDashboard is not { IsEnabled: true } || !_dashboardView.Contains(SelectedDashboard))
            SelectedDashboard = _dashboardView.Cast<DashboardTargetPickerRow>().FirstOrDefault(row => row.IsEnabled);
    }

    partial void OnSelectedDashboardChanged(DashboardTargetPickerRow? value)
        => OnPropertyChanged(nameof(CanConfirm));

    private bool FilterDashboard(object item)
    {
        if (item is not DashboardTargetPickerRow row)
            return false;

        if (string.IsNullOrWhiteSpace(FilterText))
            return true;

        return row.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ||
               row.Path.Contains(FilterText, StringComparison.OrdinalIgnoreCase);
    }
}
