namespace LogReader.App.ViewModels;

public class LogLineViewModel
{
    public int LineNumber { get; init; }
    public string Text { get; init; } = string.Empty;
    public string? HighlightColor { get; init; }
}
