namespace LogReader.App.Views;

using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using LogReader.App.Models;
using LogReader.App.ViewModels;
using LogReader.Core.Models;

public partial class DashboardTreeView : UserControl
{
    private const string GroupDragFormat = "LogReader.GroupDrag";
    private const string ClearModifierMenuHeader = "Clear Modifier";

    private Point? _dragStartPoint;
    private LogGroupViewModel? _dragSourceGroup;
    private TreeDropAdorner? _dropAdorner;

    public DashboardTreeView()
    {
        InitializeComponent();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private sealed record ModifierMenuRequest(LogGroupViewModel? Group, int DaysBack, IReadOnlyList<ReplacementPattern> Patterns, bool IsAdHoc);

    private async void GroupRow_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var current = e.OriginalSource as DependencyObject;
        while (current != null)
        {
            if (current is Button || current is TextBox)
                return;

            if (current == sender)
                break;

            current = VisualTreeHelper.GetParent(current);
        }

        if (e.ClickCount != 1 || sender is not FrameworkElement { DataContext: LogGroupViewModel group })
            return;

        _dragStartPoint = e.GetPosition(this);
        _dragSourceGroup = group;

        if (ViewModel == null)
            return;

        if (group.Kind == LogGroupKind.Dashboard)
        {
            var viewModel = ViewModel;
            var wasActiveDashboard = string.Equals(ViewModel.ActiveDashboardId, group.Id, StringComparison.Ordinal);
            if (!wasActiveDashboard)
                viewModel.ToggleGroupSelection(group);

            await viewModel.RunViewActionAsync(() => viewModel.OpenGroupFilesAsync(group));
            return;
        }

        ViewModel.ToggleGroupSelection(group);
    }

