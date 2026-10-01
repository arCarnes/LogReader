namespace LogReader.Tests;

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.App.Views;
using LogReader.Core.Models;
using LogReader.Core;
using LogReader.Infrastructure.Services;
using static AutomaticViewportTests;

public class PersistentSelectionTests
{
    [Fact]
    public async Task Selection_ViewportReplacementResizeAppendAndPause_PreservePhysicalLines()
    {
        var reader = new ControlledReader();
        using var tab = CreateTab(reader);
        await tab.LoadAsync();
        tab.AutoScrollEnabled = false;
        tab.SelectSingleLine(12);
        tab.SelectRangeTo(20, false);
        tab.ToggleSelectedLine(150);
        var expected = tab.SelectedLineNumbers.ToArray();
        var lifecycle = tab.SelectionLifecycleRevision;
        foreach (var start in new[] { 10, 100, 15, 40, 0 })
            await tab.LoadViewportAsync(start, 50);
        tab.UpdateViewportLineCount(30);
        await tab.SynchronizeViewportCapacityAsync();
        tab.SuspendTailing();
        reader.Append("Line 201");
        await tab.ResumeTailingWithCatchUpAsync(250);
        Assert.Equal(expected, tab.SelectedLineNumbers);
        Assert.Equal(lifecycle, tab.SelectionLifecycleRevision);
        tab.ToggleSelectedLine(15);
        Assert.Equal(expected.Except(new[] { 15 }), tab.SelectedLineNumbers);
    }

    [Fact]
    public async Task FilteredRanges_UseMatchingDisplayOrder_AndContractFromStableAnchor()
    {
        using var tab = CreateTab(new ControlledReader());
        await tab.LoadAsync();
        await tab.ApplyFilterAsync(new[] { 10, 20, 30, 40, 50, 60 }, "matches");
        tab.SelectSingleLine(20);
        tab.SelectRangeTo(60, false);
        Assert.Equal(new[] { 20, 30, 40, 50, 60 }, tab.SelectedLineNumbers);
        tab.SelectRangeTo(30, false);
        Assert.Equal(new[] { 20, 30 }, tab.SelectedLineNumbers);
        Assert.Equal(20, tab.SelectionAnchor);
        Assert.Equal(30, tab.SelectionCaret);
    }

    [Fact]
    public async Task AdditiveExtension_ReversalRestoresGestureBase_AndNewGestureCapturesNewBase()
    {
        using var tab = CreateTab(new ControlledReader());
        await tab.LoadAsync();
        tab.SelectSingleLine(2);
        tab.ToggleSelectedLine(10);
        tab.SelectRangeTo(15, true, continueExtension: true);
        tab.SelectRangeTo(12, true, continueExtension: true);
        Assert.Equal(new[] { 2, 10, 11, 12 }, tab.SelectedLineNumbers);
        tab.EndSelectionExtension();
        tab.SelectRangeTo(8, true, continueExtension: true);
        Assert.Equal(new[] { 2, 8, 9, 10, 11, 12 }, tab.SelectedLineNumbers);
    }

    [Fact]
    public async Task IndexedGenerationReplacement_ResetsSelection_IdentityDiscoveryDuringAppendDoesNot()
    {
        var reader = new ControlledReader { Generation = FileGenerationToken.Unknown };
        using var tab = CreateTab(reader);
        await tab.LoadAsync();
        tab.SelectSingleLine(12);
        var lifecycle = tab.SelectionLifecycleRevision;
        reader.Generation = FileGenerationToken.Create(1, 1);
        reader.Append("Line 201");
        await tab.UpdateLineIndexLineCountAsync(CancellationToken.None);
        Assert.Equal(new[] { 12 }, tab.SelectedLineNumbers);
        Assert.Equal(lifecycle, tab.SelectionLifecycleRevision);
        reader.Generation = FileGenerationToken.Create(2, 2);
        await tab.UpdateLineIndexLineCountAsync(CancellationToken.None);
        Assert.Empty(tab.SelectedLineNumbers);
        Assert.Null(tab.SelectionAnchor);
        Assert.Null(tab.SelectionCaret);
    }

