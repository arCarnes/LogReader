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
