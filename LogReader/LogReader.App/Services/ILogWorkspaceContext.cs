namespace LogReader.App.Services;

using LogReader.App.ViewModels;

public readonly record struct GoToCommandResult(bool Succeeded, string ErrorText = "")
{
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public static GoToCommandResult Success()
        => new(true, string.Empty);

    public static GoToCommandResult Failure(string errorText)
        => new(false, errorText);
}

internal interface ILogWorkspaceContext
{
    LogTabViewModel? SelectedTab { get; }

    IReadOnlyList<LogTabViewModel> GetAllTabs();

    IReadOnlyList<LogTabViewModel> GetFilteredTabsSnapshot();

    IReadOnlyList<string> GetSearchResultFileOrderSnapshot();

    Task NavigateToLineAsync(string filePath, long lineNumber, bool disableAutoScroll = false);

    Task<GoToCommandResult> NavigateToLineAsync(string lineNumberText);

    Task<GoToCommandResult> NavigateToTimestampAsync(string timestampText);
}