    [Theory]
    [InlineData(ModifierKeys.None)]
    [InlineData(ModifierKeys.Control)]
    [InlineData(ModifierKeys.Shift)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Shift)]
    public async Task ClickSemantics_PreserveOrReplaceOffscreenSelection(ModifierKeys modifiers)
    {
        using var tab = CreateTab(new ControlledReader());
        await tab.LoadAsync();
        tab.SelectSingleLine(2);
        tab.ToggleSelectedLine(10);
        LogViewportView.HandleLineClick(tab, 12, modifiers);
        var expected = modifiers switch
        {
            ModifierKeys.None => new[] { 12 },
            ModifierKeys.Control => new[] { 2, 10, 12 },
            ModifierKeys.Shift => new[] { 10, 11, 12 },
            _ => new[] { 2, 10, 11, 12 }
        };
        Assert.Equal(expected, tab.SelectedLineNumbers);
        Assert.Equal(12, tab.SelectionCaret);
    }

    [Fact]
    public async Task FilterRejection_PreservesSelection_SuccessAndIndexedResetInvalidateLifecycle()
    {
        using var tab = CreateTab(new ControlledReader());
        await tab.LoadAsync();
        tab.SelectSingleLine(12);
        var lifecycle = tab.SelectionLifecycleRevision;
        var rejected = await tab.TryCommitFilterSnapshotAsync(new LogFilterSession.FilterSnapshot
        {
            MatchingLineNumbers = new[] { 12 },
            GenerationEvidence = new FileScanGenerationEvidence(FileGenerationToken.Create(2, 2), FileGenerationCorrelation.Current)
        });
        Assert.False(rejected);
        Assert.Equal(new[] { 12 }, tab.SelectedLineNumbers);
        Assert.Equal(lifecycle, tab.SelectionLifecycleRevision);
        await tab.TryCommitFilterSnapshotAsync(new LogFilterSession.FilterSnapshot { MatchingLineNumbers = new[] { 12 } });
        Assert.Empty(tab.SelectedLineNumbers);
        Assert.True(tab.SelectionLifecycleRevision > lifecycle);
        tab.SelectSingleLine(12);
        await tab.ClearFilterAsync();
        Assert.DoesNotContain(12, tab.SelectedLineNumbers);
        tab.SelectSingleLine(99);
        await tab.ResetLineIndexAsync();
        Assert.Empty(tab.SelectedLineNumbers);
        Assert.Null(tab.SelectionAnchor);
        Assert.Null(tab.SelectionCaret);
    }

    [Fact]
    public async Task Copy_OffscreenDiscontiguousSnapshot_IsOrdered_AndSelectionChangesDoNotAlterIt()
    {
        var reader = new ControlledReader();
        using var tab = CreateTab(reader);
        await tab.LoadAsync();
        tab.SelectSingleLine(2);
        tab.ToggleSelectedLine(5);
        tab.ToggleSelectedLine(6);
        tab.ToggleSelectedLine(150);
        var blocked = reader.BlockNextRead();
        string? copied = null;
        var copy = tab.CopySelectedLinesAsync(value => copied = value);
        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        tab.SelectSingleLine(199);
        reader.Append("Line 201");
        var update = tab.UpdateLineIndexLineCountAsync(CancellationToken.None);
        Assert.False(update.IsCompleted);
        blocked.Release.TrySetResult();
        await copy;
        Assert.Equal(201, await update);
        Assert.Equal(string.Join(Environment.NewLine, "Line 2", "Line 5", "Line 6", "Line 150"), copied);
    }

    [Fact]
    public async Task Copy_LaterBatchFailure_DiscardsAlreadyReadText()
    {
        var reader = new ControlledReader();
        using var tab = CreateTab(reader);
        await tab.LoadAsync();
        tab.SelectSingleLine(2);
        tab.ToggleSelectedLine(5);
        var first = reader.BlockNextRead();
        string? published = null;
        var copy = tab.CopySelectedLinesAsync(value => published = value);
        await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = reader.BlockNextRead();
        first.Release.TrySetResult();
        await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        reader.FailNextRead = true;
        second.Release.TrySetResult();
        await Assert.ThrowsAsync<IOException>(() => copy);
        Assert.Null(published);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("read failure")]
    [InlineData("generation")]
    [InlineData("filter")]
    [InlineData("encoding")]
    [InlineData("close")]
    public async Task Copy_InvalidationOrReadFailure_NeverPublishesPartialText(string reason)
    {
        var reader = new ControlledReader();
        using var tab = CreateTab(reader);
        await tab.LoadAsync();
        tab.SelectSingleLine(2);
        tab.ToggleSelectedLine(5);
        var blocked = reader.BlockNextRead();
        string? copied = null;
        var copy = tab.CopySelectedLinesAsync(value => copied = value);
        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        switch (reason)
        {
            case "missing": reader.OmitLastLineOnNextRead = true; break;
            case "read failure": reader.FailNextRead = true; break;
            case "generation": tab.ActiveSession.DebugLineIndex!.GenerationToken = FileGenerationToken.Create(3, 3); break;
            case "filter": await tab.TryCommitFilterSnapshotAsync(new LogFilterSession.FilterSnapshot { MatchingLineNumbers = new[] { 2 } }); break;
            case "encoding": tab.Encoding = FileEncoding.Utf16; break;
            case "close": tab.BeginShutdown(); break;
        }
        blocked.Release.TrySetResult();
        if (reason == "close") await copy;
        else await Assert.ThrowsAsync<IOException>(() => copy);
        Assert.Null(copied);
    }

    [Fact]
    public async Task Copy_EmptySelection_DoesNotReadOrPublish_AndLargeRangesUseBoundedBatches()
    {
        var reader = new ControlledReader();
        for (var i = 201; i <= 2500; i++) reader.Append($"Line {i}");
        using var tab = CreateTab(reader);
        await tab.LoadAsync();
        var readCount = reader.ReadCount;
        await tab.CopySelectedLinesAsync(_ => throw new InvalidOperationException("Empty selection must not publish."));
        Assert.Equal(readCount, reader.ReadCount);
        tab.SelectSingleLine(1);
        tab.SelectRangeTo(2500, false);
        string? text = null;
        await tab.CopySelectedLinesAsync(value => text = value);
        Assert.Equal(2500, text!.Split(Environment.NewLine).Length);
        Assert.Equal(new[] { (0, 1024), (1024, 1024), (2048, 452) }, reader.ReadRequests.Skip(readCount));
    }

    [Fact]
    public async Task LiveProjection_EntireSelectionLeavesAndReturns_ResizeSwitchAndViewRecreationPreserveIt()
    {
        await RunViewportAsync(async (vm, tab, view, window, reader) =>
        {
            var list = FindList(view);
            // Exercise native SelectionChanged deltas as used during a mouse drag.
            list.SelectedItems.Add(list.Items[0]);
            list.SelectedItems.Add(list.Items[1]);
            var expected = tab.SelectedLineNumbers.ToArray();
            Assert.Equal(2, expected.Length);
            await tab.LoadViewportAsync(0, tab.ViewportLineCount);
            await WpfTestHost.FlushAsync();
            Assert.Empty(list.SelectedItems);
            Assert.Equal(expected, tab.SelectedLineNumbers);
            await tab.LoadViewportAsync(expected[0] - 1, tab.ViewportLineCount);
            await WpfTestHost.FlushAsync();
            Assert.Equal(expected, list.SelectedItems.Cast<LogLineViewModel>().Select(line => line.LineNumber).Order());

            var blocked = reader.BlockNextRead();
            var stale = tab.LoadViewportAsync(20, tab.ViewportLineCount);
            await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await tab.LoadViewportAsync(70, tab.ViewportLineCount);
            await tab.LoadViewportAsync(expected[0] - 1, tab.ViewportLineCount);
            blocked.Release.TrySetResult();
            Assert.False(await stale);
            window.Height = 500;
            await WpfTestHost.FlushAsync();
            await tab.SynchronizeViewportCapacityAsync();
            await WpfTestHost.FlushAsync();
            Assert.Equal(expected, tab.SelectedLineNumbers);

            using var other = CreateTab(reader);
            await other.LoadAsync();
            vm.Tabs.Add(other);
            other.SelectSingleLine(5);
            foreach (var selected in new[] { other, tab, other, tab })
            {
                vm.SelectedTab = selected;
                await WpfTestHost.FlushAsync();
                Assert.Equal(expected, tab.SelectedLineNumbers);
                Assert.Equal(new[] { 5 }, other.SelectedLineNumbers);
            }
            var replacement = new LogViewportView { DataContext = vm };
            window.Content = replacement;
            await WpfTestHost.FlushAsync();
            Assert.Equal(expected, tab.SelectedLineNumbers);
            Assert.Equal(expected, FindList(replacement).SelectedItems.Cast<LogLineViewModel>().Select(line => line.LineNumber).Order());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RapidShiftArrows_CrossBothEdges_UseIntendedCaretAndContract(bool filtered)
    {
        await RunViewportAsync(async (vm, tab, view, _, _) =>
        {
            if (filtered)
                await tab.ApplyFilterAsync(Enumerable.Range(1, 100).Select(i => i * 2).ToArray(), "even lines");
            await tab.LoadViewportAsync(40, tab.ViewportLineCount);
            await WpfTestHost.FlushAsync();
            var list = FindList(view);
            var anchor = tab.VisibleLines.Last().LineNumber;
            tab.SelectSingleLine(anchor);
            var increment = filtered ? 2 : 1;
            var steps = tab.ViewportLineCount * 2;
            vm.GlobalAutoScrollEnabled = true;
            await vm.AutoScrollSyncTask;
            await tab.LoadViewportAsync(40, tab.ViewportLineCount);
            await WpfTestHost.FlushAsync();
            for (var i = 0; i < steps; i++)
                Assert.True(LogViewportView.HandleKeyboardNavigation(list, vm, tab, Key.Down, ModifierKeys.Shift));
            Assert.False(vm.GlobalAutoScrollEnabled);
            Assert.Equal(anchor + steps * increment, tab.SelectionCaret);
            Assert.Equal(steps + 1, tab.SelectedLineNumbers.Count);
            await tab.RequestScrollTo(tab.ScrollPosition);
            await WpfTestHost.FlushAsync();
            Assert.Equal(anchor + steps * increment, tab.SelectionCaret);
            var contractionSteps = tab.ViewportLineCount;
            for (var i = 0; i < steps + contractionSteps; i++)
            {
                var expectedNext = Math.Max(increment, tab.SelectionCaret!.Value - increment);
                LogViewportView.HandleKeyboardNavigation(list, vm, tab, Key.Up, ModifierKeys.Shift);
                Assert.Equal(expectedNext, tab.SelectionCaret);
            }
            var expectedCaret = Math.Max(increment, anchor - contractionSteps * increment);
            Assert.Equal(expectedCaret, tab.SelectionCaret);
            Assert.Equal((anchor - expectedCaret) / increment + 1, tab.SelectedLineNumbers.Count);
            Assert.Equal(anchor, tab.SelectionAnchor);
            if (filtered) Assert.All(tab.SelectedLineNumbers, line => Assert.Equal(0, line % 2));
            await tab.RequestScrollTo(tab.ScrollPosition);
            await WpfTestHost.FlushAsync();
            Assert.Equal(tab.SelectedLineNumbers.Intersect(tab.VisibleLines.Select(line => line.LineNumber)).Order(),
                list.SelectedItems.Cast<LogLineViewModel>().Select(line => line.LineNumber).Order());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothCopyEntryPoints_CopyOffscreenLines_AndFailuresUseViewActionError(bool contextMenu)
    {
        var copied = "unchanged";
        var errors = await RunViewportAsync(async (_, tab, view, _, reader) =>
        {
            tab.SelectSingleLine(2);
            tab.ToggleSelectedLine(5);
            tab.ToggleSelectedLine(150);
            var list = FindList(view);
            if (contextMenu)
            {
                list.ContextMenu.PlacementTarget = list;
                list.ContextMenu.IsOpen = true;
                await WpfTestHost.FlushAsync();
                var item = list.ContextMenu.Items.OfType<MenuItem>().First(menu => (string)menu.Tag == "CopySelectedLines");
                await WaitForAsync(() => item.IsEnabled);
                var provider = (IInvokeProvider)new MenuItemAutomationPeer(item).GetPattern(PatternInterface.Invoke);
                provider.Invoke();
            }
            else
            {
                Assert.Contains(ApplicationCommands.Copy.InputGestures.Cast<InputGesture>(),
                    gesture => gesture is KeyGesture { Key: Key.C, Modifiers: ModifierKeys.Control });
                ApplicationCommands.Copy.Execute(null, list);
            }
            await WaitForAsync(() => copied != "unchanged");
            Assert.Equal(string.Join(Environment.NewLine, "Line 2", "Line 5", "Line 150"), copied);
            list.ContextMenu.IsOpen = false;
            reader.OmitLastLineOnNextRead = true;
            copied = "unchanged";
            await view.CopySelectedLinesAsync(list);
            Assert.Equal("unchanged", copied);
        }, value => copied = value);
        Assert.Equal("Copy Selected Lines Failed", errors.LastCaption);
    }

    private static async Task<StubMessageBoxService> RunViewportAsync(
        Func<MainViewModel, LogTabViewModel, LogViewportView, Window, ControlledReader, Task> action,
        Action<string>? publishText = null)
    {
        var errors = new StubMessageBoxService();
        await WpfTestHost.RunAsync(async () =>
        {
            var reader = new ControlledReader();
            using var vm = TestMainViewModelFactory.Create(new StubLogFileRepository(), new StubLogGroupRepository(),
                new StubSettingsRepository(), reader, new StubSearchService(), new StubFileTailService(),
                new StubEncodingDetectionService(), enableLifecycleTimer: false, messageBoxService: errors);
            using var tab = CreateTab(reader);
            await tab.LoadAsync();
            vm.Tabs.Add(tab);
            vm.SelectedTab = tab;
            vm.GlobalAutoScrollEnabled = false;
            var view = publishText == null ? new LogViewportView() : new LogViewportView(publishText);
            view.DataContext = vm;
            var window = new Window { Style = new Style(typeof(Window)), Content = view, DataContext = vm, Width = 640, Height = 320 };
            try
            {
                WpfTestHost.ShowHidden(window);
                await WpfTestHost.FlushAsync();
                await action(vm, tab, view, window, reader);
            }
            finally { window.Close(); }
        });
        return errors;
    }

    [Fact]
    public async Task GrowingFile_WpfSmoke_SearchToggleExtendScrollBackAndCopy()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "WeezTailSelectionSmoke_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            using var scope = AppPaths.BeginTestScope(rootPath: directory, baseDirectory: directory);
            var path = Path.Combine(directory, "growing.log");
            var lines = Enumerable.Range(1, 400).Select(i => i == 50 ? "SEARCH_TARGET" : $"Line {i}").ToList();
            await File.WriteAllLinesAsync(path, lines);
            var reader = new ChunkedLogReaderService();
            var search = new SearchService();
            var tail = new StubFileTailService();
            using var vm = TestMainViewModelFactory.Create(new StubLogFileRepository(), new StubLogGroupRepository(),
                new StubSettingsRepository(), reader, search, tail, new StubEncodingDetectionService());
            await vm.InitializeAsync();
            await vm.OpenFilePathAsync(path);
            var tab = vm.SelectedTab!;
            string? copied = null;
            var view = new LogViewportView(value => copied = value) { DataContext = vm };
            var window = new Window { Style = new Style(typeof(Window)), Content = view, DataContext = vm, Width = 640, Height = 320 };
            try
            {
                WpfTestHost.ShowHidden(window);
                await WpfTestHost.FlushAsync();
                var result = await search.SearchFileAsync(path, new SearchRequest { Query = "SEARCH_TARGET" }, FileEncoding.Utf8);
                var fileResult = new FileSearchResultViewModel(result, vm);
                var hit = Assert.Single(fileResult.Hits);
                await File.AppendAllLinesAsync(path, Enumerable.Range(401, 10).Select(i => $"Line {i}"));
                tail.RaiseLinesAppended(path);
                await fileResult.NavigateToHitCommand.ExecuteAsync(hit);
                await WaitForAsync(() => tab.TotalLines == 410);
                await WpfTestHost.FlushAsync();
                Assert.False(vm.GlobalAutoScrollEnabled);
                Assert.Contains(tab.VisibleLines, line => line.LineNumber == 50);
                Assert.Equal(new[] { 50 }, tab.SelectedLineNumbers);
                var committedStart = tab.ViewportStartLine;
                await File.AppendAllLinesAsync(path, Enumerable.Range(411, 10).Select(i => $"Line {i}"));
                tail.RaiseLinesAppended(path);
                await WaitForAsync(() => tab.TotalLines == 420);
                Assert.Equal(committedStart, tab.ViewportStartLine);

                var list = FindList(view);
                var checkbox = FindControl<CheckBox>(view);
                var toggle = (IToggleProvider)new CheckBoxAutomationPeer(checkbox).GetPattern(PatternInterface.Toggle);
                toggle.Toggle();
                await vm.AutoScrollSyncTask;
                Assert.Equal(tab.MaxScrollPosition, tab.ViewportStartLine);
                await File.AppendAllLinesAsync(path, new[] { "Line 421" });
                tail.RaiseLinesAppended(path);
                await WaitForAsync(() => tab.VisibleLines.Last().LineNumber == 421);
                toggle.Toggle();
                await tab.LoadViewportAsync(40, tab.ViewportLineCount);
                tab.SelectSingleLine(50);
                await WpfTestHost.FlushAsync();
                var steps = tab.ViewportLineCount * 3;
                for (var i = 0; i < steps; i++)
                    LogViewportView.HandleKeyboardNavigation(list, vm, tab, Key.Down, ModifierKeys.Shift);
                var selected = tab.SelectedLineNumbers.ToArray();
                Assert.Equal(Enumerable.Range(50, steps + 1), selected);
                await tab.RequestScrollTo(tab.ScrollPosition);
                await tab.LoadViewportAsync(0, tab.ViewportLineCount);
                await WpfTestHost.FlushAsync();
                Assert.Equal(selected, tab.SelectedLineNumbers);
                await tab.LoadViewportAsync(40, tab.ViewportLineCount);
                await WpfTestHost.FlushAsync();
                Assert.Contains(list.SelectedItems.Cast<LogLineViewModel>(), line => line.LineNumber == 50);
                ApplicationCommands.Copy.Execute(null, list);
                await WaitForAsync(() => copied != null);
                Assert.Equal(string.Join(Environment.NewLine, selected.Select(i => i == 50 ? "SEARCH_TARGET" : $"Line {i}")), copied);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task FilteredTailInPlaceMutation_PreservesSelectionAfterSelectedRowsLeaveViewport()
    {
        await RunViewportAsync(async (vm, tab, view, _, reader) =>
        {
            await tab.ApplyFilterAsync(Enumerable.Range(1, 200).ToArray(), "matches",
                new SearchRequest { Query = "Line", SourceMode = SearchRequestSourceMode.SnapshotAndTail });
            vm.GlobalAutoScrollEnabled = true;
            await vm.AutoScrollSyncTask;
            await WpfTestHost.FlushAsync();
            tab.SelectSingleLine(tab.VisibleLines.First().LineNumber);
            tab.SelectRangeTo(tab.VisibleLines[2].LineNumber, false);
            var expected = tab.SelectedLineNumbers.ToArray();
            var lifecycle = tab.SelectionLifecycleRevision;
            for (var i = 201; i <= 250; i++) reader.Append($"Line {i}");
            tab.SuspendTailing();
            await tab.ResumeTailingWithCatchUpAsync(250);
            await WpfTestHost.FlushAsync();
            Assert.Equal(250, tab.FilteredLineCount);
            Assert.Equal(expected, tab.SelectedLineNumbers);
            Assert.Equal(lifecycle, tab.SelectionLifecycleRevision);
            Assert.Empty(FindList(view).SelectedItems);
            vm.GlobalAutoScrollEnabled = false;
            await tab.LoadViewportAsync(expected[0] - 1, tab.ViewportLineCount);
            await WpfTestHost.FlushAsync();
            Assert.Equal(expected, FindList(view).SelectedItems.Cast<LogLineViewModel>().Select(line => line.LineNumber).Order());
        });
    }

    [Fact]
    public async Task PendingTargetRealization_UserSelectionCancelsObsoleteRetry()
    {
        var errors = await RunViewportAsync(async (_, tab, _, _, reader) =>
        {
            var start = tab.ViewportStartLine;
            var userLine = tab.VisibleLines[1].LineNumber;
            var blocked = reader.BlockNextRead();
            tab.SetNavigateTargetLine(2);
            await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            LogViewportView.HandleLineClick(tab, userLine, ModifierKeys.None);
            await blocked.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            blocked.Release.TrySetResult();
            await WpfTestHost.FlushAsync();
            Assert.Equal(start, tab.ViewportStartLine);
            Assert.Equal(new[] { userLine }, tab.SelectedLineNumbers);
            Assert.Equal(userLine, tab.SelectionCaret);
        });
        Assert.Null(errors.LastCaption);
    }

    private static T FindControl<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T control) return control;
            try { return FindControl<T>(child); }
            catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException($"{typeof(T).Name} was not realized.");
    }

    private static ListBox FindList(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ListBox { Name: "LogListBox" } list) return list;
            try { return FindList(child); }
            catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException("The viewport ListBox was not realized.");
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("WPF action did not complete.");
            await Task.Delay(10);
        }
    }
}
