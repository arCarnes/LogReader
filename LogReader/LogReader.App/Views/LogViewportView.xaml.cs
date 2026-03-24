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
                () => ScrollToLine(lineNumber),
                System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void ScrollToLine(int lineNumber)
    {
        var listBox = FindVisualChild<ListBox>(TabContentHost, "LogListBox");
        if (listBox == null)
            return;

        var item = listBox.Items.Cast<LogLineViewModel>().FirstOrDefault(line => line.LineNumber == lineNumber);
        if (item == null)
            return;

        listBox.SelectedItem = item;

        var scrollViewer = FindVisualChild<ScrollViewer>(listBox);
        if (scrollViewer == null)
            return;

        if (listBox.DataContext is LogTabViewModel tab)
            tab.UpdateViewportLineCount(MeasureViewportLineCount(listBox));

        var itemIndex = listBox.Items.IndexOf(item);
        var viewportHeight = (int)scrollViewer.ViewportHeight;
        var targetOffset = Math.Max(0, itemIndex - viewportHeight / 2);
        targetOffset = Math.Min(targetOffset, Math.Max(0, listBox.Items.Count - viewportHeight));
        scrollViewer.ScrollToVerticalOffset(targetOffset);
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

        var delta = e.Delta > 0 ? -3 : 3;
        tab.ScrollPosition = Math.Max(0, Math.Min(tab.MaxScrollPosition, tab.ScrollPosition + delta));
        e.Handled = true;
    }

    private void LogListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            return;

        if (sender is not ListBox listBox)
            return;

        if (TryCopySelectedLines(listBox))
            e.Handled = true;
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
