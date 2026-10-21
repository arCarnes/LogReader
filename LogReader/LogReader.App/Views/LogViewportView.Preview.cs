namespace LogReader.App.Views;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LogReader.App.ViewModels;

public partial class LogViewportView
{
    private bool _positioningPreview;

    private void PreviewListBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBox { DataContext: LogTabViewModel tab } list || list.Items.Count == 0)
            return;
        _positioningPreview = true;
        var anchor = !tab.AutoScrollEnabled && tab.PreviewAnchor is { } offset
            ? tab.PreviewLines.FirstOrDefault(line => line.ByteOffset == offset) : null;
        list.ScrollIntoView(anchor ?? list.Items[^1]);
        foreach (var line in tab.PreviewLines.Where(line => tab.PreviewSelectedOffsets.Contains(line.ByteOffset)))
            list.SelectedItems.Add(line);
        list.UpdateLayout();
        if (tab.AutoScrollEnabled || tab.PreviewAnchor == null || anchor != null)
            RememberPreview(list, tab);
        _positioningPreview = false;
    }

    private void PreviewListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_positioningPreview && sender is ListBox { DataContext: LogTabViewModel tab } list && tab.HasPreview)
            RememberPreview(list, tab);
    }

    private void PreviewListBox_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_positioningPreview || sender is not ListBox { DataContext: LogTabViewModel tab } list || !tab.HasPreview)
            return;
        RememberPreview(list, tab);
        if (e.VerticalChange != 0 && tab.IsIndexReady == false && ViewModel?.GlobalAutoScrollEnabled == true)
            ViewModel.GlobalAutoScrollEnabled = false;
    }

    private void PreviewListBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ViewModel?.GlobalAutoScrollEnabled == true)
            ViewModel.GlobalAutoScrollEnabled = false;
    }

    private static void RememberPreview(ListBox list, LogTabViewModel tab)
    {
        var scroll = FindPreviewScrollViewer(list);
        var first = Math.Clamp((int)(scroll?.VerticalOffset ?? 0), 0, Math.Max(0, tab.PreviewLines.Count - 1));
        tab.RememberPreviewPosition(tab.PreviewLines.Count > 0 ? tab.PreviewLines[first].ByteOffset : null,
            list.SelectedItems.OfType<LogPreviewLineViewModel>().Select(line => line.ByteOffset));
    }

    private static ListBox? FindPreviewList(DependencyObject root, LogTabViewModel tab)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ListBox { Name: "PreviewListBox" } list && ReferenceEquals(list.DataContext, tab)) return list;
            if (FindPreviewList(child, tab) is { } found) return found;
        }
        return null;
    }

    private static ScrollViewer? FindPreviewScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer scroll) return scroll;
            var found = FindPreviewScrollViewer(child);
            if (found != null) return found;
        }
        return null;
    }

    private void CopyPreview_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = sender is ListBox { DataContext: LogTabViewModel tab } list && tab.HasPreview &&
            ReferenceEquals(ViewModel?.SelectedTab, tab) && list.SelectedItems.Count > 0;
        e.Handled = true;
    }

    private async void CopyPreview_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is ListBox { DataContext: LogTabViewModel tab } list && tab.HasPreview && ReferenceEquals(ViewModel?.SelectedTab, tab))
            await ViewModel!.RunViewActionAsync(() =>
            {
                _copyTextToClipboard(tab.GetPreviewCopyText(list.SelectedItems.OfType<LogPreviewLineViewModel>()));
                return Task.CompletedTask;
            }, "Copy Selected Preview Lines Failed");
    }
}
