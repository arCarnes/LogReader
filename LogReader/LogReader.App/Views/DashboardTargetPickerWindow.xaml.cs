namespace LogReader.App.Views;

using System.Windows;
using System.Windows.Input;
using LogReader.App.ViewModels;

public partial class DashboardTargetPickerWindow : Window
{
    public DashboardTargetPickerWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DashboardTargetPickerViewModel { CanConfirm: true })
            DialogResult = true;
    }

    private void DashboardList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is DashboardTargetPickerViewModel { CanConfirm: true })
            DialogResult = true;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        FilterTextBox.Focus();
    }
}
