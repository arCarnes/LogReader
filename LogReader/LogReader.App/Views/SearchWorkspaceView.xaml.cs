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

        TryPrepareSelectionForContextMenu(listBox, TryGetHitRowFromSource(listBox, e.OriginalSource as DependencyObject));
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
    {
        ArgumentNullException.ThrowIfNull(listBox);

        if (hitRow == null || listBox.SelectedItems.Contains(hitRow))
            return false;

        listBox.SelectedItems.Clear();
        listBox.SelectedItem = hitRow;
        listBox.Focus();

        if (listBox.ItemContainerGenerator.ContainerFromItem(hitRow) is ListBoxItem container)
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

    private static async Task NavigateToHitAsync(SearchResultHitRowViewModel hitRow)
    {
        await hitRow.FileResult.NavigateToHitCommand.ExecuteAsync(hitRow.Hit);
    }

    private static SearchResultHitRowViewModel? TryGetHitRowFromSource(ListBox listBox, DependencyObject? originalSource)
    {
        ArgumentNullException.ThrowIfNull(listBox);

        var itemContainer = FindAncestor<ListBoxItem>(originalSource);
        return itemContainer?.DataContext as SearchResultHitRowViewModel;
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
