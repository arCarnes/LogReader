namespace LogReader.Tests;

using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LogReader.App.Controls;
using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.App.Views;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

public sealed partial class DashboardLoadingResponsivenessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WeezTail-responsive-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _scope;

    public DashboardLoadingResponsivenessTests()
    {
        Directory.CreateDirectory(_root);
        _scope = AppPaths.BeginTestScope(rootPath: _root, localCacheDirectory: Path.Combine(_root, "Cache"));
    }

    [Fact]
    public async Task Preview_IsReadableAndCopyableBeforeTheIndexFinishes()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync();
            var load = fixture.ViewModel.HandleDashboardGroupInvokedAsync(fixture.ViewModel.Groups[0]);
            await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var tab = fixture.ViewModel.SelectedTab!;
            await WaitForPreviewAsync(tab);
            Assert.False(load.IsCompleted);
            Assert.False(tab.IsIndexReady);
            Assert.False(tab.ShowIndexedContent);
            Assert.Contains("Indexing", tab.LoadingProgressText);
            Assert.Contains(" MB of ", tab.LoadingProgressText);
            Assert.False(tab.JumpToTopCommand.CanExecute(null));
            Assert.False(fixture.ViewModel.FilterPanel.AreExecutionControlsEnabled);
            Assert.Equal(200, tab.PreviewLines.Count);
            string? copied = null;
            var view = new LogViewportView(text => copied = text) { DataContext = fixture.ViewModel };
            var window = ShowWindow(view);
            window.UpdateLayout();
            var list = Find<ListBox>(view, "PreviewListBox")!;
            Assert.NotNull(list);
            list.SelectedItems.Add(tab.PreviewLines[0]);
            list.SelectedItems.Add(tab.PreviewLines[^1]);
            Assert.True(ApplicationCommands.Copy.CanExecute(null, list));
            ApplicationCommands.Copy.Execute(null, list);
            Assert.Equal(tab.PreviewLines[0].Text + Environment.NewLine + tab.PreviewLines[^1].Text, copied);
            Assert.False(tab.ShowIndexedContent);
            SaveRender(window, "preview-light.png");
            var appearance = new WpfLogAppearanceService();
            appearance.Apply(new AppSettings { IsDarkMode = true });
            window.UpdateLayout();
            SaveRender(window, "preview-dark.png");
            appearance.Apply(new AppSettings());
            fixture.Reader.Release();
            await load.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(tab.IsIndexReady);
            Assert.Empty(tab.PreviewLines);
            window.Close();
        });
    }

    [Fact]
    public async Task SwitchingDashboards_KeepsOldWorkAndDoesNotStealSelectionOnBackgroundCompletion()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync();
            var vm = fixture.ViewModel;
            var first = vm.HandleDashboardGroupInvokedAsync(vm.Groups[0]);
            await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await vm.HandleDashboardGroupInvokedAsync(vm.Groups[1]).WaitAsync(TimeSpan.FromSeconds(5));
            var selected = vm.SelectedTab;
            Assert.Equal(vm.Groups[1].Id, vm.ActiveDashboardId);
            Assert.False(first.IsCompleted);
            Assert.True(vm.Groups[0].IsDashboardLoading);
            fixture.Reader.Release();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(selected, vm.SelectedTab);
            Assert.False(vm.Groups[0].IsDashboardLoading);
            Assert.True(vm.Groups[0].DashboardReadyRevision > 0);
            Assert.Equal(1, fixture.Reader.BuildCounts[fixture.LargePath]);
        });
    }

    [Fact]
    public async Task ReturnAndRepeatedClicks_ReuseTheSamePendingCycle()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync();
            var vm = fixture.ViewModel;
            var first = vm.HandleDashboardGroupInvokedAsync(vm.Groups[0]);
            await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await vm.HandleDashboardGroupInvokedAsync(vm.Groups[1]).WaitAsync(TimeSpan.FromSeconds(5));
            var returned = vm.HandleDashboardGroupInvokedAsync(vm.Groups[0]);
            var repeated = vm.HandleDashboardGroupInvokedAsync(vm.Groups[0]);
            Assert.Equal(vm.Groups[0].Id, vm.ActiveDashboardId);
            Assert.Equal(1, fixture.Reader.BuildCounts[fixture.LargePath]);
            fixture.Reader.Release();
            await Task.WhenAll(first, returned, repeated).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, vm.Groups[0].DashboardReadyRevision);
            Assert.Single(vm.Tabs.Where(tab => tab.ScopeDashboardId == vm.Groups[0].Id));
        });
    }

    [Fact]
    public async Task PartialSuccess_FadesButAllFailuresDoNot()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync(includeMissing: true);
            var vm = fixture.ViewModel;
            var first = vm.HandleDashboardGroupInvokedAsync(vm.Groups[0]);
            await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await vm.HandleDashboardGroupInvokedAsync(vm.Groups[1]).WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Reader.Release();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("1/2 files ready; 1 failed", vm.Groups[0].DashboardLoadStatus);
            Assert.True(vm.Groups[0].DashboardReadyRevision > 0);
            Assert.True(vm.Tabs.Single(tab => tab.FilePath.EndsWith("missing.log")).HasLoadError);
        });
    }

    [Fact]
    public async Task ManualPreviewAnchorAndSelection_MapToTheFinishedIndex()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync();
            var vm = fixture.ViewModel;
            var load = vm.HandleDashboardGroupInvokedAsync(vm.Groups[0]);
            await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var tab = vm.SelectedTab!;
            await WaitForPreviewAsync(tab);
            vm.GlobalAutoScrollEnabled = false;
            await vm.AutoScrollSyncTask;
            var anchor = tab.PreviewLines[20].ByteOffset;
            var selected = tab.PreviewLines[22].ByteOffset;
            tab.RememberPreviewPosition(anchor, [selected]);
            fixture.Reader.Release();
            await load.WaitAsync(TimeSpan.FromSeconds(5));
            var index = tab.ActiveSession.DebugLineIndex!;
            var expectedStart = Enumerable.Range(0, index.LineCount).Single(i => index.LineOffsets[i] == anchor);
            Assert.Equal(expectedStart, tab.ViewportStartLine);
            Assert.Contains(expectedStart + 3, tab.SelectedLineNumbers);
        });
    }

    [Fact]
    public async Task UnloadDuringLoading_CancelsOnlyThatDashboardAndLeavesNoResurrectedTab()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync();
            var vm = fixture.ViewModel;
            var first = vm.HandleDashboardGroupInvokedAsync(vm.Groups[0]);
            await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await vm.HandleDashboardGroupInvokedAsync(vm.Groups[1]).WaitAsync(TimeSpan.FromSeconds(5));
            var selected = vm.SelectedTab;
            await vm.UnloadDashboardAsync(vm.Groups[0]).WaitAsync(TimeSpan.FromSeconds(5));
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.Reader.BlockedBuildDisposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain(vm.Tabs, tab => tab.ScopeDashboardId == vm.Groups[0].Id);
            Assert.Same(selected, vm.SelectedTab);
            Assert.Equal(0, vm.Groups[0].DashboardReadyRevision);
            Assert.False(vm.IsDashboardLoading);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnloadDuringResolution_PreservesAnyNewerScopeSelection(bool navigateAway)
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var files = new GatedTargetRepository();
            await using var fixture = await CreateAsync(fileRepository: files);
            var vm = fixture.ViewModel;
            var group = vm.Groups[0];
            files.HeldFileId = group.MemberFiles.Single().FileId;
            var load = vm.HandleDashboardGroupInvokedAsync(group);
            try
            {
                await files.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var unload = vm.UnloadDashboardAsync(group);
                Assert.False(unload.IsCompleted);
                Assert.Equal(group.Id, vm.ActiveDashboardId);
                LogTabViewModel? selected = null;
                if (navigateAway)
                {
                    await vm.HandleDashboardGroupInvokedAsync(vm.Groups[1]).WaitAsync(TimeSpan.FromSeconds(5));
                    selected = vm.SelectedTab;
                    Assert.NotNull(selected);
                }
                files.Release();
                await Task.WhenAll(load, unload).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.DoesNotContain(vm.Tabs, tab => tab.ScopeDashboardId == group.Id);
                Assert.Equal(navigateAway ? vm.Groups[1].Id : null, vm.ActiveDashboardId);
                Assert.Same(selected, vm.SelectedTab);
            }
            finally { files.Release(); }
        });
    }

    [Fact]
    public async Task SharedFile_UnloadingOneSubscriberPreservesTheOtherIndex()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync(sharedFile: true);
            var vm = fixture.ViewModel;
            var first = vm.HandleDashboardGroupInvokedAsync(vm.Groups[0]);
            await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = vm.HandleDashboardGroupInvokedAsync(vm.Groups[1]);
            Assert.Equal(vm.Groups[1].Id, vm.ActiveDashboardId);
            await vm.UnloadDashboardAsync(vm.Groups[0]).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(fixture.Reader.BlockedBuildDisposed.Task.IsCompleted);
            fixture.Reader.Release();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, fixture.Reader.BuildCounts[fixture.LargePath]);
            Assert.True(vm.SelectedTab!.IsIndexReady);
            Assert.Single(vm.Tabs);
        });
    }

    [Fact]
    public async Task RowCues_DoNotReplayOrShiftLayout_AndRespectAnimationPreferences()
    {
        await WpfTestHost.RunAsync(() =>
        {
            var group = new LogGroupViewModel(new LogGroup { Name = "Fixture" }, _ => Task.CompletedTask);
            var highlight = new Border { Opacity = 0, Background = Brushes.Blue };
            var cue = new DashboardLoadIndicator { DataContext = group, HighlightTarget = highlight, AnimationPreferenceOverride = true };
            var grid = new Grid();
            grid.Children.Add(highlight);
            grid.Children.Add(cue);
            var window = ShowWindow(grid);
            window.UpdateLayout();
            var width = cue.ActualWidth;
            group.IsDashboardLoading = true;
            Assert.True(cue.IsSpinnerAnimating);
            group.IsDashboardLoading = false;
            group.DashboardReadyRevision = 1;
            Assert.Equal(1, cue.CompletionAnimationCount);
            Assert.Equal(width, cue.ActualWidth);
            cue.DataContext = null;
            cue.DataContext = group;
            Assert.Equal(1, cue.CompletionAnimationCount);
            cue.AnimationPreferenceOverride = false;
            group.IsDashboardLoading = true;
            Assert.False(cue.IsSpinnerAnimating);
            group.DashboardReadyRevision = 2;
            Assert.Equal(1, cue.CompletionAnimationCount);
            window.Close();
            Assert.False(cue.IsSpinnerAnimating);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task RapidThreeDashboardSwitches_DoNotRestartOrStealSelection()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync();
            var vm = fixture.ViewModel;
            var first = vm.HandleDashboardGroupInvokedAsync(vm.Groups[0]);
            await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = vm.HandleDashboardGroupInvokedAsync(vm.Groups[1]);
            var third = vm.HandleDashboardGroupInvokedAsync(vm.Groups[2]);
            Assert.Equal(vm.Groups[2].Id, vm.ActiveDashboardId);
            await Task.WhenAll(second, third).WaitAsync(TimeSpan.FromSeconds(5));
            var selected = vm.SelectedTab;
            fixture.Reader.Release();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(selected, vm.SelectedTab);
            Assert.Equal(1, fixture.Reader.BuildCounts[fixture.LargePath]);
            Assert.Empty(vm.Tabs.Single(tab => tab.ScopeDashboardId == vm.Groups[0].Id).VisibleLines);
        });
    }

    [Fact]
    public async Task Shutdown_DrainsPendingWorkAndOwnedIndexResources()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync();
            var vm = fixture.ViewModel;
            var load = vm.HandleDashboardGroupInvokedAsync(vm.Groups[0]);
            await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            vm.Dispose();
            await vm.ShutdownTask.WaitAsync(TimeSpan.FromSeconds(5));
            await load.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(fixture.Reader.BlockedBuildDisposed.Task.IsCompletedSuccessfully);
            Assert.False(vm.IsDashboardLoading);
            Assert.False(vm.Groups[0].IsDashboardLoading);
            Assert.Equal(0, vm.Groups[0].DashboardReadyRevision);
        });
    }

    [Fact]
    public async Task AllFailedDashboard_SettlesWithoutCompletionFade()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync(includeMissing: true, allMissing: true);
            var vm = fixture.ViewModel;
            var load = vm.HandleDashboardGroupInvokedAsync(vm.Groups[0]);
            var other = vm.HandleDashboardGroupInvokedAsync(vm.Groups[1]);
            await Task.WhenAll(load, other).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, vm.Groups[0].DashboardReadyRevision);
            Assert.Equal(DashboardLoadPhase.Failed, vm.Groups[0].LoadProgress.Phase);
            Assert.False(vm.Groups[0].IsDashboardLoading);
        });
    }

    [Fact]
    public async Task ReopenedFilteredTab_WithholdsRawPreviewUntilRestoration()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync();
            var vm = fixture.ViewModel;
            fixture.Reader.Release();
            await vm.HandleDashboardGroupInvokedAsync(vm.Groups[0]);
            var tab = vm.SelectedTab!;
            await tab.ApplyFilterAsync([1], "One filtered line");
            var session = tab.ActiveSession;
            await vm.CloseTabCommand.ExecuteAsync(tab);
            await session.ResetLineIndexAsync();
            fixture.Reader.ResetGate();
            var reopened = vm.HandleDashboardGroupInvokedAsync(vm.Groups[0]);
            await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var replacement = vm.SelectedTab!;
            Assert.False(replacement.HasPreview);
            Assert.Contains("filtered content", replacement.PreviewStatusText);
            fixture.Reader.Release();
            await reopened.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(replacement.IsIndexReady);
            Assert.True(replacement.IsFilterActive);
        });
    }

    [Fact]
    public async Task LatePreviewArrival_PositionsTheOpenViewAndRetainsManualScrolling()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync();
            fixture.Reader.HoldPreview();
            var vm = fixture.ViewModel;
            vm.GlobalAutoScrollEnabled = true;
            var load = vm.HandleDashboardGroupInvokedAsync(vm.Groups[0]);
            await fixture.Reader.PreviewStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var view = new LogViewportView { DataContext = vm };
            var window = ShowWindow(view);
            Assert.True(vm.SelectedTab!.IsWaitingForContent);
            fixture.Reader.ReleasePreview();
            await WaitForPreviewAsync(vm.SelectedTab);
            await view.Dispatcher.InvokeAsync(() => window.UpdateLayout(), System.Windows.Threading.DispatcherPriority.ContextIdle);
            var list = Find<ListBox>(view, "PreviewListBox")!;
            var scroll = Find<ScrollViewer>(list, "PART_ScrollViewer")!;
            Assert.True(scroll.VerticalOffset > 100);
            scroll.ScrollToVerticalOffset(20);
            window.UpdateLayout();
            Assert.False(vm.GlobalAutoScrollEnabled);
            Assert.Equal(vm.SelectedTab.PreviewLines[20].ByteOffset, vm.SelectedTab.PreviewAnchor);
            fixture.Reader.Release();
            await load.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("row 11820", vm.SelectedTab.VisibleLines[0].Text);
            window.Close();
        });
    }

    [Fact]
    public async Task MemberInvocation_SelectsThePlaceholderWithoutWaitingForItsIndex()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync();
            var vm = fixture.ViewModel;
            var group = vm.Groups[0];
            await vm.OpenDashboardMemberFileAsync(group, group.MemberFiles[0]).WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var tab = vm.SelectedTab!;
            await WaitForPreviewAsync(tab);
            Assert.False(tab.IsIndexReady);
            var oldPreviewRow = tab.PreviewLines[0];
            await vm.HandleDashboardGroupInvokedAsync(vm.Groups[1]);
            Assert.Empty(tab.PreviewLines);
            await vm.OpenDashboardMemberFileAsync(group, group.MemberFiles[0]).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(tab, vm.SelectedTab);
            await WaitForPreviewAsync(tab);
            Assert.Throws<IOException>(() => tab.GetPreviewCopyText([oldPreviewRow]));
            Assert.Equal(1, fixture.Reader.BuildCounts[fixture.LargePath]);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementWithNewEncoding_DiscardsPreviewAnchorsAndIndexesTheCurrentGeneration(bool overwriteInPlace)
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync(useEncodingDetection: true);
            var vm = fixture.ViewModel;
            var load = vm.HandleDashboardGroupInvokedAsync(vm.Groups[0]);
            await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var tab = vm.SelectedTab!;
            await WaitForPreviewAsync(tab);
            tab.RememberPreviewPosition(tab.PreviewLines[10].ByteOffset, [tab.PreviewLines[11].ByteOffset]);
            if (!overwriteInPlace) File.Move(fixture.LargePath, fixture.LargePath + ".old");
            await File.WriteAllTextAsync(fixture.LargePath, "new café\r\n" + new string('z', 100000), Encoding.Unicode);
            fixture.Reader.Release();
            await load.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(tab.IsIndexReady);
            Assert.Equal(FileEncoding.Utf16, tab.EffectiveEncoding);
            Assert.Equal(2, tab.TotalLines);
            Assert.Equal("new café", tab.VisibleLines[0].Text);
            Assert.Empty(tab.SelectedLineNumbers);
            Assert.Null(tab.PreviewAnchor);
        });
    }

    [Fact]
    public async Task RevisitedPreview_RejectsPositionAndSelectionFromAReplacedFile()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync();
            var vm = fixture.ViewModel;
            var load = vm.HandleDashboardGroupInvokedAsync(vm.Groups[0]);
            await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var tab = vm.SelectedTab!;
            await WaitForPreviewAsync(tab);
            var oldRow = tab.PreviewLines[10];
            tab.RememberPreviewPosition(oldRow.ByteOffset, [oldRow.ByteOffset]);
            await vm.HandleDashboardGroupInvokedAsync(vm.Groups[1]);
            File.Move(fixture.LargePath, fixture.LargePath + ".old");
            await File.WriteAllTextAsync(fixture.LargePath, "new\ncurrent");
            await vm.OpenDashboardMemberFileAsync(vm.Groups[0], vm.Groups[0].MemberFiles[0]);
            await WaitForPreviewAsync(tab);
            Assert.Null(tab.PreviewAnchor);
            Assert.Empty(tab.PreviewSelectedOffsets);
            Assert.Equal("new", tab.PreviewLines[0].Text);
            fixture.Reader.Release();
            await load.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(tab.SelectedLineNumbers);
        });
    }

    [Fact]
    public async Task TruncatedPreviewCopy_IncludesTheDisplayedMarkerWithoutRereadingTheFile()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync();
            await File.AppendAllTextAsync(fixture.LargePath, new string('z', 6000));
            var load = fixture.ViewModel.HandleDashboardGroupInvokedAsync(fixture.ViewModel.Groups[0]);
            await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var tab = fixture.ViewModel.SelectedTab!;
            await WaitForPreviewAsync(tab);
            var line = tab.PreviewLines[^1];
            Assert.True(line.IsTruncated);
            File.Move(fixture.LargePath, fixture.LargePath + ".old");
            Assert.Equal(line.Text + " …", tab.GetPreviewCopyText([line]));
            await File.WriteAllTextAsync(fixture.LargePath, "replacement");
            fixture.Reader.Release();
            await load.WaitAsync(TimeSpan.FromSeconds(5));
        });
    }

    [Fact]
    public async Task Shutdown_WaitsForTheCurrentPreviewReadToReleaseItsHandleAndPermit()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync();
            fixture.Reader.HoldPreview(ignoreCancellation: true);
            var vm = fixture.ViewModel;
            var load = vm.HandleDashboardGroupInvokedAsync(vm.Groups[0]);
            await fixture.Reader.PreviewStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            vm.Dispose();
            Assert.False(vm.ShutdownTask.IsCompleted);
            fixture.Reader.ReleasePreview();
            await vm.ShutdownTask.WaitAsync(TimeSpan.FromSeconds(5));
            await load.WaitAsync(TimeSpan.FromSeconds(5));
        });
    }

    [Fact]
    public async Task DashboardRows_RenderBothCuesInBothThemesWithoutChangingDimensions()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync();
            var vm = fixture.ViewModel;
            var view = new DashboardTreeView { DataContext = vm };
            var window = ShowWindow(view);
            var cue = Find<DashboardLoadIndicator>(view, string.Empty)!;
            Assert.NotNull(cue);
            Assert.NotNull(cue.HighlightTarget);
            cue.AnimationPreferenceOverride = true;
            var size = cue.RenderSize;
            var group = (LogGroupViewModel)cue.DataContext;
            group.IsDashboardLoading = true;
            window.UpdateLayout();
            Assert.True(cue.IsSpinnerAnimating);
            Assert.Equal(size, cue.RenderSize);
            SaveRender(window, "dashboard-loading-light.png");
            var appearance = new WpfLogAppearanceService();
            appearance.Apply(new AppSettings { IsDarkMode = true });
            SaveRender(window, "dashboard-loading-dark.png");
            group.IsDashboardLoading = false;
            group.DashboardReadyRevision = 1;
            Assert.Equal(1, cue.CompletionAnimationCount);
            await Task.Delay(60); // Capture a live animation frame in the isolated render fixture.
            SaveRender(window, "dashboard-ready-dark.png");
            appearance.Apply(new AppSettings());
            SaveRender(window, "dashboard-ready-light.png");
            Assert.Equal(size, cue.RenderSize);
            window.Close();
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TreeMouseInput_SwitchesDashboardsAndSelectsMembersWhileLoading(bool moveBelowThreshold)
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync();
            var vm = fixture.ViewModel;
            var load = vm.HandleDashboardGroupInvokedAsync(vm.Groups[0]);
            await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pendingTab = vm.SelectedTab;
            var view = new DashboardTreeView { DataContext = vm };
            var window = ShowWindow(view);
            var nextRow = Find<Grid>(view, "GroupRowGrid", vm.Groups[1])!;
            Assert.NotNull(nextRow);
            nextRow.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
            Assert.True(view.HasPendingGroupRowGesture);
            if (moveBelowThreshold)
            {
                Assert.Null(view.ProcessGroupRowMouseMove(Mouse.GetPosition(view) + new Vector(.5, .5), MouseButtonState.Pressed));
                Assert.True(view.HasPendingGroupRowGesture);
            }
            nextRow.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            Assert.Equal(vm.Groups[1].Id, vm.ActiveDashboardId);
            await vm.HandleDashboardGroupInvokedAsync(vm.Groups[1]).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(load.IsCompleted);
            vm.Groups[0].IsExpanded = true;
            window.UpdateLayout();
            var member = Find<DashboardMemberRowPanel>(view, string.Empty, vm.Groups[0].MemberFiles[0])!;
            Assert.NotNull(member);
            member.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            Assert.Equal(vm.Groups[0].Id, vm.ActiveDashboardId);
            Assert.Same(pendingTab, vm.SelectedTab);
            fixture.Reader.Release();
            await load.WaitAsync(TimeSpan.FromSeconds(5));
            window.Close();
        });
    }

    [Fact]
    public async Task TreeDragThreshold_CancelsLoadingGesturesAndPreservesOrdinaryDragging()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync();
            var vm = fixture.ViewModel;
            var load = vm.HandleDashboardGroupInvokedAsync(vm.Groups[0]);
            await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var view = new DashboardTreeView { DataContext = vm };
            var window = ShowWindow(view);
            var row = Find<Grid>(view, "GroupRowGrid", vm.Groups[1])!;
            Assert.NotNull(row);
            var dragPosition = new Point(SystemParameters.MinimumHorizontalDragDistance + 1, 0);
            view.SetPendingGroupRowGestureForTests(vm.Groups[1]);
            Assert.Null(view.ProcessGroupRowMouseMove(new Point(1, 0), MouseButtonState.Pressed));
            Assert.True(view.HasPendingGroupRowGesture);
            Assert.Null(view.ProcessGroupRowMouseMove(dragPosition, MouseButtonState.Pressed));
            Assert.False(view.HasPendingGroupRowGesture);
            row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            Assert.Equal(vm.Groups[0].Id, vm.ActiveDashboardId);

            fixture.Reader.Release();
            await load.WaitAsync(TimeSpan.FromSeconds(5));
            view.SetPendingGroupRowGestureForTests(vm.Groups[1]);
            Assert.Same(vm.Groups[1], view.ProcessGroupRowMouseMove(dragPosition, MouseButtonState.Pressed));
            Assert.False(view.HasPendingGroupRowGesture);
            Assert.Null(view.ProcessGroupRowMouseMove(dragPosition, MouseButtonState.Pressed));
            view.SetPendingGroupRowGestureForTests(vm.Groups[1]);
            Assert.Null(view.ProcessGroupRowMouseMove(new Point(1, 0), MouseButtonState.Released));
            Assert.False(view.HasPendingGroupRowGesture);
            window.Close();
        });
    }

    [Fact]
    public async Task DeferredIndexedNavigation_DoesNotOverwriteANewerUserRange()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync();
            fixture.Reader.Release();
            var vm = fixture.ViewModel;
            await vm.HandleDashboardGroupInvokedAsync(vm.Groups[0]);
            var tab = vm.SelectedTab!;
            var view = new LogViewportView { DataContext = vm };
            var window = ShowWindow(view);
            await tab.LoadViewportAsync(0, tab.ViewportLineCount);
            await WpfTestHost.FlushAsync();
            tab.SetNavigateTargetLine(10);
            tab.SelectSingleLine(10);
            tab.SelectRangeTo(12, false);
            await WpfTestHost.FlushAsync();
            Assert.Equal(new[] { 10, 11, 12 }, tab.SelectedLineNumbers);
            window.Close();
        });
    }

    [Fact]
    public async Task CompletedDashboardNavigation_ReusesItsCycleAndMaterializesHiddenContent()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync();
            var vm = fixture.ViewModel;
            var first = vm.HandleDashboardGroupInvokedAsync(vm.Groups[0]);
            await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await vm.HandleDashboardGroupInvokedAsync(vm.Groups[1]);
            fixture.Reader.Release();
            await first;
            var revision = vm.Groups[0].LoadProgress.Revision;
            var completion = vm.Groups[0].DashboardReadyRevision;
            await vm.HandleDashboardGroupInvokedAsync(vm.Groups[0]);
            await WaitForVisibleLinesAsync(vm.SelectedTab!);
            Assert.Equal(revision, vm.Groups[0].LoadProgress.Revision);
            Assert.Equal(completion, vm.Groups[0].DashboardReadyRevision);
            Assert.NotEmpty(vm.SelectedTab!.VisibleLines);
            Assert.Equal(1, fixture.Reader.BuildCounts[fixture.LargePath]);
        });
    }

    [Fact]
    public async Task ReloadDuringIndexing_StartsAFreshCycleAndPreservesAnotherDashboard()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync();
            var vm = fixture.ViewModel;
            var first = vm.HandleDashboardGroupInvokedAsync(vm.Groups[0]);
            await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var oldTab = vm.SelectedTab!;
            var oldRevision = vm.Groups[0].LoadProgress.Revision;
            await vm.HandleDashboardGroupInvokedAsync(vm.Groups[1]);
            var otherTab = vm.SelectedTab;
            fixture.Reader.ResetGate();
            var reload = vm.ReloadDashboardAsync(vm.Groups[0]);
            await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(vm.Groups[0].LoadProgress.Revision > oldRevision);
            Assert.DoesNotContain(oldTab, vm.Tabs);
            Assert.Contains(otherTab!, vm.Tabs);
            fixture.Reader.Release();
            await Task.WhenAll(first, reload).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(vm.Tabs.Single(tab => tab.ScopeDashboardId == vm.Groups[0].Id).IsIndexReady);
            Assert.Equal(2, fixture.Reader.BuildCounts[fixture.LargePath]);
        });
    }

    [Fact]
    public async Task ProductionShutdownCoordinator_WaitsForNativeReadBeforeDispatcherExit()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync();
            fixture.Reader.HoldPreview(ignoreCancellation: true);
            var load = fixture.ViewModel.HandleDashboardGroupInvokedAsync(fixture.ViewModel.Groups[0]);
            await fixture.Reader.PreviewStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var coordinator = new LogReader.App.AppShutdownCoordinator(() => fixture.ViewModel, () => fixture.Tail, () => { });
            var drain = coordinator.DrainAsync();
            Assert.False(drain.IsCompleted);
            fixture.Reader.ReleasePreview();
            await drain.WaitAsync(TimeSpan.FromSeconds(5));
            await load.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(fixture.ViewModel.ShutdownTask.IsCompletedSuccessfully);
            coordinator.Complete();
        });
    }

    private async Task<Fixture> CreateAsync(bool includeMissing = false, bool sharedFile = false, bool allMissing = false,
        bool useEncodingDetection = false, bool includeSmallInFirstDashboard = false, ISearchService? searchService = null,
        ILogFileRepository? fileRepository = null, ILogGroupRepository? groupRepository = null,
        IMessageBoxService? messageBoxService = null)
    {
        var large = Path.Combine(_root, "large.log");
        var small = Path.Combine(_root, "small.log");
        await File.WriteAllTextAsync(large, string.Concat(Enumerable.Range(0, 12000).Select(i => $"row {i}\n")));
        await File.WriteAllTextAsync(small, "ready\nsmall");
        var files = fileRepository ?? new StubLogFileRepository();
        var entryA = new LogFileEntry { FilePath = large };
        var entryB = new LogFileEntry { FilePath = sharedFile ? large : small };
        await files.AddAsync(entryA);
        if (!sharedFile) await files.AddAsync(entryB);
        var groupA = new LogGroup { Name = "A", FileIds = [entryA.Id] };
        if (includeSmallInFirstDashboard)
            groupA.FileIds.Add(entryB.Id);
        if (includeMissing)
        {
            var missing = new LogFileEntry { FilePath = Path.Combine(_root, "missing.log") };
            await files.AddAsync(missing);
            groupA.FileIds.Add(missing.Id);
            if (allMissing) groupA.FileIds.Remove(entryA.Id);
        }
        var groups = groupRepository ?? new StubLogGroupRepository();
        await groups.AddAsync(groupA);
        await groups.AddAsync(new LogGroup { Name = "B", FileIds = [sharedFile ? entryA.Id : entryB.Id] });
        await groups.AddAsync(new LogGroup { Name = "C", FileIds = [sharedFile ? entryA.Id : entryB.Id] });
        var reader = new GatedInteractiveReader(large);
        var tail = new StubFileTailService();
        var resources = new ResourceSnapshot();
        var vm = new MainViewModel(files, groups, new StubSettingsRepository(), reader, searchService ?? new StubSearchService(), tail,
            useEncodingDetection ? new FileEncodingDetectionService() : new StubEncodingDetectionService(),
            enableLifecycleTimer: false, fileDialogService: ForbiddenUiService.Instance,
            messageBoxService: messageBoxService ?? ForbiddenUiService.Instance, settingsDialogService: ForbiddenUiService.Instance,
            bulkOpenPathsDialogService: ForbiddenUiService.Instance);
        await vm.InitializeAsync();
        return new Fixture(vm, reader, tail, large, resources);
    }

    private static async Task WaitForPreviewAsync(LogTabViewModel tab)
    {
        if (tab.HasPreview) return;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(LogTabViewModel.HasPreview) && tab.HasPreview) ready.TrySetResult();
        }
        tab.PropertyChanged += Changed;
        try
        {
            if (tab.HasPreview) ready.TrySetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { tab.PropertyChanged -= Changed; }
    }

    private static async Task WaitForVisibleLinesAsync(LogTabViewModel tab)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, NotifyCollectionChangedEventArgs args)
        {
            if (tab.VisibleLines.Count > 0) ready.TrySetResult();
        }
        tab.VisibleLines.CollectionChanged += Changed;
        try
        {
            if (tab.VisibleLines.Count > 0) ready.TrySetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { tab.VisibleLines.CollectionChanged -= Changed; }
    }

    private static Window ShowWindow(UIElement content)
    {
        var window = new Window { Style = new Style(typeof(Window)), Content = content, Width = 800, Height = 600, Left = -32000, Top = -32000,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        window.Show();
        return window;
    }

    private static T? Find<T>(DependencyObject root, string name, object? dataContext = null) where T : FrameworkElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T element && element.Name == name && (dataContext == null || ReferenceEquals(element.DataContext, dataContext)))
                return element;
            if (Find<T>(child, name, dataContext) is { } found) return found;
        }
        return null;
    }

    private static void SaveRender(Window window, string name)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var directory = Path.Combine(root, ".agent", "reviews", "dashboard-loading-responsiveness");
        Directory.CreateDirectory(directory);
        window.UpdateLayout();
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(Path.Combine(directory, name));
        encoder.Save(output);
    }

    private sealed record Fixture(MainViewModel ViewModel, GatedInteractiveReader Reader, StubFileTailService Tail,
        string LargePath, ResourceSnapshot Resources) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Reader.Release();
            Reader.ReleasePreview();
            ViewModel.Dispose();
            try { await ViewModel.ShutdownTask; }
            finally
            {
                Tail.Dispose();
                Resources.Dispose();
            }
        }
    }

    private sealed class ResourceSnapshot : IDisposable
    {
        private readonly ResourceDictionary _resources = Application.Current.Resources;
        private readonly Dictionary<object, object> _values;

        public ResourceSnapshot() => _values = _resources.Keys.Cast<object>().Where(IsAppearanceResource)
            .ToDictionary(key => key, key => _resources[key]);

        private static bool IsAppearanceResource(object key) => key is ResourceKey ||
            key is string name && (name.EndsWith("Resource", StringComparison.Ordinal) || name.EndsWith("Brush", StringComparison.Ordinal));

        public void Dispose()
        {
            foreach (var key in _resources.Keys.Cast<object>().Where(IsAppearanceResource).ToArray())
                if (!_values.ContainsKey(key)) _resources.Remove(key);
            foreach (var (key, value) in _values)
                if (!ReferenceEquals(_resources[key], value)) _resources[key] = value;
        }
    }

    private sealed class GatedTargetRepository : StubLogFileRepository, ILogFileRepository
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? HeldFileId { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int HeldRequestCount { get; private set; }
        public int OpenRequestCount { get; private set; }
        public void Release() => _release.TrySetResult();

        Task<LogFileEntry> ILogFileRepository.GetOrCreateByPathAsync(string filePath, DateTime? lastOpenedAtUtc)
        {
            OpenRequestCount++;
            return GetOrCreateByPathAsync(filePath, lastOpenedAtUtc);
        }

        async Task<IReadOnlyDictionary<string, LogFileEntry>> ILogFileRepository.GetByIdsAsync(IEnumerable<string> ids)
        {
            var requested = ids.ToArray();
            if (HeldFileId != null && requested.Contains(HeldFileId))
            {
                HeldRequestCount++;
                Started.TrySetResult();
                await _release.Task;
            }
            return await GetByIdsAsync(requested);
        }
    }

    private sealed class GatedInteractiveReader : ILogReaderService, IInteractiveLogReaderService
    {
        private readonly ChunkedLogReaderService _reader = new();
        private readonly string _blockedPath;
        private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? _previewRelease;
        private bool _ignorePreviewCancellation;
        public Exception? PreviewFailure { get; set; }
        public TaskCompletionSource PreviewStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BlockedBuildDisposed { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentDictionary<string, int> BuildCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, int> ViewportReadCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public GatedInteractiveReader(string path) => _blockedPath = path;
        public void Release() => _release.TrySetResult();
        public void HoldPreview(bool ignoreCancellation = false)
        {
            _previewRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _ignorePreviewCancellation = ignoreCancellation;
        }
        public void ReleasePreview() => _previewRelease?.TrySetResult();
        public void ResetGate()
        {
            _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            BlockedBuildDisposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        public async Task<IInteractiveIndexBuild> CreateIndexBuildAsync(string path, FileEncoding encoding, CancellationToken ct)
        {
            BuildCounts.AddOrUpdate(path, 1, (_, count) => count + 1);
            var build = await ((IInteractiveLogReaderService)_reader).CreateIndexBuildAsync(path, encoding, ct);
            return string.Equals(path, _blockedPath, StringComparison.OrdinalIgnoreCase) ? new GatedBuild(build, this) : build;
        }
        public async Task<RecentLogPreview> ReadRecentPreviewAsync(string path, FileEncoding encoding, CancellationToken ct)
        {
            PreviewStarted.TrySetResult();
            if (_previewRelease != null)
                await (_ignorePreviewCancellation ? _previewRelease.Task : _previewRelease.Task.WaitAsync(ct));
            if (PreviewFailure != null)
                throw PreviewFailure;
            return await ((IInteractiveLogReaderService)_reader).ReadRecentPreviewAsync(path, encoding, ct);
        }
        public Task<LineIndex> BuildIndexAsync(string path, FileEncoding encoding, CancellationToken ct = default) => _reader.BuildIndexAsync(path, encoding, ct);
        public Task<LineIndex> UpdateIndexAsync(string path, LineIndex index, FileEncoding encoding, CancellationToken ct = default) => _reader.UpdateIndexAsync(path, index, encoding, ct);
        public Task<IReadOnlyList<string>> ReadLinesAsync(string path, LineIndex index, int start, int count, FileEncoding encoding, CancellationToken ct = default)
        {
            ViewportReadCounts.AddOrUpdate(path, 1, (_, reads) => reads + 1);
            return _reader.ReadLinesAsync(path, index, start, count, encoding, ct);
        }
        public Task<string> ReadLineAsync(string path, LineIndex index, int line, FileEncoding encoding, CancellationToken ct = default)
            => _reader.ReadLineAsync(path, index, line, encoding, ct);
        private sealed class GatedBuild(IInteractiveIndexBuild build, GatedInteractiveReader owner) : IInteractiveIndexBuild
        {
            public InteractiveIndexProgress Progress => build.Progress;
            public bool CanResume => build.CanResume;
            public async ValueTask<bool> AdvanceAsync(int bytes, Func<bool> shouldYield, CancellationToken ct)
            {
                owner.Started.TrySetResult();
                await owner._release.Task.WaitAsync(ct);
                return await build.AdvanceAsync(bytes, shouldYield, ct);
            }
            public ValueTask SuspendAsync() => build.SuspendAsync();
            public LineIndex TakeCompletedIndex() => build.TakeCompletedIndex();
            public async ValueTask DisposeAsync()
            {
                await build.DisposeAsync();
                owner.BlockedBuildDisposed.TrySetResult();
            }
        }
    }

    public void Dispose()
    {
        _scope.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}
