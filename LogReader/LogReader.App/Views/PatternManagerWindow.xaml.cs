namespace LogReader.App.Views;

using System.Windows;
using LogReader.App.ViewModels;

public partial class PatternManagerWindow : Window
{
    public PatternManagerWindow()
    {
        InitializeComponent();
    }

    private async void OK_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not PatternManagerViewModel vm)
        {
            DialogResult = true;
            return;
        }

        if (await vm.TrySaveAsync(this))
            DialogResult = true;
    }
}
