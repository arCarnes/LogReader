namespace LogReader.App.Views;

using System.ComponentModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using LogReader.App.ViewModels;

public partial class LogViewportView : UserControl
{
    internal readonly record struct PendingLineSelection(string TabInstanceId, int LineNumber);

    internal enum VerticalNavigationKind
    {
        ScrollByDelta,
        JumpToTop,
        JumpToBottom
    }

    internal readonly record struct VerticalNavigationRequest(VerticalNavigationKind Kind, int ScrollDelta);

    internal const string CopySelectedLinesMenuItemTag = "CopySelectedLines";
    internal const string OpenLogFileMenuItemTag = "OpenLogFile";
    internal const string BulkOpenFilesMenuItemTag = "BulkOpenFiles";

    private LogTabViewModel? _subscribedTab;
    private MainViewModel? _subscribedViewModel;
    private ListBox? _activeLogListBox;
    private ListBox? _fontMetricSubscribedListBox;
    private PendingLineSelection? _pendingLineSelection;
    private PendingSelectionRestore? _pendingSelectionRestore;

    internal readonly record struct PendingSelectionRestore(string TabInstanceId, IReadOnlyList<int> LineNumbers);

    public LogViewportView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_subscribedViewModel != null)
                _subscribedViewModel.PropertyChanged -= ViewModel_PropertyChanged;

            _activeLogListBox = null;
            _pendingLineSelection = null;
            _pendingSelectionRestore = null;
            SubscribeToSelectedTab(null);
            _subscribedViewModel = ViewModel;
            if (_subscribedViewModel != null)
            {
                _subscribedViewModel.PropertyChanged += ViewModel_PropertyChanged;
                SubscribeToSelectedTab(_subscribedViewModel.SelectedTab);
            }
        };
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ShouldRefreshViewportForPropertyChange(e.PropertyName))
            return;

        if (e.PropertyName == nameof(MainViewModel.SelectedTab))
        {
            _pendingLineSelection = null;
            _pendingSelectionRestore = null;
            SubscribeToSelectedTab(ViewModel?.SelectedTab);
        }

        RequestViewportRefreshForSelectedTab(forceLayout: e.PropertyName == nameof(MainViewModel.ViewportRefreshVersion));
    }

    internal static bool ShouldRefreshViewportForPropertyChange(string? propertyName)
        => propertyName == nameof(MainViewModel.SelectedTab) ||
           propertyName == nameof(MainViewModel.ViewportRefreshVersion);

    private void SubscribeToSelectedTab(LogTabViewModel? tab)
    {
        if (_subscribedTab != null)
        {
            _subscribedTab.PropertyChanged -= Tab_PropertyChanged;
            _subscribedTab.VisibleLines.CollectionChanged -= VisibleLines_CollectionChanged;
        }

        _subscribedTab = tab;
        if (_subscribedTab != null)
        {
            _subscribedTab.PropertyChanged += Tab_PropertyChanged;
            _subscribedTab.VisibleLines.CollectionChanged += VisibleLines_CollectionChanged;
        }
    }

    private void RequestViewportRefreshForSelectedTab(bool forceLayout)
    {
        Dispatcher.InvokeAsync(
            () => RefreshViewportForSelectedTab(forceLayout),
            forceLayout
                ? System.Windows.Threading.DispatcherPriority.Loaded
                : System.Windows.Threading.DispatcherPriority.Background);

        if (!forceLayout)
            return;

        Dispatcher.InvokeAsync(
            () => RefreshViewportForSelectedTab(forceLayout: true),
            System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private void RefreshViewportForSelectedTab(bool forceLayout)
    {
        var tab = ViewModel?.SelectedTab;
        if (tab == null)
            return;

        var listBox = GetActiveLogListBox(tab);
        if (listBox == null)
            return;

        ApplyForcedLayoutIfRequested(listBox, forceLayout, ForceLayout);
        var viewportLineCount = TryMeasureViewportLineCount(listBox);
        if (viewportLineCount != null)
            tab.UpdateViewportLineCount(viewportLineCount.Value);

        RequestHorizontalContentWidthMeasurement(listBox, tab);
    }

    internal static void ApplyForcedLayoutIfRequested(
        ListBox listBox,
        bool forceLayout,
        Action<ListBox>? forceLayoutAction = null)
    {
        ArgumentNullException.ThrowIfNull(listBox);

        if (!forceLayout)
            return;

        (forceLayoutAction ?? ForceLayout)(listBox);
    }

    internal static void ForceLayout(ListBox listBox)
    {
        ArgumentNullException.ThrowIfNull(listBox);
        listBox.ApplyTemplate();
        listBox.UpdateLayout();
    }

    private ListBox? GetActiveLogListBox(LogTabViewModel tab)
    {
        if (_activeLogListBox != null &&
            ReferenceEquals(_activeLogListBox.DataContext, tab) &&
            _activeLogListBox.IsLoaded)
        {
            return _activeLogListBox;
        }

        _activeLogListBox = null;
        return null;
    }

    private void Tab_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogTabViewModel.NavigateToLineNumber) &&
            sender is LogTabViewModel tab &&
            tab.NavigateToLineNumber > 0)
        {
            var lineNumber = tab.NavigateToLineNumber;
            Dispatcher.InvokeAsync(
                () => SelectLine(tab, lineNumber),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        if (ShouldRefreshViewportForTabPropertyChange(e.PropertyName))
            RequestViewportRefreshForSelectedTab(forceLayout: true);
    }

    private void SelectLine(LogTabViewModel tab, int lineNumber)
    {
        if (!ReferenceEquals(ViewModel?.SelectedTab, tab))
            return;

        var listBox = GetActiveLogListBox(tab);
        if (listBox == null)
        {
            QueuePendingLineSelection(tab, lineNumber);
            return;
        }

        if (TrySelectLine(listBox, lineNumber))
        {
            _pendingLineSelection = null;
            ClearPendingSelectionRestoreForLine(tab, lineNumber);
        }
        else
        {
            QueuePendingLineSelection(tab, lineNumber);
        }
    }

    internal static bool TrySelectLine(ListBox listBox, int lineNumber)
    {
        ArgumentNullException.ThrowIfNull(listBox);

        var item = listBox.Items.Cast<LogLineViewModel>().FirstOrDefault(line => line.LineNumber == lineNumber);
        if (item == null)
            return false;

        listBox.SelectedItems.Clear();
        listBox.SelectedItem = item;
        listBox.ScrollIntoView(item);
        listBox.Focus();

        if (listBox.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container)
            container.Focus();

        return true;
    }

    private void LogListBox_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not ListBox listBox || listBox.DataContext is not LogTabViewModel tab)
            return;

        var viewportLineCount = TryMeasureViewportLineCount(listBox);
        if (viewportLineCount != null)
            tab.UpdateViewportLineCount(viewportLineCount.Value);
        RequestHorizontalContentWidthMeasurement(listBox, tab);
    }

    private void LogListBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBox listBox)
            return;

        if (ReferenceEquals(ViewModel?.SelectedTab, listBox.DataContext))
            _activeLogListBox = listBox;

        SubscribeToFontMetricChanges(listBox);
        RefreshViewportForSelectedTab(forceLayout: false);
        TryApplyPendingLineSelection();
    }

    private void LogListBox_Unloaded(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(_fontMetricSubscribedListBox, sender))
            UnsubscribeFromFontMetricChanges(_fontMetricSubscribedListBox);

        if (ReferenceEquals(_activeLogListBox, sender))
            _activeLogListBox = null;
    }

    private void VisibleLines_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.InvokeAsync(
            TryRestoreSelectionAfterViewportChange,
            System.Windows.Threading.DispatcherPriority.ContextIdle);

        var tab = _subscribedTab;
        var listBox = tab == null ? null : GetActiveLogListBox(tab);
        if (tab != null && listBox != null)
            RequestHorizontalContentWidthMeasurement(listBox, tab);
    }

    private void RequestHorizontalContentWidthMeasurement(ListBox listBox, LogTabViewModel tab)
    {
        Dispatcher.InvokeAsync(
            () =>
            {
                if (!ReferenceEquals(ViewModel?.SelectedTab, tab) ||
                    !ReferenceEquals(GetActiveLogListBox(tab), listBox))
                {
                    return;
                }

                var observedWidth = MeasureWidestRealizedRowWidth(listBox);
                if (observedWidth != null)
                    tab.GrowHorizontalContentMinWidth(observedWidth.Value);
            },
            System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    internal static double? MeasureWidestRealizedRowWidth(ListBox listBox)
    {
        ArgumentNullException.ThrowIfNull(listBox);

        double? maxWidth = null;
        for (var i = 0; i < listBox.Items.Count; i++)
        {
            if (listBox.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem container)
                continue;

            var width = container.DesiredSize.Width;
            if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width))
                width = container.ActualWidth;

            if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width))
                continue;

            maxWidth = Math.Max(maxWidth ?? 0, width);
        }

        return maxWidth;
    }

    private void SubscribeToFontMetricChanges(ListBox listBox)
    {
        if (ReferenceEquals(_fontMetricSubscribedListBox, listBox))
            return;

        if (_fontMetricSubscribedListBox != null)
            UnsubscribeFromFontMetricChanges(_fontMetricSubscribedListBox);

        DependencyPropertyDescriptor
            .FromProperty(Control.FontFamilyProperty, typeof(ListBox))
            .AddValueChanged(listBox, LogListBox_FontMetricChanged);
        DependencyPropertyDescriptor
            .FromProperty(Control.FontSizeProperty, typeof(ListBox))
            .AddValueChanged(listBox, LogListBox_FontMetricChanged);
        _fontMetricSubscribedListBox = listBox;
    }

    private void UnsubscribeFromFontMetricChanges(ListBox listBox)
    {
        DependencyPropertyDescriptor
            .FromProperty(Control.FontFamilyProperty, typeof(ListBox))
            .RemoveValueChanged(listBox, LogListBox_FontMetricChanged);
        DependencyPropertyDescriptor
            .FromProperty(Control.FontSizeProperty, typeof(ListBox))
            .RemoveValueChanged(listBox, LogListBox_FontMetricChanged);

        if (ReferenceEquals(_fontMetricSubscribedListBox, listBox))
            _fontMetricSubscribedListBox = null;
    }

    private void LogListBox_FontMetricChanged(object? sender, EventArgs e)
    {
        if (sender is not ListBox listBox || listBox.DataContext is not LogTabViewModel tab)
            return;

        tab.ResetHorizontalContentMinWidth();
        RequestHorizontalContentWidthMeasurement(listBox, tab);
    }

    internal static bool ShouldApplyPendingLineSelection(
        PendingLineSelection? pendingLineSelection,
        LogTabViewModel? selectedTab,
        int currentNavigateToLineNumber)
    {
        return pendingLineSelection is { } pending &&
               selectedTab != null &&
               string.Equals(pending.TabInstanceId, selectedTab.TabInstanceId, StringComparison.Ordinal) &&
               pending.LineNumber == currentNavigateToLineNumber;
    }

    internal static bool ShouldRefreshViewportForTabPropertyChange(string? propertyName)
        => propertyName == nameof(LogTabViewModel.ViewportRefreshToken);

    private void QueuePendingLineSelection(LogTabViewModel tab, int lineNumber)
    {
        _pendingLineSelection = new PendingLineSelection(tab.TabInstanceId, lineNumber);

        Dispatcher.InvokeAsync(
            TryApplyPendingLineSelection,
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void TryApplyPendingLineSelection()
    {
        var selectedTab = ViewModel?.SelectedTab;
        if (!ShouldApplyPendingLineSelection(_pendingLineSelection, selectedTab, selectedTab?.NavigateToLineNumber ?? -1))
            return;

        var listBox = GetActiveLogListBox(selectedTab!);
        if (listBox == null)
            return;

        var pendingLineSelection = _pendingLineSelection;
        if (pendingLineSelection != null && TrySelectLine(listBox, pendingLineSelection.Value.LineNumber))
            _pendingLineSelection = null;
    }

    internal static int? TryMeasureViewportLineCount(ListBox listBox)
    {
        ArgumentNullException.ThrowIfNull(listBox);

        double? itemHeight = null;
        if (listBox.Items.Count > 0)
        {
            var container = listBox.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement;
            if (container?.ActualHeight > 0)
                itemHeight = container.ActualHeight;
        }

        return TryCalculateViewportLineCount(listBox.ActualHeight, itemHeight);
    }

    internal static int? TryCalculateViewportLineCount(double viewportHeight, double? itemHeight)
        => viewportHeight > 0 && itemHeight > 0
            ? Math.Max(1, (int)(viewportHeight / itemHeight.Value))
            : null;

    private void LogListBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ListBox listBox || listBox.DataContext is not LogTabViewModel tab)
            return;

        CaptureSelectionForViewportChange(listBox, tab);
        e.Handled = HandleMouseWheel(ViewModel, tab, e.Delta);
    }

    private void LogListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListBox listBox)
            return;

        if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (TryCopySelectedLines(listBox))
                e.Handled = true;

            return;
        }

        if (listBox.DataContext is not LogTabViewModel tab)
            return;

        if (ShouldPreserveSelectionForKeyboardViewportNavigation(e.Key, Keyboard.Modifiers, tab.ViewportLineCount))
            CaptureSelectionForViewportChange(listBox, tab);

        var pendingSelectionLineNumber = GetPendingSelectionLineNumber(tab);
        var pendingSelectionMoveTarget = GetSelectionMoveTargetLineNumber(listBox, tab, e.Key, Keyboard.Modifiers, pendingSelectionLineNumber);
        e.Handled = HandleKeyboardNavigation(listBox, ViewModel, tab, e.Key, Keyboard.Modifiers, pendingSelectionLineNumber);
        if (e.Handled)
        {
            if (pendingSelectionMoveTarget != null)
                _pendingSelectionRestore = null;

            CapturePendingSelectionMoveIfNeeded(listBox, tab, pendingSelectionMoveTarget);
        }
    }

    private void LogListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox listBox && listBox.DataContext is LogTabViewModel tab)
            _pendingSelectionRestore = null;
    }

    private void VerticalScrollBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        TryExitStickyAutoScrollForScrollBar(ViewModel, e.ChangedButton);
    }

    private void VerticalScrollBar_Scroll(object sender, ScrollEventArgs e)
    {
        if (sender is not ScrollBar scrollBar || scrollBar.DataContext is not LogTabViewModel tab || tab.AutoScrollEnabled)
            return;

        CaptureSelectionForViewportChange(GetActiveLogListBox(tab), tab);
        tab.ScrollPosition = Math.Max(0, Math.Min(tab.MaxScrollPosition, (int)Math.Round(e.NewValue)));
    }

    private void CaptureSelectionForViewportChange(ListBox? listBox, LogTabViewModel tab)
    {
        if (listBox == null)
            return;

        var lineNumbers = CaptureSelectedLineNumbers(listBox);
        if (lineNumbers.Count > 0)
            _pendingSelectionRestore = ResolveSelectionRestoreForViewportChange(_pendingSelectionRestore, tab, lineNumbers);
    }

    private void TryRestoreSelectionAfterViewportChange()
    {
        var selectedTab = ViewModel?.SelectedTab;
        if (_pendingSelectionRestore is not { } restore ||
            selectedTab == null ||
            !string.Equals(restore.TabInstanceId, selectedTab.TabInstanceId, StringComparison.Ordinal))
        {
            return;
        }

        var listBox = GetActiveLogListBox(selectedTab);
        if (listBox == null)
            return;

        if (RestoreSelectionByLineNumber(listBox, restore.LineNumbers))
            _pendingSelectionRestore = null;
    }

    private void ClearPendingSelectionRestoreForLine(LogTabViewModel? tab, int lineNumber)
    {
        if (tab == null ||
            _pendingSelectionRestore is not { } restore ||
            !string.Equals(restore.TabInstanceId, tab.TabInstanceId, StringComparison.Ordinal) ||
            !restore.LineNumbers.Contains(lineNumber))
        {
            return;
        }

        _pendingSelectionRestore = null;
    }

    private void CapturePendingSelectionMoveIfNeeded(
        ListBox listBox,
        LogTabViewModel tab,
        int? targetLineNumber)
    {
        if (targetLineNumber == null)
            return;

        var targetIsVisible = listBox.Items
            .OfType<LogLineViewModel>()
            .Any(line => line.LineNumber == targetLineNumber.Value);
        if (!targetIsVisible)
            _pendingSelectionRestore = new PendingSelectionRestore(tab.TabInstanceId, new[] { targetLineNumber.Value });
    }

    private int? GetPendingSelectionLineNumber(LogTabViewModel tab)
    {
        if (_pendingSelectionRestore is not { } restore ||
            !string.Equals(restore.TabInstanceId, tab.TabInstanceId, StringComparison.Ordinal) ||
            restore.LineNumbers.Count != 1)
        {
            return null;
        }

        return restore.LineNumbers[0];
    }

    internal static bool TryGetVerticalNavigationRequest(
        Key key,
        ModifierKeys modifiers,
        int viewportLineCount,
        out VerticalNavigationRequest request)
    {
        request = default;
        if (modifiers != ModifierKeys.None)
            return false;

        var pageDelta = Math.Max(1, viewportLineCount);
        switch (key)
        {
            case Key.Up:
            case Key.Down:
                return false;
            case Key.PageUp:
                request = new VerticalNavigationRequest(VerticalNavigationKind.ScrollByDelta, -pageDelta);
                return true;
            case Key.PageDown:
                request = new VerticalNavigationRequest(VerticalNavigationKind.ScrollByDelta, pageDelta);
                return true;
            case Key.Home:
                request = new VerticalNavigationRequest(VerticalNavigationKind.JumpToTop, 0);
                return true;
            case Key.End:
                request = new VerticalNavigationRequest(VerticalNavigationKind.JumpToBottom, 0);
                return true;
            default:
                return false;
        }
    }

    internal static bool ShouldDisableStickyAutoScrollForMouseWheel(int delta)
        => delta > 0;

    internal static bool ShouldDisableStickyAutoScrollForVerticalNavigation(VerticalNavigationRequest request)
        => request.Kind == VerticalNavigationKind.JumpToTop ||
           (request.Kind == VerticalNavigationKind.ScrollByDelta && request.ScrollDelta < 0);

    internal static bool ShouldDisableStickyAutoScrollForScrollBar(MouseButton button)
        => button == MouseButton.Left;

    internal static bool ShouldPreserveSelectionForKeyboardViewportNavigation(Key key, ModifierKeys modifiers, int viewportLineCount)
        => TryGetVerticalNavigationRequest(key, modifiers, viewportLineCount, out _);

    internal static PendingSelectionRestore? ResolveSelectionRestoreForViewportChange(
        PendingSelectionRestore? pendingSelectionRestore,
        LogTabViewModel tab,
        IReadOnlyList<int> visibleSelectedLineNumbers)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(visibleSelectedLineNumbers);

        if (pendingSelectionRestore is { } pending &&
            string.Equals(pending.TabInstanceId, tab.TabInstanceId, StringComparison.Ordinal))
        {
            return pending;
        }

        return visibleSelectedLineNumbers.Count > 0
            ? new PendingSelectionRestore(tab.TabInstanceId, visibleSelectedLineNumbers)
            : null;
    }

    internal static bool HandleMouseWheel(MainViewModel? viewModel, LogTabViewModel tab, int delta)
    {
        DisableStickyAutoScrollIfNeeded(viewModel, ShouldDisableStickyAutoScrollForMouseWheel(delta));

        var scrollDelta = delta > 0 ? -3 : 3;
        tab.ScrollPosition = Math.Max(0, Math.Min(tab.MaxScrollPosition, tab.ScrollPosition + scrollDelta));
        return true;
    }

    internal static bool HandleVerticalNavigation(
        MainViewModel? viewModel,
        LogTabViewModel tab,
        Key key,
        ModifierKeys modifiers)
    {
        if (!TryGetVerticalNavigationRequest(key, modifiers, tab.ViewportLineCount, out var request))
            return false;

        DisableStickyAutoScrollIfNeeded(viewModel, ShouldDisableStickyAutoScrollForVerticalNavigation(request));
        ApplyVerticalNavigation(tab, request);
        return true;
    }

    internal static bool HandleKeyboardNavigation(
        ListBox listBox,
        MainViewModel? viewModel,
        LogTabViewModel tab,
        Key key,
        ModifierKeys modifiers,
        int? pendingSelectionLineNumber = null)
    {
        if (TryMoveSelectionByLine(listBox, tab, key, modifiers, pendingSelectionLineNumber))
            return true;

        if (!TryGetVerticalNavigationRequest(key, modifiers, tab.ViewportLineCount, out var request))
            return false;

        var selectedLineNumbers = CaptureSelectedLineNumbers(listBox);
        DisableStickyAutoScrollIfNeeded(viewModel, ShouldDisableStickyAutoScrollForVerticalNavigation(request));
        ApplyVerticalNavigation(tab, request);
        _ = RestoreSelectionByLineNumber(listBox, selectedLineNumbers);
        return true;
    }

    internal static bool TryExitStickyAutoScrollForScrollBar(MainViewModel? viewModel, MouseButton button)
    {
        if (!ShouldDisableStickyAutoScrollForScrollBar(button))
            return false;

        DisableStickyAutoScrollIfNeeded(viewModel, shouldDisable: true);
        return true;
    }

    private static void ApplyVerticalNavigation(LogTabViewModel tab, VerticalNavigationRequest request)
    {
        switch (request.Kind)
        {
            case VerticalNavigationKind.ScrollByDelta:
                tab.ScrollPosition = Math.Max(0, Math.Min(tab.MaxScrollPosition, tab.ScrollPosition + request.ScrollDelta));
                break;
            case VerticalNavigationKind.JumpToTop:
                if (tab.JumpToTopCommand.CanExecute(null))
                    tab.JumpToTopCommand.Execute(null);

                break;
            case VerticalNavigationKind.JumpToBottom:
                if (tab.JumpToBottomCommand.CanExecute(null))
                    tab.JumpToBottomCommand.Execute(null);

                break;
        }
    }

    internal static IReadOnlyList<int> CaptureSelectedLineNumbers(ListBox listBox)
    {
        ArgumentNullException.ThrowIfNull(listBox);

        return listBox.SelectedItems
            .OfType<LogLineViewModel>()
            .Select(line => line.LineNumber)
            .Distinct()
            .ToList();
    }

    internal static bool RestoreSelectionByLineNumber(ListBox listBox, IReadOnlyList<int> selectedLineNumbers)
    {
        ArgumentNullException.ThrowIfNull(listBox);
        ArgumentNullException.ThrowIfNull(selectedLineNumbers);

        var selectedLineNumberSet = selectedLineNumbers.ToHashSet();
        listBox.SelectedItems.Clear();
        if (selectedLineNumberSet.Count == 0)
            return true;

        var restoredSelection = false;
        foreach (var item in listBox.Items.OfType<LogLineViewModel>())
        {
            if (selectedLineNumberSet.Contains(item.LineNumber))
            {
                listBox.SelectedItems.Add(item);
                restoredSelection = true;
            }
        }

        return restoredSelection;
    }

    internal static bool TryMoveSelectionByLine(
        ListBox listBox,
        LogTabViewModel tab,
        Key key,
        ModifierKeys modifiers,
        int? pendingSelectionLineNumber = null)
    {
        ArgumentNullException.ThrowIfNull(listBox);
        ArgumentNullException.ThrowIfNull(tab);

        if (modifiers != ModifierKeys.None || key is not (Key.Up or Key.Down))
            return false;

        var visibleLines = listBox.Items.OfType<LogLineViewModel>().ToList();
        if (visibleLines.Count == 0)
            return true;

        var selectedLineNumber = GetSelectedLineNumber(listBox);
        var currentLineNumber = selectedLineNumber ?? pendingSelectionLineNumber;
        if (currentLineNumber == null)
        {
            listBox.SelectedItems.Clear();
            listBox.SelectedItem = visibleLines[0];
            return true;
        }

        var targetLineNumber = currentLineNumber.Value + (key == Key.Up ? -1 : 1);
        if (targetLineNumber < 1 || targetLineNumber > tab.TotalLines)
            return true;

        var visibleTarget = visibleLines.FirstOrDefault(line => line.LineNumber == targetLineNumber);
        if (visibleTarget != null)
        {
            listBox.SelectedItems.Clear();
            listBox.SelectedItem = visibleTarget;
            return true;
        }

        var scrollDelta = key == Key.Up ? -1 : 1;
        tab.ScrollPosition = Math.Max(0, Math.Min(tab.MaxScrollPosition, tab.ScrollPosition + scrollDelta));
        listBox.SelectedItems.Clear();
        return true;
    }

    internal static int? GetSelectionMoveTargetLineNumber(
        ListBox listBox,
        LogTabViewModel tab,
        Key key,
        ModifierKeys modifiers,
        int? pendingSelectionLineNumber = null)
    {
        ArgumentNullException.ThrowIfNull(listBox);
        ArgumentNullException.ThrowIfNull(tab);

        if (modifiers != ModifierKeys.None || key is not (Key.Up or Key.Down))
            return null;

        var currentLineNumber = GetSelectedLineNumber(listBox) ?? pendingSelectionLineNumber;
        if (currentLineNumber == null)
            return null;

        var targetLineNumber = currentLineNumber.Value + (key == Key.Up ? -1 : 1);
        return targetLineNumber < 1 || targetLineNumber > tab.TotalLines
            ? null
            : targetLineNumber;
    }

    private static int? GetSelectedLineNumber(ListBox listBox)
        => listBox.SelectedItems
            .OfType<LogLineViewModel>()
            .OrderBy(line => line.LineNumber)
            .Select(line => (int?)line.LineNumber)
            .FirstOrDefault();

    private static void DisableStickyAutoScrollIfNeeded(MainViewModel? viewModel, bool shouldDisable)
    {
        if (shouldDisable && viewModel?.GlobalAutoScrollEnabled == true)
            viewModel.GlobalAutoScrollEnabled = false;
    }

    private void CopySelectedLines_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem ||
            menuItem.Parent is not ContextMenu contextMenu ||
            contextMenu.PlacementTarget is not ListBox listBox)
        {
            return;
        }

        if (TryCopySelectedLines(listBox))
            e.Handled = true;
    }

    private void ViewportContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu contextMenu)
            return;

        UpdateViewportContextMenu(contextMenu, ViewModel?.IsCurrentScopeEmpty == true);
    }

    internal static void UpdateViewportContextMenu(ContextMenu contextMenu, bool isCurrentScopeEmpty)
    {
        foreach (var menuItem in contextMenu.Items.OfType<MenuItem>())
        {
            var tag = menuItem.Tag as string;
            if (tag == CopySelectedLinesMenuItemTag)
            {
                menuItem.Visibility = isCurrentScopeEmpty ? Visibility.Collapsed : Visibility.Visible;
                continue;
            }

            if (tag == OpenLogFileMenuItemTag || tag == BulkOpenFilesMenuItemTag)
                menuItem.Visibility = isCurrentScopeEmpty ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static bool TryCopySelectedLines(ListBox listBox)
    {
        var lines = listBox.SelectedItems
            .OfType<LogLineViewModel>()
            .OrderBy(line => line.LineNumber)
            .Select(line => line.Text)
            .ToList();

        if (lines.Count == 0)
            return false;

        Clipboard.SetText(string.Join(Environment.NewLine, lines));
        return true;
    }
}
