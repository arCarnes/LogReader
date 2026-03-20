namespace LogReader.App.Views;

using System.Windows;
using LogReader.App.ViewModels;

public partial class StorageSetupWindow : Window
{
    public StorageSetupWindow()
    {
        InitializeComponent();
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not StorageSetupViewModel viewModel)
            return;

        if (viewModel.TryComplete(out var errorMessage))
        {
            DialogResult = true;
            return;
        }

        MessageBox.Show(
            this,
            errorMessage,
            "LogReader Storage Setup",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
}
