namespace LogReader.Tests;

public class SettingsLayoutTests
{
    [Fact]
    public void MainWindowXaml_DoesNotExposeStandaloneDateRollingPatternsButton()
    {
        var xaml = File.ReadAllText(GetRepoFilePath(@"LogReader.App\Views\MainWindow.xaml"));

        Assert.DoesNotContain("Content=\"Date Rolling Patterns\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Settings\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowXaml_DoesNotExposeTopLevelFileOrHotkeysMenus()
    {
        var xaml = File.ReadAllText(GetRepoFilePath(@"LogReader.App\Views\MainWindow.xaml"));

        Assert.DoesNotContain("Header=\"_File\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"_Hotkeys\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Open Log Files\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Bulk Open Files\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Settings\"", xaml, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(xaml, "<Separator/>"));
    }

    [Fact]
    public void MainWindowXaml_UsesLargerInvisibleHitTargetForSearchSplitter()
    {
        var xaml = File.ReadAllText(GetRepoFilePath(@"LogReader.App\Views\MainWindow.xaml"));

        Assert.Contains("DragCompleted=\"SearchSplitter_DragCompleted\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"12\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Margin=\"0,-6,0,-6\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"Transparent\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<GridSplitter.Template>", xaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"1\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsWindowXaml_ContainsDateRollingPatternsSection()
    {
        var xaml = File.ReadAllText(GetRepoFilePath(@"LogReader.App\Views\SettingsWindow.xaml"));

        Assert.Contains("Date Rolling Patterns", xaml, StringComparison.Ordinal);
        Assert.Contains("+ Add Pattern", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Import...", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Export...", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void DashboardTreeViewXaml_DoesNotUseNestedDashboardFileScrollbar()
    {
        var xaml = File.ReadAllText(GetRepoFilePath(@"LogReader.App\Views\DashboardTreeView.xaml"));

        Assert.Contains("ItemsSource=\"{Binding MemberFiles}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("MaxHeight=\"240\"", xaml, StringComparison.Ordinal);
        Assert.Equal(
            1,
            CountOccurrences(xaml, "ScrollViewer.VerticalScrollBarVisibility=\"Auto\""));
        Assert.Contains("ScrollViewer.VerticalScrollBarVisibility=\"Disabled\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void DashboardTreeViewXaml_UsesErrorCountTagInsteadOfDashboardRowTint()
    {
        var xaml = File.ReadAllText(GetRepoFilePath(@"LogReader.App\Views\DashboardTreeView.xaml"));

        Assert.Contains("Text=\"{Binding ErrorCountTag}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"Bulk add files\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding=\"{Binding HasErroredMemberFiles}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Binding=\"{Binding HasMemberErrors}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Binding=\"{Binding HasOnlyErroredMembers}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Value=\"#FFD966\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Value=\"#F4A6A3\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchWorkspaceViewXaml_UsesDedicatedResultsHeaderTextAndCompactStatusBindings()
    {
        var xaml = File.ReadAllText(GetRepoFilePath(@"LogReader.App\Views\SearchWorkspaceView.xaml"));

        Assert.Contains("Text=\"{Binding ResultsHeaderText}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Border BorderBrush=\"{StaticResource AppBorderBrush}\" BorderThickness=\"0,1,0,0\">", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Search\"", xaml, StringComparison.Ordinal);
        Assert.Equal(0, CountOccurrences(xaml, "Text=\"Order:\""));
        Assert.Equal(0, CountOccurrences(xaml, "Content=\"Ascending\""));
        Assert.Equal(0, CountOccurrences(xaml, "Content=\"Descending\""));
        Assert.Equal(2, CountOccurrences(xaml, "Text=\"{Binding StatusText}\""));
        Assert.DoesNotContain("Grid.Row=\"1\" Grid.ColumnSpan=\"5\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Grid.Row=\"1\" Grid.ColumnSpan=\"4\"", xaml, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(xaml, "Style=\"{StaticResource DisclosureExpanderStyle}\""));
    }

    private static string GetRepoFilePath(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "LogReader.sln")))
            current = current.Parent;

        Assert.NotNull(current);
        return Path.Combine(current!.FullName, relativePath);
    }

    private static int CountOccurrences(string input, string value)
    {
        var count = 0;
        var startIndex = 0;
        while ((startIndex = input.IndexOf(value, startIndex, StringComparison.Ordinal)) >= 0)
        {
            count++;
            startIndex += value.Length;
        }

        return count;
    }
}
