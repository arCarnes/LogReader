namespace LogReader.App.Views;

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using LogReader.App.ViewModels;

public partial class TabStripView : UserControl
{
    private MainViewModel? _subscribedViewModel;
    private LogTabViewModel? _lastSelectedTabForHeaderVisibility;

    public TabStripView()
    {
        InitializeComponent();
        Loaded += (_, _) => BringSelectedTabHeaderIntoView();
        DataContextChanged += (_, _) =>
        {
            if (_subscribedViewModel != null)
                _subscribedViewModel.PropertyChanged -= ViewModel_PropertyChanged;

            _lastSelectedTabForHeaderVisibility = null;
            _subscribedViewModel = ViewModel;
            if (_subscribedViewModel != null)
                _subscribedViewModel.PropertyChanged += ViewModel_PropertyChanged;
        };
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.SelectedTab))
            return;

        Dispatcher.InvokeAsync(
            BringSelectedTabHeaderIntoView,
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void BringSelectedTabHeaderIntoView()
    {
        if (ViewModel?.SelectedTab == null)
            return;

        if (TabHeaderListBox.ItemContainerGenerator.ContainerFromItem(ViewModel.SelectedTab) is not ListBoxItem selectedItem)
            return;

        selectedItem.BringIntoView();

        var orderedTabs = ViewModel.FilteredTabs.ToList();
        var selectedIndex = orderedTabs.IndexOf(ViewModel.SelectedTab);
        if (selectedIndex < 0)
        {
            _lastSelectedTabForHeaderVisibility = ViewModel.SelectedTab;
            return;
        }

        var lastSelectedIndex = _lastSelectedTabForHeaderVisibility != null
            ? orderedTabs.IndexOf(_lastSelectedTabForHeaderVisibility)
            : -1;

        var movedLeft = lastSelectedIndex >= 0 && selectedIndex < lastSelectedIndex;
        var adjacentIndex = movedLeft ? selectedIndex - 1 : selectedIndex + 1;
        EnsureAdjacentTabVisible(orderedTabs, adjacentIndex, movedLeft);
        _lastSelectedTabForHeaderVisibility = ViewModel.SelectedTab;
    }

    private void EnsureAdjacentTabVisible(IReadOnlyList<LogTabViewModel> orderedTabs, int adjacentIndex, bool ensureLeftNeighbor)
    {
        if (adjacentIndex < 0 || adjacentIndex >= orderedTabs.Count)
            return;

        var adjacentTab = orderedTabs[adjacentIndex];
        if (TabHeaderListBox.ItemContainerGenerator.ContainerFromItem(adjacentTab) is not ListBoxItem adjacentItem)
        {
            TabHeaderListBox.ScrollIntoView(adjacentTab);
            return;
        }

        var adjacentBounds = adjacentItem.TransformToAncestor(TabHeaderListBox)
            .TransformBounds(new Rect(0, 0, adjacentItem.ActualWidth, adjacentItem.ActualHeight));

        if (ensureLeftNeighbor)
        {
            if (adjacentBounds.Left < -0.5)
                TabHeaderListBox.ScrollIntoView(adjacentTab);
            return;
        }

        if (adjacentBounds.Right > TabHeaderListBox.ActualWidth + 0.5)
            TabHeaderListBox.ScrollIntoView(adjacentTab);
    }

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

    private void TabOverflow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || ViewModel == null)
            return;

        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        foreach (var tab in ViewModel.FilteredTabs)
        {
            var prefix = tab.IsPinned ? "[P] " : string.Empty;
            var item = new MenuItem
            {
                Header = $"{prefix}{tab.FileName}",
                IsCheckable = true,
                IsChecked = ReferenceEquals(ViewModel.SelectedTab, tab),
                Tag = tab
            };
            item.Click += OverflowTabItem_Click;
            menu.Items.Add(item);
        }

        if (menu.Items.Count == 0)
        {
            menu.Items.Add(new MenuItem
            {
                Header = "(No tabs)",
                IsEnabled = false
            });
        }

        menu.IsOpen = true;
    }

    private void OverflowTabItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: LogTabViewModel tab } && ViewModel != null)
            ViewModel.SelectedTab = tab;
    }
}
