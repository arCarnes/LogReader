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

    private static string GetRepoFilePath(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "LogReader.sln")))
            current = current.Parent;

        Assert.NotNull(current);
        return Path.Combine(current!.FullName, relativePath);
    }
}
