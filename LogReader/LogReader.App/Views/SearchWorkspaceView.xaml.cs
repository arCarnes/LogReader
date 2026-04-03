namespace LogReader.App.Views;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using LogReader.App.ViewModels;

public partial class SearchWorkspaceView : UserControl
{
    public SearchWorkspaceView()
    {
        InitializeComponent();
    }

    public void FocusActiveTabPrimaryInput()
    {
        var target = FilterQueryBox.IsKeyboardFocusWithin ? FilterQueryBox : SearchBox;

        target.Focus();
        target.SelectAll();
    }

    private async void SearchResultsList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListBox listBox)
            return;

        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (TryCopySelectedHits(listBox))
                e.Handled = true;

            return;
        }

        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None)
            return;

        var hit = GetNavigableSelectedHit(listBox);
        if (hit == null)
            return;

        if (listBox.SelectedItem is SearchResultHitRowViewModel selectedRow)
        {
            await NavigateToHitAsync(selectedRow);
            e.Handled = true;
            return;
        }

        var selectedHitRow = listBox.Items
            .OfType<SearchResultHitRowViewModel>()
            .FirstOrDefault(row => ReferenceEquals(row.Hit, hit) && listBox.SelectedItems.Contains(row));
        if (selectedHitRow == null)
            return;

        await NavigateToHitAsync(selectedHitRow);
        e.Handled = true;
    }

    private void SearchResultsList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox)
            return;

        TryPrepareSelectionForContextMenu(listBox, TryGetResultRowFromSource(listBox, e.OriginalSource as DependencyObject));
    }

    private async void SearchResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox ||
            TryGetHitRowFromSource(listBox, e.OriginalSource as DependencyObject) is not SearchResultHitRowViewModel hitRow)
        {
            return;
        }

        await NavigateToHitAsync(hitRow);
        e.Handled = true;
    }

    private void CopySelectedSearchHits_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem ||
            menuItem.Parent is not ContextMenu { PlacementTarget: ListBox listBox })
        {
            return;
        }

        if (TryCopySelectedHits(listBox))
            e.Handled = true;
    }

    private void CollapseCurrentSearchResults_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem ||
            menuItem.Parent is not ContextMenu { PlacementTarget: ListBox listBox })
        {
            return;
        }

        if (TryCollapseCurrentResults(listBox))
            e.Handled = true;
    }

    private void CollapseAllSearchResults_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem ||
            menuItem.Parent is not ContextMenu { PlacementTarget: ListBox listBox })
        {
            return;
        }

        if (CollapseAllResults(listBox))
            e.Handled = true;
    }

    internal static IReadOnlyList<string> GetSelectedHitLineTexts(ListBox listBox)
    {
        ArgumentNullException.ThrowIfNull(listBox);

        var selectedHits = listBox.SelectedItems
            .OfType<SearchResultHitRowViewModel>()
            .ToHashSet();

        return listBox.Items
            .OfType<SearchResultHitRowViewModel>()
            .Where(selectedHits.Contains)
            .Select(hit => hit.Hit.LineText)
            .ToList();
    }

    internal static bool TryPrepareSelectionForContextMenu(ListBox listBox, SearchResultHitRowViewModel? hitRow)
        => TryPrepareSelectionForContextMenu(listBox, hitRow as SearchResultsRowViewModel);

    internal static bool TryPrepareSelectionForContextMenu(ListBox listBox, SearchResultsRowViewModel? row)
    {
        ArgumentNullException.ThrowIfNull(listBox);

        if (row == null || listBox.SelectedItems.Contains(row))
            return false;

        listBox.SelectedItems.Clear();
        listBox.SelectedItem = row;
        listBox.Focus();

        if (listBox.ItemContainerGenerator.ContainerFromItem(row) is ListBoxItem container)
            container.Focus();

        return true;
    }

    internal static SearchHitViewModel? GetNavigableSelectedHit(ListBox listBox)
    {
        ArgumentNullException.ThrowIfNull(listBox);

        return (listBox.SelectedItem as SearchResultHitRowViewModel)?.Hit ??
               listBox.Items
                   .OfType<SearchResultHitRowViewModel>()
                   .FirstOrDefault(hit => listBox.SelectedItems.Contains(hit))
                   ?.Hit;
    }

    internal static bool TryCopySelectedHits(ListBox listBox)
    {
        var selectedLines = GetSelectedHitLineTexts(listBox);
        if (selectedLines.Count == 0)
            return false;

        Clipboard.SetText(string.Join(Environment.NewLine, selectedLines));
        return true;
    }

    internal static bool TryCollapseCurrentResults(ListBox listBox)
    {
        ArgumentNullException.ThrowIfNull(listBox);

        if (GetCurrentFileResult(listBox) is not { IsExpanded: true } fileResult)
            return false;

        fileResult.IsExpanded = false;
        return true;
    }

    internal static bool CollapseAllResults(ListBox listBox)
    {
        ArgumentNullException.ThrowIfNull(listBox);

        var anyChanged = false;
        var fileResults = listBox.Items
            .OfType<SearchResultsRowViewModel>()
            .Select(row => row.FileResult)
            .Distinct()
            .ToList();

        foreach (var fileResult in fileResults)
        {
            if (!fileResult.IsExpanded)
                continue;

            fileResult.IsExpanded = false;
            anyChanged = true;
        }

        return anyChanged;
    }

    private static async Task NavigateToHitAsync(SearchResultHitRowViewModel hitRow)
    {
        await hitRow.FileResult.NavigateToHitCommand.ExecuteAsync(hitRow.Hit);
    }

    private static FileSearchResultViewModel? GetCurrentFileResult(ListBox listBox)
    {
        return (listBox.SelectedItem as SearchResultsRowViewModel)?.FileResult ??
               listBox.Items
                   .OfType<SearchResultsRowViewModel>()
                   .FirstOrDefault(row => listBox.SelectedItems.Contains(row))
                   ?.FileResult;
    }

    private static SearchResultHitRowViewModel? TryGetHitRowFromSource(ListBox listBox, DependencyObject? originalSource)
    {
        ArgumentNullException.ThrowIfNull(listBox);

        var itemContainer = FindAncestor<ListBoxItem>(originalSource);
        return itemContainer?.DataContext as SearchResultHitRowViewModel;
    }

    private static SearchResultsRowViewModel? TryGetResultRowFromSource(ListBox listBox, DependencyObject? originalSource)
    {
        ArgumentNullException.ThrowIfNull(listBox);

        var itemContainer = FindAncestor<ListBoxItem>(originalSource);
        return itemContainer?.DataContext as SearchResultsRowViewModel;
    }

    private static T? FindAncestor<T>(DependencyObject? dependencyObject) where T : DependencyObject
    {
        var current = dependencyObject;
        while (current != null)
        {
            if (current is T match)
                return match;

            current = GetParentObject(current);
        }

        return null;
    }

    internal static DependencyObject? GetParentObject(DependencyObject? dependencyObject)
    {
        return dependencyObject switch
        {
            null => null,
            Visual or Visual3D => VisualTreeHelper.GetParent(dependencyObject),
            FrameworkContentElement frameworkContentElement => frameworkContentElement.Parent,
            ContentElement contentElement => ContentOperations.GetParent(contentElement),
            _ => null
        };
    }
}
