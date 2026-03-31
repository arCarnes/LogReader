namespace LogReader.App.Views;

using System.Windows;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using LogReader.App.ViewModels;

public partial class SearchWorkspaceView : UserControl
{
    private MainViewModel? _subscribedViewModel;

    public SearchWorkspaceView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_subscribedViewModel != null)
                _subscribedViewModel.PropertyChanged -= ViewModel_PropertyChanged;

            _subscribedViewModel = ViewModel;
            if (_subscribedViewModel != null)
                _subscribedViewModel.PropertyChanged += ViewModel_PropertyChanged;
        };
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsSearchPanelOpen) && ViewModel?.IsSearchPanelOpen == true)
        {
            Dispatcher.InvokeAsync(
                FocusActiveTabPrimaryInput,
                System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    public void FocusActiveTabPrimaryInput()
    {
        var target = WorkspaceTabs.SelectedIndex switch
        {
            1 => FilterQueryBox,
            2 => GoToTimestampBox,
            _ => SearchBox
        };

        target.Focus();
        target.SelectAll();
    }

    private async void SearchHitsList_PreviewKeyDown(object sender, KeyEventArgs e)
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

        await NavigateToHitAsync(listBox, hit);
        e.Handled = true;
    }

    private void SearchHitsList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox)
            return;

        TryPrepareSelectionForContextMenu(listBox, TryGetHitFromSource(listBox, e.OriginalSource as DependencyObject));
    }

    private async void SearchHitsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox ||
            TryGetHitFromSource(listBox, e.OriginalSource as DependencyObject) is not SearchHitViewModel hit)
        {
            return;
        }

        await NavigateToHitAsync(listBox, hit);
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
            .OfType<SearchHitViewModel>()
            .ToHashSet();

        return listBox.Items
            .OfType<SearchHitViewModel>()
            .Where(selectedHits.Contains)
            .Select(hit => hit.LineText)
            .ToList();
    }

    internal static bool TryPrepareSelectionForContextMenu(ListBox listBox, SearchHitViewModel? hit)
    {
        ArgumentNullException.ThrowIfNull(listBox);

        if (hit == null || listBox.SelectedItems.Contains(hit))
            return false;

        listBox.SelectedItems.Clear();
        listBox.SelectedItem = hit;
        listBox.Focus();

        if (listBox.ItemContainerGenerator.ContainerFromItem(hit) is ListBoxItem container)
            container.Focus();

        return true;
    }

    internal static SearchHitViewModel? GetNavigableSelectedHit(ListBox listBox)
    {
        ArgumentNullException.ThrowIfNull(listBox);

        return listBox.SelectedItem as SearchHitViewModel ??
               listBox.Items
                   .OfType<SearchHitViewModel>()
                   .FirstOrDefault(hit => listBox.SelectedItems.Contains(hit));
    }

    internal static bool TryCopySelectedHits(ListBox listBox)
    {
        var selectedLines = GetSelectedHitLineTexts(listBox);
        if (selectedLines.Count == 0)
            return false;

        Clipboard.SetText(string.Join(Environment.NewLine, selectedLines));
        return true;
    }

    private static async Task NavigateToHitAsync(ListBox listBox, SearchHitViewModel hit)
    {
        if (listBox.DataContext is not FileSearchResultViewModel fileResult)
            return;

        await fileResult.NavigateToHitCommand.ExecuteAsync(hit);
    }

    private static SearchHitViewModel? TryGetHitFromSource(ListBox listBox, DependencyObject? originalSource)
    {
        ArgumentNullException.ThrowIfNull(listBox);

        var itemContainer = FindAncestor<ListBoxItem>(originalSource);
        return itemContainer?.DataContext as SearchHitViewModel;
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
