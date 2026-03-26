namespace LogReader.App.Views;

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LogReader.App.ViewModels;

public partial class LogViewportView : UserControl
{
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

    public LogViewportView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_subscribedViewModel != null)
                _subscribedViewModel.PropertyChanged -= ViewModel_PropertyChanged;

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
        if (e.PropertyName != nameof(MainViewModel.SelectedTab))
            return;

        SubscribeToSelectedTab(ViewModel?.SelectedTab);
        Dispatcher.InvokeAsync(
            RefreshViewportForSelectedTab,
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void SubscribeToSelectedTab(LogTabViewModel? tab)
    {
        if (_subscribedTab != null)
            _subscribedTab.PropertyChanged -= Tab_PropertyChanged;

        _subscribedTab = tab;
        if (_subscribedTab != null)
            _subscribedTab.PropertyChanged += Tab_PropertyChanged;
    }

    private void RefreshViewportForSelectedTab()
    {
        var tab = ViewModel?.SelectedTab;
        if (tab == null)
            return;

        var listBox = FindVisualChild<ListBox>(TabContentHost, "LogListBox");
        if (listBox == null || listBox.DataContext != tab)
            return;

        tab.UpdateViewportLineCount(MeasureViewportLineCount(listBox));
    }

    private void Tab_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogTabViewModel.NavigateToLineNumber) &&
            sender is LogTabViewModel tab &&
            tab.NavigateToLineNumber > 0)
        {
            var lineNumber = tab.NavigateToLineNumber;
            Dispatcher.InvokeAsync(
                () => SelectLine(lineNumber),
                System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void SelectLine(int lineNumber)
    {
        var listBox = FindVisualChild<ListBox>(TabContentHost, "LogListBox");
        if (listBox == null)
            return;

        var item = listBox.Items.Cast<LogLineViewModel>().FirstOrDefault(line => line.LineNumber == lineNumber);
        if (item == null)
            return;

        listBox.SelectedItem = item;
    }

    private void LogListBox_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not ListBox listBox || listBox.DataContext is not LogTabViewModel tab)
            return;

        tab.UpdateViewportLineCount(MeasureViewportLineCount(listBox));
    }

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
        if (sender is not ListBox listBox || listBox.DataContext is not LogTabViewModel tab)
            return;

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

        e.Handled = HandleVerticalNavigation(ViewModel, tab, e.Key, Keyboard.Modifiers);
    }

    private void VerticalScrollBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        TryExitStickyAutoScrollForScrollBar(ViewModel, e.ChangedButton);
    }

    private void VerticalScrollBar_Scroll(object sender, ScrollEventArgs e)
    {
        if (sender is not ScrollBar scrollBar || scrollBar.DataContext is not LogTabViewModel tab || tab.AutoScrollEnabled)
            return;

        tab.ScrollPosition = Math.Max(0, Math.Min(tab.MaxScrollPosition, (int)Math.Round(e.NewValue)));
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
                request = new VerticalNavigationRequest(VerticalNavigationKind.ScrollByDelta, -1);
                return true;
            case Key.Down:
                request = new VerticalNavigationRequest(VerticalNavigationKind.ScrollByDelta, 1);
                return true;
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

    private static T? FindVisualChild<T>(DependencyObject parent, string? name = null) where T : FrameworkElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T element && (name == null || element.Name == name))
                return element;

            var nested = FindVisualChild<T>(child, name);
            if (nested != null)
                return nested;
        }

        return null;
    }
}
