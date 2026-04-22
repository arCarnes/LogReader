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
    private string? _pendingSelectedTabRealizationTabInstanceId;

    public TabStripView()
    {
        InitializeComponent();
        Loaded += (_, _) => BringSelectedTabHeaderIntoView();
        DataContextChanged += (_, _) =>
        {
            if (_subscribedViewModel != null)
                _subscribedViewModel.PropertyChanged -= ViewModel_PropertyChanged;

            _lastSelectedTabForHeaderVisibility = null;
            _pendingSelectedTabRealizationTabInstanceId = null;
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

        _pendingSelectedTabRealizationTabInstanceId = null;
        Dispatcher.InvokeAsync(
            BringSelectedTabHeaderIntoView,
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void BringSelectedTabHeaderIntoView()
    {
        if (ViewModel?.SelectedTab == null)
            return;

        var selectedTab = ViewModel.SelectedTab;
        if (TabHeaderListBox.ItemContainerGenerator.ContainerFromItem(selectedTab) is not ListBoxItem selectedItem)
        {
            TabHeaderListBox.ScrollIntoView(selectedTab);
            ScheduleSelectedTabRealizationRetry(selectedTab);
            return;
        }

        selectedItem.BringIntoView();
        _pendingSelectedTabRealizationTabInstanceId = null;

        var orderedTabs = ViewModel.GetFilteredTabsSnapshot();
        var selectedIndex = IndexOfTab(orderedTabs, selectedTab);
        if (selectedIndex < 0)
        {
            _lastSelectedTabForHeaderVisibility = selectedTab;
            return;
        }

        var lastSelectedIndex = _lastSelectedTabForHeaderVisibility != null
            ? IndexOfTab(orderedTabs, _lastSelectedTabForHeaderVisibility)
            : -1;

        var movedLeft = lastSelectedIndex >= 0 && selectedIndex < lastSelectedIndex;
        var adjacentIndex = movedLeft ? selectedIndex - 1 : selectedIndex + 1;
        EnsureAdjacentTabVisible(orderedTabs, adjacentIndex, movedLeft);
        _lastSelectedTabForHeaderVisibility = selectedTab;
    }

    internal static bool ShouldRetrySelectedTabRealization(string? pendingTabInstanceId, LogTabViewModel selectedTab)
        => !string.Equals(pendingTabInstanceId, selectedTab.TabInstanceId, StringComparison.Ordinal);

    private void ScheduleSelectedTabRealizationRetry(LogTabViewModel selectedTab)
    {
        if (!ShouldRetrySelectedTabRealization(_pendingSelectedTabRealizationTabInstanceId, selectedTab))
            return;

        _pendingSelectedTabRealizationTabInstanceId = selectedTab.TabInstanceId;
        Dispatcher.InvokeAsync(
            () =>
            {
                if (ViewModel?.SelectedTab == null ||
                    !string.Equals(ViewModel.SelectedTab.TabInstanceId, selectedTab.TabInstanceId, StringComparison.Ordinal))
                {
                    return;
                }

                BringSelectedTabHeaderIntoView();
            },
            System.Windows.Threading.DispatcherPriority.Loaded);
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
        if (ViewModel?.IsLoadAffectingActionFrozen == true)
            return;

        if (sender is MenuItem mi && mi.Tag is LogTabViewModel tab)
            ViewModel?.TogglePinTab(tab);
    }

    private async void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel?.IsLoadAffectingActionFrozen == true)
            return;

        if (sender is MenuItem mi && mi.Tag is LogTabViewModel tab)
            await viewModel!.RunViewActionAsync(() => viewModel.CloseTabCommand.ExecuteAsync(tab));
    }

    private async void CloseOtherTabs_Click(object sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel?.IsLoadAffectingActionFrozen == true)
            return;

        if (sender is MenuItem mi && mi.Tag is LogTabViewModel tab)
            await viewModel!.RunViewActionAsync(() => viewModel.CloseOtherTabsAsync(tab));
    }

    private async void CloseAllButPinned_Click(object sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel?.IsLoadAffectingActionFrozen == true)
            return;

        if (viewModel != null)
            await viewModel.RunViewActionAsync(() => viewModel.CloseAllButPinnedAsync());
    }

    private async void CloseAllTabs_Click(object sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel?.IsLoadAffectingActionFrozen == true)
            return;

        if (viewModel != null)
            await viewModel.RunViewActionAsync(() => viewModel.CloseAllTabsAsync());
    }

    private void TabOverflow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || ViewModel == null)
            return;

        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        foreach (var tab in ViewModel.GetFilteredTabsSnapshot())
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

    private static int IndexOfTab(IReadOnlyList<LogTabViewModel> orderedTabs, LogTabViewModel tab)
    {
        for (var i = 0; i < orderedTabs.Count; i++)
        {
            if (ReferenceEquals(orderedTabs[i], tab))
                return i;
        }

        return -1;
    }
}
