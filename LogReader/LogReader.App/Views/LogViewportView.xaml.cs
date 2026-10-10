namespace LogReader.App.Views;

using System.ComponentModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
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
    private PendingLineSelection? _pendingLineNavigationRetry;
    private bool _projectingSelection;
    private readonly Action<string> _copyTextToClipboard = Clipboard.SetText;

    internal LogViewportView(Action<string> copyTextToClipboard) : this()
    {
        ArgumentNullException.ThrowIfNull(copyTextToClipboard);
        _copyTextToClipboard = copyTextToClipboard;
    }

    public LogViewportView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_subscribedViewModel != null)
                _subscribedViewModel.PropertyChanged -= ViewModel_PropertyChanged;

            _activeLogListBox = null;
            _pendingLineSelection = null;
            _pendingLineNavigationRetry = null;
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
            _pendingLineNavigationRetry = null;
            SubscribeToSelectedTab(ViewModel?.SelectedTab);
        }

        RequestViewportRefreshForSelectedTab(forceLayout: true);
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
        var tab = ViewModel?.SelectedTab;
        var listBox = tab == null ? null : GetActiveLogListBox(tab);
        Dispatcher.InvokeAsync(
            () => RefreshViewportForSelectedTab(forceLayout, tab, listBox),
            forceLayout
                ? System.Windows.Threading.DispatcherPriority.Loaded
                : System.Windows.Threading.DispatcherPriority.Background);

        if (!forceLayout)
            return;

        Dispatcher.InvokeAsync(
            () => RefreshViewportForSelectedTab(true, tab, listBox),
            System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private void RefreshViewportForSelectedTab(bool forceLayout, LogTabViewModel? tab, ListBox? expectedListBox)
    {
        if (tab == null || !ReferenceEquals(ViewModel?.SelectedTab, tab))
            return;

        var listBox = GetActiveLogListBox(tab);
        if (listBox == null || expectedListBox != null && !ReferenceEquals(listBox, expectedListBox))
            return;

        MeasureAndPublishViewportCapacity(listBox, tab, forceLayout);
        ProjectSelection(listBox, tab);
        RequestHorizontalContentWidthMeasurement(listBox, tab);
    }

    internal static void ForceLayout(ListBox listBox)
    {
        ArgumentNullException.ThrowIfNull(listBox);
        listBox.ApplyTemplate();
        listBox.UpdateLayout();
        EnsureFirstVisibleItemRealized(listBox);
    }

    private static bool EnsureFirstVisibleItemRealized(ListBox listBox)
    {
        ArgumentNullException.ThrowIfNull(listBox);

        if (!ShouldRetryVisibleItemRealization(listBox))
            return true;

        var firstItem = listBox.Items[0];
        listBox.ScrollIntoView(firstItem);
        listBox.UpdateLayout();
        return IsFirstVisibleItemContainerRealized(listBox);
    }

    private static bool ShouldRetryVisibleItemRealization(ListBox listBox)
    {
        ArgumentNullException.ThrowIfNull(listBox);

        return listBox.Items.Count > 0 && !IsFirstVisibleItemContainerRealized(listBox);
    }

    private static bool IsFirstVisibleItemContainerRealized(ListBox listBox)
    {
        ArgumentNullException.ThrowIfNull(listBox);

        return listBox.Items.Count == 0 ||
               listBox.ItemContainerGenerator.ContainerFromItem(listBox.Items[0]) != null;
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
        if (e.PropertyName == nameof(LogTabViewModel.SelectionRevision) && sender is LogTabViewModel selectionTab)
        {
            if (_pendingLineSelection is { } pending &&
                (selectionTab.SelectionCaret != pending.LineNumber || selectionTab.SelectedLineNumbers.Count != 1))
            {
                _pendingLineSelection = null;
                if (_pendingLineNavigationRetry != null)
                    selectionTab.CancelPendingLineNavigation();
            }
            QueueSelectionProjection(selectionTab, GetActiveLogListBox(selectionTab));
            CommandManager.InvalidateRequerySuggested();
        }

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
        if (!ReferenceEquals(ViewModel?.SelectedTab, tab) || tab.NavigateToLineNumber != lineNumber ||
            !tab.SelectedLineNumbers.Contains(lineNumber))
            return;

        var listBox = GetActiveLogListBox(tab);
        if (listBox == null)
        {
            QueuePendingLineSelection(tab, lineNumber);
            return;
        }

        bool selected;
        _projectingSelection = true;
        try { selected = TrySelectLine(listBox, lineNumber); }
        finally { _projectingSelection = false; }
        if (selected)
        {
            _pendingLineSelection = null;
        }
        else
        {
            QueuePendingLineSelection(tab, lineNumber);
            RetryPendingLineNavigationIfTargetIsMissing(tab);
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

        MeasureAndPublishViewportCapacity(listBox, tab, forceLayout: false);
        RequestHorizontalContentWidthMeasurement(listBox, tab);
    }

    private void LogListBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBox listBox)
            return;

        var selectedTab = ViewModel?.SelectedTab;
        var shouldRefreshSelectedListBox = selectedTab != null && ReferenceEquals(selectedTab, listBox.DataContext);
        if (shouldRefreshSelectedListBox)
            _activeLogListBox = listBox;

        SubscribeToFontMetricChanges(listBox);
        if (shouldRefreshSelectedListBox)
            RequestViewportRefreshForSelectedTab(forceLayout: true);

        TryApplyPendingLineSelection();
        if (shouldRefreshSelectedListBox)
            ProjectSelection(listBox, selectedTab!);
    }

    private void LogListBox_Unloaded(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(_fontMetricSubscribedListBox, sender))
            UnsubscribeFromFontMetricChanges(_fontMetricSubscribedListBox);

        if (ReferenceEquals(_activeLogListBox, sender))
            _activeLogListBox = null;
    }

    private void LogListBox_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not ListBox listBox)
            return;

        if (e.NewValue is LogTabViewModel tab &&
            ReferenceEquals(ViewModel?.SelectedTab, tab) &&
            listBox.IsLoaded)
        {
            _activeLogListBox = listBox;
            QueueSelectionProjection(tab, listBox);
            TryApplyPendingLineSelection();
            return;
        }

        if (ReferenceEquals(_activeLogListBox, listBox))
            _activeLogListBox = null;
    }

    private void VisibleLines_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var tab = _subscribedTab;
        var listBox = tab == null ? null : GetActiveLogListBox(tab);
        if (tab != null)
            QueueSelectionProjection(tab, listBox);
        if (e.Action == NotifyCollectionChangedAction.Reset && tab != null)
        {
            RequestVisibleItemRealizationRetryForTab(tab);
            RetryPendingLineNavigationIfTargetIsMissing(tab);
        }

        if (tab != null && listBox != null)
            RequestHorizontalContentWidthMeasurement(listBox, tab);
    }

    private void RequestVisibleItemRealizationRetryForTab(LogTabViewModel tab)
    {
        var listBox = GetActiveLogListBox(tab);
        if (listBox == null)
            return;
        Dispatcher.InvokeAsync(
            () => RetryVisibleItemRealizationForTab(tab, listBox),
            System.Windows.Threading.DispatcherPriority.Loaded);

        Dispatcher.InvokeAsync(
            () => RetryVisibleItemRealizationForTab(tab, listBox),
            System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private void RetryVisibleItemRealizationForTab(LogTabViewModel tab, ListBox expectedListBox)
    {
        if (!ReferenceEquals(ViewModel?.SelectedTab, tab))
            return;

        var listBox = GetActiveLogListBox(tab);
        if (listBox == null || !ReferenceEquals(listBox, expectedListBox) || !ShouldRetryVisibleItemRealization(listBox))
            return;

        EnsureFirstVisibleItemRealized(listBox);
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

        MeasureAndPublishViewportCapacity(listBox, tab, forceLayout: true);
        tab.ResetHorizontalContentMinWidth();
        RequestHorizontalContentWidthMeasurement(listBox, tab);
    }

    private static void MeasureAndPublishViewportCapacity(ListBox listBox, LogTabViewModel tab, bool forceLayout)
    {
        if (forceLayout)
            ForceLayout(listBox);

        var viewportLineCount = TryMeasureViewportLineCount(listBox);
        if (viewportLineCount != null)
            tab.UpdateViewportLineCount(viewportLineCount.Value);
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

        Dispatcher.InvokeAsync(
            TryApplyPendingLineSelection,
            System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private void RetryPendingLineNavigationIfTargetIsMissing(LogTabViewModel tab)
    {
        if (_pendingLineSelection is not { } pending ||
            !string.Equals(pending.TabInstanceId, tab.TabInstanceId, StringComparison.Ordinal) ||
            tab.SelectionCaret != pending.LineNumber || tab.SelectedLineNumbers.Count != 1 ||
            tab.VisibleLines.Any(line => line.LineNumber == pending.LineNumber) ||
            _pendingLineNavigationRetry == pending)
        {
            return;
        }

        _pendingLineNavigationRetry = pending;
        _ = RetryPendingLineNavigationAsync(tab, pending);
    }

    private async Task RetryPendingLineNavigationAsync(LogTabViewModel tab, PendingLineSelection pending)
    {
        try
        {
            var viewModel = ViewModel;
            if (viewModel == null || !ReferenceEquals(viewModel.SelectedTab, tab))
                return;

            await viewModel.RunViewActionAsync(
                () => tab.NavigateToLineAsync(pending.LineNumber),
                "Search Result Navigation Failed");
        }
        finally
        {
            if (_pendingLineNavigationRetry == pending)
                _pendingLineNavigationRetry = null;
        }
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
        if (pendingLineSelection == null || !selectedTab!.SelectedLineNumbers.Contains(pendingLineSelection.Value.LineNumber))
            return;

        bool selected;
        _projectingSelection = true;
        try { selected = TrySelectLine(listBox, pendingLineSelection.Value.LineNumber); }
        finally { _projectingSelection = false; }
        if (selected)
        {
            _pendingLineSelection = null;
        }
    }

    private static int? TryMeasureViewportLineCount(ListBox listBox)
    {
        ArgumentNullException.ThrowIfNull(listBox);

        double? itemHeight = null;
        if (listBox.Items.Count > 0)
        {
            var container = listBox.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement;
            if (container?.ActualHeight > 0)
                itemHeight = container.ActualHeight;
        }

        var contentViewport = FindFirstDescendant<ScrollContentPresenter>(listBox);
        var viewportHeight = ResolveViewportHeightForLineCount(listBox.ActualHeight, contentViewport?.ActualHeight);
        return TryCalculateViewportLineCount(viewportHeight, itemHeight);
    }

    internal static double ResolveViewportHeightForLineCount(double listBoxActualHeight, double? contentViewportActualHeight)
        => contentViewportActualHeight is > 0 &&
           !double.IsNaN(contentViewportActualHeight.Value) &&
           !double.IsInfinity(contentViewportActualHeight.Value)
            ? contentViewportActualHeight.Value
            : listBoxActualHeight;

    internal static int? TryCalculateViewportLineCount(double viewportHeight, double? itemHeight)
        => viewportHeight > 0 && itemHeight > 0
            ? Math.Max(1, (int)(viewportHeight / itemHeight.Value))
            : null;

    private static T? FindFirstDescendant<T>(DependencyObject parent)
        where T : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;

            var descendant = FindFirstDescendant<T>(child);
            if (descendant != null)
                return descendant;
        }

        return null;
    }

    private void LogListBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ListBox listBox || listBox.DataContext is not LogTabViewModel tab)
            return;

        e.Handled = HandleMouseWheel(ViewModel, tab, e.Delta);
    }

    private void LogListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListBox listBox || listBox.DataContext is not LogTabViewModel tab ||
            !ReferenceEquals(GetActiveLogListBox(tab), listBox) || !ReferenceEquals(ViewModel?.SelectedTab, tab))
            return;

        e.Handled = HandleKeyboardNavigation(listBox, ViewModel, tab, e.Key, Keyboard.Modifiers);
    }

    private void LogListBox_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (sender is ListBox { DataContext: LogTabViewModel tab } &&
            e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift)
            tab.EndSelectionExtension();
    }

    private void LogListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox || listBox.DataContext is not LogTabViewModel tab ||
            !ReferenceEquals(ViewModel?.SelectedTab, tab) || !ReferenceEquals(GetActiveLogListBox(tab), listBox) ||
            e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(listBox, source) is not ListBoxItem { DataContext: LogLineViewModel line } container)
            return;

        HandleLineClick(tab, line.LineNumber, Keyboard.Modifiers);
        if (Keyboard.Modifiers != ModifierKeys.None)
        {
            ProjectSelection(listBox, tab);
            listBox.Focus();
            container.Focus();
            e.Handled = true;
        }
    }

    internal static void HandleLineClick(LogTabViewModel tab, int lineNumber, ModifierKeys modifiers)
    {
        if (modifiers.HasFlag(ModifierKeys.Shift))
            tab.SelectRangeTo(lineNumber, modifiers.HasFlag(ModifierKeys.Control));
        else if (modifiers.HasFlag(ModifierKeys.Control))
            tab.ToggleSelectedLine(lineNumber);
        else
            tab.SelectSingleLine(lineNumber);
    }

    private void JumpToTop_Click(object sender, RoutedEventArgs e)
    {
        DisableStickyAutoScrollIfNeeded(ViewModel, shouldDisable: true);
    }

    private void VerticalScrollBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        TryExitStickyAutoScrollForScrollBar(ViewModel, e.ChangedButton);
    }

    private void VerticalScrollBar_Scroll(object sender, ScrollEventArgs e)
    {
        if (sender is not ScrollBar scrollBar || scrollBar.DataContext is not LogTabViewModel tab)
            return;

        if (!tab.AutoScrollEnabled)
        {
            _ = tab.RequestScrollTo((int)Math.Round(e.NewValue));
        }

        // A standalone WPF ScrollBar replaces its one-way Value binding during native input.
        // Restore it so subsequent navigation and tail updates still move the thumb.
        scrollBar.SetBinding(ScrollBar.ValueProperty, new Binding(nameof(LogTabViewModel.ScrollBarValue))
        {
            Mode = BindingMode.OneWay
        });
    }

    private void LogListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_projectingSelection || sender is not ListBox listBox || listBox.DataContext is not LogTabViewModel tab ||
            tab.IsViewportMutationInProgress || !ReferenceEquals(ViewModel?.SelectedTab, tab) ||
            !ReferenceEquals(GetActiveLogListBox(tab), listBox) || !ReferenceEquals(listBox.ItemsSource, tab.VisibleLines))
            return;

        tab.ApplyUserSelectionChanges(e.AddedItems.OfType<LogLineViewModel>().Select(line => line.LineNumber),
            e.RemovedItems.OfType<LogLineViewModel>().Select(line => line.LineNumber));
    }

    private void QueueSelectionProjection(LogTabViewModel tab, ListBox? listBox)
    {
        if (listBox == null)
            return;
        Dispatcher.InvokeAsync(() =>
        {
            if (ReferenceEquals(ViewModel?.SelectedTab, tab) && ReferenceEquals(GetActiveLogListBox(tab), listBox))
                ProjectSelection(listBox, tab);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void ProjectSelection(ListBox listBox, LogTabViewModel tab)
    {
        _projectingSelection = true;
        try { RestoreSelectionByLineNumber(listBox, tab.SelectedLineNumbers.ToArray()); }
        finally { _projectingSelection = false; }
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

    internal static bool HandleMouseWheel(MainViewModel? viewModel, LogTabViewModel tab, int delta)
    {
        DisableStickyAutoScrollIfNeeded(viewModel, ShouldDisableStickyAutoScrollForMouseWheel(delta));

        var scrollDelta = delta > 0 ? -3 : 3;
        _ = tab.RequestScrollBy(scrollDelta);
        return true;
    }

    internal static bool HandleKeyboardNavigation(
        ListBox listBox,
        MainViewModel? viewModel,
        LogTabViewModel tab,
        Key key,
        ModifierKeys modifiers)
    {
        if (TryMoveSelectionByLine(listBox, tab, key, modifiers, viewModel))
            return true;

        if (!TryGetVerticalNavigationRequest(key, modifiers, tab.ViewportLineCount, out var request))
            return false;

        tab.EndSelectionExtension();
        DisableStickyAutoScrollIfNeeded(viewModel, ShouldDisableStickyAutoScrollForVerticalNavigation(request));
        ApplyVerticalNavigation(tab, request);
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
                _ = tab.RequestScrollBy(request.ScrollDelta);
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

    internal static bool RestoreSelectionByLineNumber(ListBox listBox, IReadOnlyList<int> selectedLineNumbers)
    {
        ArgumentNullException.ThrowIfNull(listBox);
        ArgumentNullException.ThrowIfNull(selectedLineNumbers);

        var selectedLineNumberSet = selectedLineNumbers.ToHashSet();
        foreach (var item in listBox.SelectedItems.OfType<LogLineViewModel>().ToArray())
            if (!selectedLineNumberSet.Contains(item.LineNumber))
                listBox.SelectedItems.Remove(item);
        var restoredSelection = false;
        foreach (var item in listBox.Items.OfType<LogLineViewModel>())
        {
            if (selectedLineNumberSet.Contains(item.LineNumber))
            {
                if (!listBox.SelectedItems.Contains(item))
                    listBox.SelectedItems.Add(item);
                restoredSelection = true;
            }
        }

        return selectedLineNumberSet.Count == 0 || restoredSelection;
    }

    internal static bool TryMoveSelectionByLine(
        ListBox listBox,
        LogTabViewModel tab,
        Key key,
        ModifierKeys modifiers,
        MainViewModel? viewModel = null)
    {
        ArgumentNullException.ThrowIfNull(listBox);
        ArgumentNullException.ThrowIfNull(tab);

        if ((modifiers & ~(ModifierKeys.Control | ModifierKeys.Shift)) != 0 ||
            modifiers == ModifierKeys.Control || key is not (Key.Up or Key.Down))
            return false;

        var visibleLines = listBox.Items.OfType<LogLineViewModel>().ToList();
        if (visibleLines.Count == 0)
            return true;

        var currentLineNumber = tab.SelectionCaret ?? GetSelectedLineNumber(listBox);
        if (currentLineNumber == null)
        {
            tab.SelectSingleLine(visibleLines[0].LineNumber);
            ProjectSelectionForInput(listBox, tab);
            return true;
        }

        var targetLineNumber = tab.GetAdjacentDisplayLineNumber(currentLineNumber.Value, key == Key.Up ? -1 : 1);
        if (targetLineNumber == null)
            return true;

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            if (tab.SelectionAnchor == null)
                tab.SelectSingleLine(currentLineNumber.Value);
            tab.SelectRangeTo(targetLineNumber.Value, modifiers.HasFlag(ModifierKeys.Control), continueExtension: true);
        }
        else
            tab.SelectSingleLine(targetLineNumber.Value);

        var visibleTarget = visibleLines.FirstOrDefault(line => line.LineNumber == targetLineNumber.Value);
        if (visibleTarget != null)
        {
            ProjectSelectionForInput(listBox, tab);
            return true;
        }

        DisableStickyAutoScrollIfNeeded(viewModel, shouldDisable: true);
        _ = tab.RequestSelectionViewportAsync(targetLineNumber.Value);
        ProjectSelectionForInput(listBox, tab);
        return true;
    }

    private static void ProjectSelectionForInput(ListBox listBox, LogTabViewModel tab)
    {
        // Route through the owning view's projection guard when this is a live list.
        var view = FindAncestorViewport(listBox);
        if (view != null)
            view.ProjectSelection(listBox, tab);
        else
            RestoreSelectionByLineNumber(listBox, tab.SelectedLineNumbers.ToArray());
    }

    private static LogViewportView? FindAncestorViewport(DependencyObject item)
    {
        for (var parent = VisualTreeHelper.GetParent(item); parent != null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is LogViewportView view)
                return view;
        return null;
    }

    internal static int? GetSelectionMoveTargetLineNumber(
        ListBox listBox,
        LogTabViewModel tab,
        Key key,
        ModifierKeys modifiers)
    {
        ArgumentNullException.ThrowIfNull(listBox);
        ArgumentNullException.ThrowIfNull(tab);

        if ((modifiers & ~(ModifierKeys.Control | ModifierKeys.Shift)) != 0 ||
            modifiers == ModifierKeys.Control || key is not (Key.Up or Key.Down))
            return null;

        var currentLineNumber = tab.SelectionCaret ?? GetSelectedLineNumber(listBox);
        if (currentLineNumber == null)
            return null;

        return tab.GetAdjacentDisplayLineNumber(currentLineNumber.Value, key == Key.Up ? -1 : 1);
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

    private void CopySelectedLines_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = sender is ListBox { DataContext: LogTabViewModel tab } listBox &&
            ReferenceEquals(ViewModel?.SelectedTab, tab) && ReferenceEquals(GetActiveLogListBox(tab), listBox) &&
            tab.SelectedLineNumbers.Count > 0;
        e.Handled = true;
    }

    private async void CopySelectedLines_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is ListBox listBox)
            await CopySelectedLinesAsync(listBox);
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

    internal Task CopySelectedLinesAsync(ListBox listBox)
    {
        var viewModel = ViewModel;
        if (viewModel == null || listBox.DataContext is not LogTabViewModel tab ||
            !ReferenceEquals(viewModel.SelectedTab, tab) || !ReferenceEquals(GetActiveLogListBox(tab), listBox))
            return Task.CompletedTask;

        return viewModel.RunViewActionAsync(() => tab.CopySelectedLinesAsync(_copyTextToClipboard), "Copy Selected Lines Failed");
    }
}
