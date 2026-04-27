namespace LogReader.Tests;

public class SettingsLayoutTests
{
    [Fact]
    public void MainWindowXaml_DoesNotExposeStandaloneDateRollingPatternsButton()
    {
        var xaml = File.ReadAllText(GetRepoFilePath(@"LogReader.App\Views\MainWindow.xaml"));

        Assert.DoesNotContain("Content=\"Date Rolling Patterns\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowXaml_DoesNotExposeTopLevelFileOrHotkeysMenus()
    {
        var xaml = File.ReadAllText(GetRepoFilePath(@"LogReader.App\Views\MainWindow.xaml"));

        Assert.DoesNotContain("Header=\"_File\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"_Hotkeys\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowXaml_DisablesGlobalOpenButtonsDuringDashboardLoad()
    {
        var xaml = File.ReadAllText(GetRepoFilePath(@"LogReader.App\Views\MainWindow.xaml"));

        Assert.Equal(3, CountOccurrences(xaml, "IsEnabled=\"{Binding AreLoadAffectingActionsEnabled}\""));
        Assert.Contains("Content=\"Import View\" Command=\"{Binding ImportViewCommand}\" IsEnabled=\"{Binding AreLoadAffectingActionsEnabled}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowXaml_UsesLargerInvisibleHitTargetForSearchSplitter()
    {
        var xaml = File.ReadAllText(GetRepoFilePath(@"LogReader.App\Views\MainWindow.xaml"));

        Assert.Contains("DragCompleted=\"SearchSplitter_DragCompleted\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"10\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Margin=\"0,-5,0,-5\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"Transparent\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<GridSplitter.Template>", xaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"1\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowXaml_UsesLargerInvisibleHitTargetForGroupsSplitter()
    {
        var xaml = File.ReadAllText(GetRepoFilePath(@"LogReader.App\Views\MainWindow.xaml"));

        Assert.Contains("DragCompleted=\"GroupsSplitter_DragCompleted\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"10\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Margin=\"-5,0,-5,0\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"Transparent\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ResizeDirection=\"Columns\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"1\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsWindowXaml_ContainsDateRollingReplacementPatternsSection()
    {
        var xaml = File.ReadAllText(GetRepoFilePath(@"LogReader.App\Views\SettingsWindow.xaml"));

        Assert.Contains("Date Rolling Replacement Patterns", xaml, StringComparison.Ordinal);
        Assert.Contains("+ Add Pattern", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Import...", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Export...", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsWindowXaml_ContainsLogFontSizeSelector()
    {
        var xaml = File.ReadAllText(GetRepoFilePath(@"LogReader.App\Views\SettingsWindow.xaml"));

        Assert.Contains("SelectedItem=\"{Binding LogFontFamily, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{x:Static vm:SettingsViewModel.LogFontSizeOptions}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding LogFontSize, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsWindowXaml_DoesNotExposeDashboardLoadConcurrencySelector()
    {
        var xaml = File.ReadAllText(GetRepoFilePath(@"LogReader.App\Views\SettingsWindow.xaml"));

        Assert.DoesNotContain("Dashboard load concurrency", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DashboardLoadConcurrencyOptions", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DashboardLoadConcurrency", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Safe default: 4", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void LogViewportViewXaml_RecalculatesViewportOnSizeChanges()
    {
        var xaml = File.ReadAllText(GetRepoFilePath(@"LogReader.App\Views\LogViewportView.xaml"));

        Assert.Contains("SizeChanged=\"LogListBox_SizeChanged\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Loaded=\"LogListBox_Loaded\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Unloaded=\"LogListBox_Unloaded\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void LogViewportViewXaml_BindsRowsToStableHorizontalContentWidth()
    {
        var xaml = File.ReadAllText(GetRepoFilePath(@"LogReader.App\Views\LogViewportView.xaml"));

        Assert.Contains(
            "MinWidth=\"{Binding DataContext.HorizontalContentMinWidth, RelativeSource={RelativeSource AncestorType=ListBox}}\"",
            xaml,
            StringComparison.Ordinal);
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
    public void DashboardTreeViewXaml_DebouncesFilterTyping()
    {
        var xaml = File.ReadAllText(GetRepoFilePath(@"LogReader.App\Views\DashboardTreeView.xaml"));

        Assert.Contains(
            "Text=\"{Binding DashboardTreeFilter, UpdateSourceTrigger=PropertyChanged, Delay=200}\"",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TabStripViewXaml_UsesVirtualizingHorizontalItemsHost()
    {
        var xaml = File.ReadAllText(GetRepoFilePath(@"LogReader.App\Views\TabStripView.xaml"));

        Assert.Contains("ScrollViewer.CanContentScroll=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("VirtualizingPanel.IsVirtualizing=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("VirtualizingStackPanel Orientation=\"Horizontal\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MaxWidth\" Value=\"165\"", xaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalContentAlignment\" Value=\"Stretch\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<ColumnDefinition Width=\"*\"/>", xaml, StringComparison.Ordinal);
        Assert.Contains("Grid.Column=\"2\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<StackPanel Orientation=\"Horizontal\"/>", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void DashboardTreeViewXaml_UsesErrorCountTagInsteadOfDashboardRowTint()
    {
        var xaml = File.ReadAllText(GetRepoFilePath(@"LogReader.App\Views\DashboardTreeView.xaml"));

        Assert.DoesNotContain("<Border BorderBrush=\"{StaticResource AppBorderBrush}\" BorderThickness=\"0,0,1,0\">", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding ErrorCountTag}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"Bulk add files\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding=\"{Binding HasErroredMemberFiles}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Binding=\"{Binding HasMemberErrors}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Binding=\"{Binding HasOnlyErroredMembers}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Value=\"#FFD966\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Value=\"#F4A6A3\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void DashboardTreeViewXaml_RevealsRowActionsOnHoverOrSelectionWithoutAutoWidth()
    {
        var xaml = File.ReadAllText(GetRepoFilePath(@"LogReader.App\Views\DashboardTreeView.xaml"));

        Assert.DoesNotContain("MouseLeftButtonDown=\"GroupName_MouseDown\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"RenameGroup_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding=\"{Binding IsMouseOver, ElementName=GroupRowGrid}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"GroupRowGrid\" Margin=\"{Binding RowIndentMargin}\" Background=\"Transparent\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<Condition Binding=\"{Binding IsSelected}\" Value=\"True\"/>", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<StackPanel Grid.Column=\"3\" Orientation=\"Horizontal\">", xaml, StringComparison.Ordinal);
        Assert.Contains("<Border Grid.Column=\"3\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Width\" Value=\"98\"/>", xaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Width\" Value=\"74\"/>", xaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Opacity\" Value=\"0\"/>", xaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"IsHitTestVisible\" Value=\"False\"/>", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void DashboardTreeViewXaml_ContainsDashboardReloadButNoMemberReloadActions()
    {
        var xaml = File.ReadAllText(GetRepoFilePath(@"LogReader.App\Views\DashboardTreeView.xaml"));

        Assert.Contains("Header=\"Reload Dashboard\" Click=\"ReloadDashboard_Click\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ReloadDashboardFileDashboard_Click", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ReloadDashboardFile_Click", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"Reload File\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AreLoadAffectingActionsEnabled", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void DashboardTreeViewXaml_UsesExpandableAdHocSectionWithDividerAndClearAction()
    {
        var xaml = File.ReadAllText(GetRepoFilePath(@"LogReader.App\Views\DashboardTreeView.xaml"));

        Assert.Contains("MouseLeftButtonDown=\"AdHocExpand_MouseDown\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding AdHocMemberFiles}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"Clear Ad Hoc Files\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"ClearAdHocFiles_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MouseLeftButtonUp=\"OpenAdHocMemberFile_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"CloseAdHocMemberFile_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding=\"{Binding IsSelected}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AdHocSectionDivider\"", xaml, StringComparison.Ordinal);
        Assert.Contains("BorderBrush=\"{StaticResource AppBorderBrush}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("BorderThickness=\"0,1,0,0\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchWorkspaceViewXaml_UsesDedicatedResultsHeaderTextAndCompactStatusBindings()
    {
        var xaml = File.ReadAllText(GetRepoFilePath(@"LogReader.App\Views\SearchWorkspaceView.xaml"));

        Assert.Contains("Text=\"{Binding ResultsHeaderText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Monitor New Matches\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ToggleMonitoringNewMatchesCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding IsMonitorNewMatchesChecked, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("FontSize=\"{DynamicResource LogViewportFontSizeResource}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("FontFamily=\"{DynamicResource LogFontFamilyResource}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<ColumnDefinition Width=\"Auto\" MinWidth=\"48\"/>", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<ColumnDefinition Width=\"60\"/>", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Border BorderBrush=\"{StaticResource AppBorderBrush}\" BorderThickness=\"0,1,0,0\">", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Search\"", xaml, StringComparison.Ordinal);
        Assert.Equal(0, CountOccurrences(xaml, "Text=\"Order:\""));
        Assert.Equal(0, CountOccurrences(xaml, "Content=\"Ascending\""));
        Assert.Equal(0, CountOccurrences(xaml, "Content=\"Descending\""));
        Assert.Equal(2, CountOccurrences(xaml, "Text=\"{Binding StatusText}\""));
        Assert.DoesNotContain("Grid.Row=\"1\" Grid.ColumnSpan=\"5\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Grid.Row=\"1\" Grid.ColumnSpan=\"4\"", xaml, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(xaml, "Style=\"{StaticResource DisclosureExpanderStyle}\""));
        Assert.Equal(2, CountOccurrences(xaml, "Content=\"{Binding SearchActionButtonText}\""));
        Assert.Equal(2, CountOccurrences(xaml, "Command=\"{Binding SearchActionButtonCommand}\""));
        Assert.DoesNotContain("Content=\"Snapshot + Tail\"", xaml, StringComparison.Ordinal);
        Assert.Equal(0, CountOccurrences(xaml, "Content=\"Cancel\""));
        Assert.Equal(0, CountOccurrences(xaml, "Content=\"Clear\""));
    }

    [Fact]
    public void SearchWorkspaceViewXaml_DisablesSearchAndFilterActionsDuringDashboardLoad()
    {
        var xaml = File.ReadAllText(GetRepoFilePath(@"LogReader.App\Views\SearchWorkspaceView.xaml"));

        Assert.Contains("IsEnabled=\"{Binding AreTargetAndSourceToggleEnabled}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding AreExecutionControlsEnabled}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding IsSearchActionButtonEnabled}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding AreResultsInteractionEnabled}\"", xaml, StringComparison.Ordinal);
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
