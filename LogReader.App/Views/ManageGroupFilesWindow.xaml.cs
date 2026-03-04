namespace LogReader.App.Views;

using System.Windows;
using LogReader.App.ViewModels;

public partial class ManageGroupFilesWindow : Window
{
    private ManageGroupFilesViewModel? ViewModel => DataContext as ManageGroupFilesViewModel;

    public ManageGroupFilesWindow()
    {
        InitializeComponent();
    }

    private void OK_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void SelectAll_Click(object sender, RoutedEventArgs e) => ViewModel?.SelectAll();
    private void DeselectAll_Click(object sender, RoutedEventArgs e) => ViewModel?.DeselectAll();
}