    private void GroupRow_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStartPoint == null || _dragSourceGroup == null || e.LeftButton != MouseButtonState.Pressed)
        {
            _dragStartPoint = null;
            _dragSourceGroup = null;
            return;
        }

        var position = e.GetPosition(this);
        var delta = position - _dragStartPoint.Value;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var source = _dragSourceGroup;
        _dragStartPoint = null;
        _dragSourceGroup = null;

        var data = new DataObject(GroupDragFormat, source);
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move);
    }

    private void GroupExpand_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LogGroupViewModel group })
        {
            if (!group.CanExpand)
            {
                e.Handled = true;
                return;
            }

            group.IsExpanded = !group.IsExpanded;
        }

        e.Handled = true;
    }

    private void GroupName_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement { DataContext: LogGroupViewModel group })
        {
            group.BeginEdit();
            e.Handled = true;
        }
    }

    private async void GroupNameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: LogGroupViewModel group })
            return;

        if (e.Key == Key.Return)
        {
            var viewModel = ViewModel;
            if (viewModel == null)
                return;

            await viewModel.RunViewActionAsync(() => group.CommitEditAsync());
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            group.CancelEdit();
            e.Handled = true;
        }
    }

    private async void GroupNameTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: LogGroupViewModel group } && group.IsEditing)
        {
            var viewModel = ViewModel;
            if (viewModel == null)
                return;

            await viewModel.RunViewActionAsync(() => group.CommitEditAsync());
        }
    }

    private void GroupNameTextBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox textBox && (bool)e.NewValue)
        {
            textBox.Focus();
            textBox.SelectAll();
        }
    }

    private async void MoveGroupUp_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: LogGroupViewModel group })
        {
            var viewModel = ViewModel;
            if (viewModel == null)
                return;

            await viewModel.RunViewActionAsync(() => viewModel.MoveGroupUpAsync(group));
        }
    }

    private async void MoveGroupDown_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: LogGroupViewModel group })
        {
            var viewModel = ViewModel;
            if (viewModel == null)
                return;

            await viewModel.RunViewActionAsync(() => viewModel.MoveGroupDownAsync(group));
        }
    }

    private async void AddChildFolder_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: LogGroupViewModel group } && ViewModel != null)
        {
            var viewModel = ViewModel;
            await viewModel.RunViewActionAsync(async () =>
            {
                await viewModel.CreateChildGroupAsync(group, LogGroupKind.Branch);
            });
        }
    }

    private async void AddChildDashboard_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: LogGroupViewModel group } && ViewModel != null)
        {
            var viewModel = ViewModel;
            await viewModel.RunViewActionAsync(async () =>
            {
                await viewModel.CreateChildGroupAsync(group, LogGroupKind.Dashboard);
            });
        }
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: LogGroupViewModel group })
        {
            var viewModel = ViewModel;
            if (viewModel == null)
                return;

            await viewModel.RunViewActionAsync(() => viewModel.AddFilesToDashboardAsync(group));
        }
    }

    private async void BulkOpenFiles_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: LogGroupViewModel group })
        {
            var viewModel = ViewModel;
            if (viewModel == null)
                return;

            await viewModel.RunViewActionAsync(() => viewModel.BulkAddFilesToDashboardAsync(group));
        }
    }

    private async void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: LogGroupViewModel group })
        {
            var viewModel = ViewModel;
            if (viewModel == null)
                return;

            await viewModel.RunViewActionAsync(() => viewModel.DeleteGroupCommand.ExecuteAsync(group));
        }
    }

    private async void DashboardModifiers_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem &&
            menuItem.DataContext is LogGroupViewModel group &&
            ViewModel != null)
        {
            await ViewModel.RunViewActionAsync(() => PopulateModifierMenuAsync(menuItem, group, isAdHoc: false));
        }
    }

    private async void AdHocModifiers_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && ViewModel != null)
            await ViewModel.RunViewActionAsync(() => PopulateModifierMenuAsync(menuItem, group: null, isAdHoc: true));
    }

    private async Task PopulateModifierMenuAsync(MenuItem menuItem, LogGroupViewModel? group, bool isAdHoc)
    {
        menuItem.Items.Clear();
        var patterns = await ViewModel!.LoadReplacementPatternsAsync();

        if (patterns.Count == 0)
        {
            menuItem.Items.Add(new MenuItem
            {
                Header = "No date rolling patterns configured",
                IsEnabled = false
            });
            return;
        }

        for (var daysBack = 1; daysBack <= 7; daysBack++)
        {
            menuItem.Items.Add(new MenuItem
            {
                Header = $"T-{daysBack}",
                Tag = new ModifierMenuRequest(group, daysBack, patterns, isAdHoc)
            });
        }

        AttachModifierClickHandlers(menuItem);

        if (isAdHoc ? ViewModel.HasAdHocModifier() : group != null && ViewModel.HasDashboardModifier(group))
        {
            menuItem.Items.Add(new Separator());
            menuItem.Items.Add(new MenuItem
            {
                Header = ClearModifierMenuHeader,
                Tag = group
            });
        }

        AttachClearModifierClickHandlers(menuItem, isAdHoc);
    }

    private void AttachModifierClickHandlers(ItemsControl menuRoot)
    {
        foreach (var item in menuRoot.Items.OfType<MenuItem>())
        {
            if (item.Tag is ModifierMenuRequest)
                item.Click += ApplyModifierMenuItem_Click;

            if (item.Items.Count > 0)
                AttachModifierClickHandlers(item);
        }
    }

    private void AttachClearModifierClickHandlers(ItemsControl menuRoot, bool isAdHoc)
    {
        foreach (var item in menuRoot.Items.OfType<MenuItem>())
        {
            if (!string.Equals(item.Header as string, ClearModifierMenuHeader, StringComparison.Ordinal))
                continue;

            if (isAdHoc)
                item.Click += ClearAdHocModifierMenuItem_Click;
            else
                item.Click += ClearDashboardModifierMenuItem_Click;
        }
    }

    private async void ApplyModifierMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: ModifierMenuRequest request } ||
            ViewModel == null)
        {
            return;
        }

        var viewModel = ViewModel;
        await viewModel.RunViewActionAsync(async () =>
        {
            if (request.IsAdHoc)
                await viewModel.ApplyAdHocModifierAsync(request.DaysBack, request.Patterns);
            else if (request.Group != null)
                await viewModel.ApplyDashboardModifierAsync(request.Group, request.DaysBack, request.Patterns);
        });
    }

    private async void ClearDashboardModifierMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: LogGroupViewModel group } && ViewModel != null)
        {
            var viewModel = ViewModel;
            await viewModel.RunViewActionAsync(() => viewModel.ClearDashboardModifierAsync(group));
        }
    }

    private async void ClearAdHocModifierMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            var viewModel = ViewModel;
            await viewModel.RunViewActionAsync(() => viewModel.ClearAdHocModifierAsync());
        }
    }

    private void OpenFileLocation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem ||
            menuItem.Parent is not ContextMenu { PlacementTarget: FrameworkElement { DataContext: GroupFileMemberViewModel fileVm } })
        {
            return;
        }

        var directory = Path.GetDirectoryName(fileVm.FilePath);
        Process? process = null;
        if (File.Exists(fileVm.FilePath))
            process = Process.Start("explorer.exe", $"/select,\"{fileVm.FilePath}\"");
        else if (directory != null && Directory.Exists(directory))
            process = Process.Start("explorer.exe", directory);

        process?.Dispose();
    }

    private void CopyFullPath_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem ||
            menuItem.Parent is not ContextMenu { PlacementTarget: FrameworkElement { DataContext: GroupFileMemberViewModel fileVm } })
        {
            return;
        }

        Clipboard.SetText(fileVm.FilePath);
        e.Handled = true;
    }

    private async void OpenMemberFile_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            element.DataContext is not GroupFileMemberViewModel fileVm ||
            element.Tag is not LogGroupViewModel groupVm ||
            ViewModel == null)
        {
            return;
        }

        var viewModel = ViewModel;
        await viewModel.RunViewActionAsync(async () =>
        {
            if (groupVm.Kind == LogGroupKind.Dashboard && viewModel.ActiveDashboardId != groupVm.Id)
            {
                await viewModel.OpenGroupFilesAsync(groupVm);
                viewModel.ToggleGroupSelection(groupVm);
            }

            await viewModel.OpenFilePathAsync(fileVm.FilePath);
        });
        e.Handled = true;
    }

    private async void RemoveDashboardFile_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetDashboardFileMenuContext(sender, out var fileVm, out var groupVm) ||
            ViewModel == null)
        {
            return;
        }

        var viewModel = ViewModel;
        await viewModel.RunViewActionAsync(() => viewModel.RemoveFileFromDashboardAsync(groupVm, fileVm.FileId));
        e.Handled = true;
    }

    internal static bool TryGetDashboardFileMenuContext(
        object? sender,
        [NotNullWhen(true)] out GroupFileMemberViewModel? fileVm,
        [NotNullWhen(true)] out LogGroupViewModel? groupVm)
    {
        if (sender is MenuItem menuItem &&
            menuItem.Parent is ContextMenu
            {
                PlacementTarget: FrameworkElement
                {
                    DataContext: GroupFileMemberViewModel resolvedFileVm,
                    Tag: LogGroupViewModel resolvedGroupVm
                }
            })
        {
            fileVm = resolvedFileVm;
            groupVm = resolvedGroupVm;
            return true;
        }

        fileVm = null;
        groupVm = null;
        return false;
    }

    private void GroupTree_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(GroupDragFormat) || ViewModel == null)
            return;

        var source = (LogGroupViewModel)e.Data.GetData(GroupDragFormat)!;
        var (target, container) = HitTestGroupItem(GroupItemsControl, e.GetPosition(GroupItemsControl));
        if (target == null || container == null)
        {
            e.Effects = DragDropEffects.None;
            HideDropAdorner();
            e.Handled = true;
            return;
        }

        var placement = GetDropPlacement(target, container, e);
        if (!ViewModel.CanMoveGroupTo(source, target, placement))
        {
            e.Effects = DragDropEffects.None;
            HideDropAdorner();
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        var bounds = container.TransformToAncestor(GroupItemsControl)
            .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
        EnsureDropAdorner(GroupItemsControl);
        _dropAdorner!.Update(bounds, placement);
        e.Handled = true;
    }

    private void GroupTree_DragLeave(object sender, DragEventArgs e)
    {
        HideDropAdorner();
    }

    private async void GroupTree_Drop(object sender, DragEventArgs e)
    {
        HideDropAdorner();

        if (!e.Data.GetDataPresent(GroupDragFormat) || ViewModel == null)
            return;

        var source = (LogGroupViewModel)e.Data.GetData(GroupDragFormat)!;
        var (target, container) = HitTestGroupItem(GroupItemsControl, e.GetPosition(GroupItemsControl));
        if (target == null || container == null)
            return;

        var placement = GetDropPlacement(target, container, e);
        var viewModel = ViewModel;
        await viewModel.RunViewActionAsync(() => viewModel.MoveGroupToAsync(source, target, placement));
        e.Handled = true;
    }

    private static DropPlacement GetDropPlacement(LogGroupViewModel target, FrameworkElement container, DragEventArgs e)
    {
        var position = e.GetPosition(container);
        var height = container.ActualHeight;
        if (target.Kind == LogGroupKind.Branch)
        {
            if (position.Y < height * 0.25)
                return DropPlacement.Before;

            if (position.Y > height * 0.75)
                return DropPlacement.After;

            return DropPlacement.Inside;
        }

        return position.Y < height * 0.5 ? DropPlacement.Before : DropPlacement.After;
    }

    private (LogGroupViewModel? group, FrameworkElement? container) HitTestGroupItem(ItemsControl itemsControl, Point position)
    {
        var hit = itemsControl.InputHitTest(position) as DependencyObject;
        while (hit != null)
        {
            if (hit is FrameworkElement element && element.DataContext is LogGroupViewModel group)
            {
                var container = element;
                var parent = VisualTreeHelper.GetParent(element);
                while (parent != null && parent != itemsControl)
                {
                    if (parent is FrameworkElement parentElement && parentElement.DataContext == group)
                        container = parentElement;

                    parent = VisualTreeHelper.GetParent(parent);
                }

                return (group, container);
            }

            hit = VisualTreeHelper.GetParent(hit);
        }

        return (null, null);
    }

    private void EnsureDropAdorner(UIElement adornedElement)
    {
        if (_dropAdorner?.AdornedElement == adornedElement)
            return;

        HideDropAdorner();
        _dropAdorner = new TreeDropAdorner(adornedElement);
        AdornerLayer.GetAdornerLayer(adornedElement)?.Add(_dropAdorner);
    }

    private void HideDropAdorner()
    {
        if (_dropAdorner == null)
            return;

        var layer = AdornerLayer.GetAdornerLayer(_dropAdorner.AdornedElement);
        layer?.Remove(_dropAdorner);
        _dropAdorner = null;
    }
}
