namespace LogReader.App.Services;

using LogReader.App.ViewModels;

internal interface ILogWorkspaceContext
{
    LogTabViewModel? SelectedTab { get; }

    IReadOnlyList<LogTabViewModel> GetAllTabs();

    Task NavigateToLineAsync(string filePath, long lineNumber, bool disableAutoScroll = false);

    Task<string> NavigateToLineAsync(string lineNumberText);

    Task<string> NavigateToTimestampAsync(string timestampText);
}
