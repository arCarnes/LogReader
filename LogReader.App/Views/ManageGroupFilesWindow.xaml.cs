namespace LogReader.App.Views;

using System.Windows;
using Microsoft.Win32;
using LogReader.App.ViewModels;

public partial class ManageGroupFilesWindow : Window
{
    private ManageGroupFilesViewModel? ViewModel => DataContext as ManageGroupFilesViewModel;

    public ManageGroupFilesWindow()
    {
        InitializeComponent();
    }

    private void OK_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Add Log Files",
            Filter = "Log Files (*.log;*.txt)|*.log;*.txt|All Files (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            ViewModel.AddFiles(dialog.FileNames);
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => ViewModel?.SelectAll();
    private void DeselectAll_Click(object sender, RoutedEventArgs e) => ViewModel?.DeselectAll();
}
