namespace LogReader.App.Views;

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using LogReader.App.ViewModels;

public partial class MainWindow : Window
{
    private const double CollapsedRailWidth = 36;
    private LogTabViewModel? _subscribedTab;
    private MainViewModel? _subscribedViewModel;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyPanelLayout();
        DataContextChanged += (_, _) =>
        {
            if (_subscribedViewModel != null)
                _subscribedViewModel.PropertyChanged -= ViewModel_PropertyChanged;

            _subscribedViewModel = ViewModel;
            if (_subscribedViewModel != null)
                _subscribedViewModel.PropertyChanged += ViewModel_PropertyChanged;

            ApplyPanelLayout();
        };
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsGroupsPanelOpen) ||
            e.PropertyName == nameof(MainViewModel.IsSearchPanelOpen) ||
            e.PropertyName == nameof(MainViewModel.GroupsPanelWidth) ||
            e.PropertyName == nameof(MainViewModel.SearchPanelWidth))
        {
            ApplyPanelLayout();
            if (e.PropertyName == nameof(MainViewModel.IsSearchPanelOpen) && ViewModel?.IsSearchPanelOpen == true)
            {
                Dispatcher.InvokeAsync(() => SearchBox.Focus(),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        if (e.PropertyName == nameof(MainViewModel.SelectedTab))
        {
            // Unsubscribe from old tab
            if (_subscribedTab != null)
                _subscribedTab.PropertyChanged -= Tab_PropertyChanged;

            _subscribedTab = ViewModel?.SelectedTab;

            if (_subscribedTab != null)
                _subscribedTab.PropertyChanged += Tab_PropertyChanged;

            // Recalculate viewport for the newly-visible tab in case the window was
            // resized while a different tab was active (the ListBox SizeChanged doesn't
            // fire for off-screen tabs).
            Dispatcher.InvokeAsync(RefreshViewportForSelectedTab,
                System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void RefreshViewportForSelectedTab()
    {
        var tab = ViewModel?.SelectedTab;
        if (tab == null) return;

        var listBox = FindVisualChild<ListBox>(TabControl, "LogListBox");
        if (listBox == null || listBox.DataContext != tab) return;

        tab.UpdateViewportLineCount(MeasureViewportLineCount(listBox));
    }

    private void Tab_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogTabViewModel.NavigateToLineNumber) &&
            sender is LogTabViewModel tab && tab.NavigateToLineNumber > 0)
        {
            var lineNumber = tab.NavigateToLineNumber;
            // Background priority runs after all data-binding, render, and layout passes
            Dispatcher.InvokeAsync(() => ScrollToLine(lineNumber),
                System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void ScrollToLine(int lineNumber)
    {
        var listBox = FindVisualChild<ListBox>(TabControl, "LogListBox");
        if (listBox == null) return;

        var item = listBox.Items.Cast<LogLineViewModel>().FirstOrDefault(l => l.LineNumber == lineNumber);
        if (item == null) return;

        listBox.SelectedItem = item;

        var scrollViewer = FindVisualChild<ScrollViewer>(listBox);
        if (scrollViewer == null) return;

        // Refresh the viewport count now that layout is complete (items are rendered).
        if (listBox.DataContext is LogTabViewModel tab)
            tab.UpdateViewportLineCount(MeasureViewportLineCount(listBox));

        // With CanContentScroll=True, VerticalOffset and ViewportHeight are in item-count units.
        var itemIndex = listBox.Items.IndexOf(item);
        var vh = (int)scrollViewer.ViewportHeight;
        var targetOffset = Math.Max(0, itemIndex - vh / 2);
        // Clamp so we never scroll past the last item — prevents blank rows at the bottom.
        targetOffset = Math.Min(targetOffset, Math.Max(0, listBox.Items.Count - vh));
        scrollViewer.ScrollToVerticalOffset(targetOffset);
    }

    private static T? FindVisualChild<T>(DependencyObject parent, string? name = null) where T : FrameworkElement
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T fe && (name == null || fe.Name == name))
                return fe;
            var result = FindVisualChild<T>(child, name);
            if (result != null) return result;
        }
        return null;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private async void OnFileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && ViewModel != null)
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
            foreach (var file in files)
            {
                await ViewModel.OpenFilePathAsync(file);
            }
        }
    }

    private async void OpenSettings(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        await ViewModel.OpenSettingsAsync(this);
    }

    private void ApplyPanelLayout()
    {
        if (ViewModel == null)
            return;

        var groupsOpen = ViewModel.IsGroupsPanelOpen;
        var searchOpen = ViewModel.IsSearchPanelOpen;

        GroupsPanelColumn.Width = new GridLength(groupsOpen ? ViewModel.GroupsPanelWidth : CollapsedRailWidth, GridUnitType.Pixel);
        SearchPanelColumn.Width = new GridLength(searchOpen ? ViewModel.SearchPanelWidth : CollapsedRailWidth, GridUnitType.Pixel);

        GroupsPanelContent.Visibility = groupsOpen ? Visibility.Visible : Visibility.Collapsed;
        GroupsPanelRailButton.Visibility = groupsOpen ? Visibility.Collapsed : Visibility.Visible;

        SearchPanelContent.Visibility = searchOpen ? Visibility.Visible : Visibility.Collapsed;
        SearchPanelRailButton.Visibility = searchOpen ? Visibility.Collapsed : Visibility.Visible;
    }

    private void GroupsSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (ViewModel == null)
            return;

        if (GroupsPanelColumn.ActualWidth <= CollapsedRailWidth + 0.5)
        {
            if (ViewModel.IsGroupsPanelOpen)
                ViewModel.ToggleGroupsPanelCommand.Execute(null);
            return;
        }

        ViewModel.RememberGroupsPanelWidth(GroupsPanelColumn.ActualWidth);
    }

    private void SearchSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (ViewModel == null)
            return;

        if (SearchPanelColumn.ActualWidth <= CollapsedRailWidth + 0.5)
        {
            if (ViewModel.IsSearchPanelOpen)
                ViewModel.ToggleSearchPanelCommand.Execute(null);
            return;
        }

        ViewModel.RememberSearchPanelWidth(SearchPanelColumn.ActualWidth);
    }

    private void ShowControls_Click(object sender, RoutedEventArgs e)
    {
        var controlsWindow = new ControlsWindow
        {
            Owner = this
        };

        controlsWindow.ShowDialog();
    }

    private void GroupsSplitter_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel?.IsGroupsPanelOpen == true)
            ViewModel.ToggleGroupsPanelCommand.Execute(null);
    }

    private void SearchSplitter_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel?.IsSearchPanelOpen == true)
            ViewModel.ToggleSearchPanelCommand.Execute(null);
    }

    // ── Group panel handlers ──────────────────────────────────────────────────

    private async void GroupRow_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Ignore if clicking inside a button or textbox
        var obj = e.OriginalSource as DependencyObject;
        while (obj != null)
        {
            if (obj is Button || obj is TextBox) return;
            if (obj == sender) break;
            obj = System.Windows.Media.VisualTreeHelper.GetParent(obj);
        }

        if (e.ClickCount == 1 && sender is FrameworkElement el && el.DataContext is LogGroupViewModel group)
        {
            if (ViewModel != null)
            {
                bool isCtrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
                if (group.Kind == LogReader.Core.Models.LogGroupKind.Dashboard)
                    await ViewModel.OpenGroupFilesAsync(group);
                ViewModel.ToggleGroupSelection(group, isCtrl);
            }
        }
    }

    private void GroupExpand_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is LogGroupViewModel group)
            group.IsExpanded = !group.IsExpanded;
        e.Handled = true; // prevent bubbling to GroupRow_MouseDown
    }

    private void GroupName_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement el && el.DataContext is LogGroupViewModel group)
        {
            group.BeginEdit();
            e.Handled = true;
        }
    }

    private async void GroupNameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is LogGroupViewModel group)
        {
            if (e.Key == Key.Return)
            {
                await group.CommitEditAsync();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                group.CancelEdit();
                e.Handled = true;
            }
        }
    }

    private async void GroupNameTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is LogGroupViewModel group && group.IsEditing)
            await group.CommitEditAsync();
    }

    private void GroupNameTextBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox tb && (bool)e.NewValue)
        {
            tb.Focus();
            tb.SelectAll();
        }
    }

    private async void MoveGroupUp_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement el && el.DataContext is LogGroupViewModel group)
            await ViewModel!.MoveGroupUpAsync(group);
    }

    private async void MoveGroupDown_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement el && el.DataContext is LogGroupViewModel group)
            await ViewModel!.MoveGroupDownAsync(group);
    }

    private void AddChildFolder_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement el && el.DataContext is LogGroupViewModel group && ViewModel != null)
        {
            _ = ViewModel.CreateChildGroupAsync(group, LogReader.Core.Models.LogGroupKind.Branch);
        }
    }

    private void AddChildDashboard_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement el && el.DataContext is LogGroupViewModel group && ViewModel != null)
        {
            _ = ViewModel.CreateChildGroupAsync(group, LogReader.Core.Models.LogGroupKind.Dashboard);
        }
    }

    private async void ManageGroup_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement el && el.DataContext is LogGroupViewModel group)
            await ViewModel!.AddFilesToDashboardAsync(group, this);
    }

    private async void ExportGroup_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement el && el.DataContext is LogGroupViewModel group)
            await ViewModel!.ExportGroupCommand.ExecuteAsync(group);
    }

    private async void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement el && el.DataContext is LogGroupViewModel group)
            await ViewModel!.DeleteGroupCommand.ExecuteAsync(group);
    }

    private void OpenFileLocation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi &&
            mi.Parent is ContextMenu cm &&
            cm.PlacementTarget is FrameworkElement fe &&
            fe.DataContext is GroupFileMemberViewModel fileVm)
        {
            var dir = Path.GetDirectoryName(fileVm.FilePath);
            if (File.Exists(fileVm.FilePath))
                Process.Start("explorer.exe", $"/select,\"{fileVm.FilePath}\"");
            else if (dir != null && Directory.Exists(dir))
                Process.Start("explorer.exe", dir);
        }
    }

    private async void RemoveDashboardFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi ||
            mi.Parent is not ContextMenu cm ||
            cm.PlacementTarget is not FrameworkElement fe ||
            fe.DataContext is not GroupFileMemberViewModel fileVm ||
            fe.Tag is not LogGroupViewModel groupVm ||
            ViewModel == null)
        {
            return;
        }

        await ViewModel.RemoveFileFromDashboardAsync(groupVm, fileVm.FileId);
        e.Handled = true;
    }

    // ── Tab context menu handlers ─────────────────────────────────────────────

    private void PinTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is LogTabViewModel tab)
            ViewModel?.TogglePinTab(tab);
    }

    private async void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is LogTabViewModel tab)
            await ViewModel!.CloseTabCommand.ExecuteAsync(tab);
    }

    private async void CloseOtherTabs_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is LogTabViewModel tab)
            await ViewModel!.CloseOtherTabsAsync(tab);
    }

    private async void CloseAllButPinned_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
            await ViewModel.CloseAllButPinnedAsync();
    }

    private async void CloseAllTabs_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
            await ViewModel.CloseAllTabsAsync();
    }



    private void LogListBox_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not ListBox lb || lb.DataContext is not LogTabViewModel tab) return;

        tab.UpdateViewportLineCount(MeasureViewportLineCount(lb));
    }

    /// <summary>
    /// Measures how many lines fit in a ListBox by probing a rendered container's height.
    /// Falls back to 16px (Consolas 12pt) if no containers are realized yet.
    /// </summary>
    private static int MeasureViewportLineCount(ListBox listBox)
    {
        double itemHeight = 0;
        if (listBox.Items.Count > 0)
        {
            var container = listBox.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement;
            if (container != null)
                itemHeight = container.ActualHeight;
        }
        if (itemHeight <= 0)
            itemHeight = 16;

        return Math.Max(1, (int)(listBox.ActualHeight / itemHeight));
    }

    private void LogListBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ListBox lb || lb.DataContext is not LogTabViewModel tab) return;
        int delta = e.Delta > 0 ? -3 : 3;
        tab.ScrollPosition = Math.Max(0, Math.Min(tab.MaxScrollPosition, tab.ScrollPosition + delta));
        e.Handled = true;
    }
}
