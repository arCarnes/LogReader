namespace LogReader.App.Views;

using System.Windows;
using LogReader.App.Services;

public partial class BulkOpenDashboardPathsWindow : Window
{
    private sealed record PreviewRow(string Status, string FilePath);

    public BulkOpenDashboardPathsWindow(BulkOpenPathsDialogRequest request)
    {
        InitializeComponent();
        Title = request.Title;
        DialogTitleTextBlock.Text = request.Title;
        InstructionsTextBlock.Text = BuildInstructions(request);
        ConfirmButton.Content = request.Scope == BulkOpenPathsScope.Dashboard
            ? "Add to Dashboard"
            : "Open Files";
        ResetPreview();
        Loaded += OnLoaded;
    }

    public string PathsText => PathsTextBox.Text;

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        var currentText = PathsText;
        PreviewButton.IsEnabled = false;
        ConfirmButton.IsEnabled = false;

        try
        {
            var preview = await Task.Run(() => DashboardWorkspaceService.BuildBulkFilePreview(currentText));
            if (!string.Equals(PathsText, currentText, StringComparison.Ordinal))
            {
                ResetPreview();
                return;
            }

            ApplyPreview(preview);
        }
        finally
        {
            PreviewButton.IsEnabled = true;
        }
    }

    private void PathsTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        ResetPreview();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PathsTextBox.Focus();
    }

    private void ResetPreview()
    {
        ConfirmButton.IsEnabled = false;
        PreviewStatusTextBlock.Text = "Preview the pasted paths to see which files are currently reachable.";
        PreviewListView.ItemsSource = Array.Empty<PreviewRow>();
    }

    private void ApplyPreview(BulkFilePreview preview)
    {
        ConfirmButton.IsEnabled = preview.ParsedPaths.Count > 0;
        PreviewStatusTextBlock.Text = BuildPreviewStatus(preview);
        PreviewListView.ItemsSource = preview.Items
            .Select(item => new PreviewRow(item.IsFound ? "Found" : "Missing", item.FilePath))
            .ToList();
    }

    private static string BuildInstructions(BulkOpenPathsDialogRequest request)
    {
        if (request.Scope == BulkOpenPathsScope.AdHoc)
            return "Paste one literal file path per line to open them in Ad Hoc. Preview is required before you continue.";

        if (string.IsNullOrWhiteSpace(request.TargetName))
            return "Paste one literal file path per line to add them to this dashboard. Preview is required before you continue.";

        return $"Paste one literal file path per line to add them to \"{request.TargetName}\". Preview is required before you continue.";
    }

    private static string BuildPreviewStatus(BulkFilePreview preview)
    {
        if (preview.ParsedPaths.Count == 0)
            return "No literal file paths were parsed from the current input.";

        if (preview.MissingCount == 0)
            return $"Found {preview.FoundCount} of {preview.ParsedPaths.Count} paths.";

        var verb = preview.MissingCount == 1 ? "is" : "are";
        return $"Found {preview.FoundCount} of {preview.ParsedPaths.Count} paths. {preview.MissingCount} {verb} currently missing.";
    }
}
