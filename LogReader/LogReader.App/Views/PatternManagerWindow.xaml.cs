namespace LogReader.App.Views;

using System.Windows;
using LogReader.App.ViewModels;

public partial class PatternManagerWindow : Window
{
    public PatternManagerWindow()
    {
        InitializeComponent();
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is PatternManagerViewModel vm && vm.HasValidationErrors)
        {
            MessageBox.Show(
                this,
                "One or more patterns have invalid replace tokens. Please fix the errors before saving.",
                "Validation Error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }
}
