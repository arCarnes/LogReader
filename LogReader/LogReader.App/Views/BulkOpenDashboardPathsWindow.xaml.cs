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
            var preview = await Task.Run(() => BulkFilePathHelper.BuildPreview(currentText));
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
        PreviewStatusTextBlock.Text = "Preview the pasted paths or wildcard patterns to see which files are currently reachable.";
        PreviewListView.ItemsSource = Array.Empty<PreviewRow>();
    }

    private void ApplyPreview(BulkFilePreview preview)
    {
        ConfirmButton.IsEnabled = preview.ParsedPaths.Count > 0;
        PreviewStatusTextBlock.Text = BuildPreviewStatus(preview);
        PreviewListView.ItemsSource = preview.Items
            .Select(item => new PreviewRow(GetPreviewStatusLabel(item.Status), item.FilePath))
            .ToList();
    }

    private static string BuildInstructions(BulkOpenPathsDialogRequest request)
    {
        if (request.Scope == BulkOpenPathsScope.AdHoc)
            return "One file path or pattern (*.log, file names only) per line.";

        if (string.IsNullOrWhiteSpace(request.TargetName))
            return "One file path or pattern (*.log, file names only) per line.";

        return "One file path or pattern (*.log, file names only) per line.";
    }

    private static string BuildPreviewStatus(BulkFilePreview preview)
    {
        var unmatchedPatternCount = preview.Items.Count(item => item.Status == BulkFilePreviewItemStatus.NoMatches);
        var issueCount = preview.MissingCount + preview.UnavailableCount;
        if (preview.ParsedPaths.Count == 0)
        {
            if (preview.UnavailableCount > 0)
                return $"{preview.UnavailableCount} path or wildcard pattern{(preview.UnavailableCount == 1 ? string.Empty : "s")} could not be checked.";

            return unmatchedPatternCount == 0
                ? "No file paths or wildcard matches were parsed from the current input."
                : $"{unmatchedPatternCount} wildcard pattern{(unmatchedPatternCount == 1 ? string.Empty : "s")} did not match any files.";
        }

        if (issueCount == 0)
        {
            var suffix = unmatchedPatternCount == 0
                ? string.Empty
                : $" {unmatchedPatternCount} wildcard pattern{(unmatchedPatternCount == 1 ? string.Empty : "s")} had no matches.";
            return $"Found {preview.FoundCount} of {preview.ParsedPaths.Count} paths.{suffix}";
        }

        var issueSummary = preview.UnavailableCount == 0
            ? $"{preview.MissingCount} {(preview.MissingCount == 1 ? "is" : "are")} currently missing."
            : $"{preview.MissingCount} missing; {preview.UnavailableCount} unavailable.";
        var unmatchedSuffix = unmatchedPatternCount == 0
            ? string.Empty
            : $" {unmatchedPatternCount} wildcard pattern{(unmatchedPatternCount == 1 ? string.Empty : "s")} had no matches.";
        return $"Found {preview.FoundCount} of {preview.ParsedPaths.Count} paths. {issueSummary}{unmatchedSuffix}";
    }

    private static string GetPreviewStatusLabel(BulkFilePreviewItemStatus status)
    {
        return status switch
        {
            BulkFilePreviewItemStatus.Found => "Found",
            BulkFilePreviewItemStatus.Missing => "Missing",
            BulkFilePreviewItemStatus.NoMatches => "No Matches",
            BulkFilePreviewItemStatus.AccessDenied => "Access Denied",
            BulkFilePreviewItemStatus.InvalidPath => "Invalid Path",
            BulkFilePreviewItemStatus.Unavailable => "Unavailable",
            _ => "Unknown"
        };
    }
}
