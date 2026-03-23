namespace LogReader.App.Views;

using System.Windows;
using LogReader.App.Services;

public partial class BulkOpenDashboardPathsWindow : Window
{
    private sealed record WindowViewModel(string Title, string Instructions);

    public BulkOpenDashboardPathsWindow(BulkOpenPathsDialogRequest request)
    {
        InitializeComponent();
        DataContext = new WindowViewModel(
            request.Title,
            BuildInstructions(request.DashboardName));
        Loaded += OnLoaded;
    }

    public string PathsText => PathsTextBox.Text;

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PathsTextBox.Focus();
    }

    private static string BuildInstructions(string dashboardName)
    {
        if (string.IsNullOrWhiteSpace(dashboardName))
            return "Paste one literal file path per line. Blank lines are ignored.";

        return $"Paste one literal file path per line to add them to \"{dashboardName}\". Blank lines are ignored.";
    }
}
