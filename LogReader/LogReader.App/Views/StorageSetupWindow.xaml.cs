namespace LogReader.App.Views;

using System.Windows;
using LogReader.App.Services;
using LogReader.App.ViewModels;

public partial class StorageSetupWindow : Window
{
    private readonly IMessageBoxService _messageBoxService;

    public StorageSetupWindow()
        : this(new MessageBoxService())
    {
    }

    internal StorageSetupWindow(IMessageBoxService messageBoxService)
    {
        _messageBoxService = messageBoxService;
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

        _messageBoxService.Show(
            this,
            errorMessage,
            "LogReader Storage Setup",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
}
