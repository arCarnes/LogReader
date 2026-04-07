namespace LogReader.App.Views;

using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using LogReader.App.Models;
using LogReader.App.ViewModels;
using LogReader.Core.Models;

public partial class DashboardTreeView : UserControl
{
    private const string GroupDragFormat = "LogReader.GroupDrag";
    private const string DashboardFileDragFormat = "LogReader.DashboardFileDrag";
    private const string ClearModifierMenuHeader = "Clear Modifier";

    private readonly MouseButtonEventHandler _hostWindowPreviewMouseDownHandler;
    private Point? _dragStartPoint;
    private LogGroupViewModel? _dragSourceGroup;
    private Point? _memberDragStartPoint;
    private GroupFileMemberViewModel? _dragSourceMemberFile;
    private LogGroupViewModel? _dragSourceMemberGroup;
    private TreeDropAdorner? _dropAdorner;
    private Window? _hostWindow;

    public DashboardTreeView()
    {
        _hostWindowPreviewMouseDownHandler = HostWindow_PreviewMouseDown;
        InitializeComponent();
        Loaded += DashboardTreeView_Loaded;
        Unloaded += DashboardTreeView_Unloaded;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private sealed record ModifierMenuRequest(LogGroupViewModel? Group, int DaysBack, IReadOnlyList<ReplacementPattern> Patterns, bool IsAdHoc);
    private sealed record DashboardFileDragRequest(LogGroupViewModel Group, GroupFileMemberViewModel File);

    private void DashboardTreeView_Loaded(object sender, RoutedEventArgs e)
    {
        var hostWindow = Window.GetWindow(this);
        if (ReferenceEquals(_hostWindow, hostWindow))
            return;

        UnsubscribeFromHostWindow();
        _hostWindow = hostWindow;
        _hostWindow?.AddHandler(UIElement.PreviewMouseDownEvent, _hostWindowPreviewMouseDownHandler, true);
    }

    private void DashboardTreeView_Unloaded(object sender, RoutedEventArgs e)
    {
        UnsubscribeFromHostWindow();
    }

    private void UnsubscribeFromHostWindow()
    {
        if (_hostWindow == null)
            return;

        _hostWindow.RemoveHandler(UIElement.PreviewMouseDownEvent, _hostWindowPreviewMouseDownHandler);
        _hostWindow = null;
    }

    internal static bool ShouldIgnoreGroupRowMouseDown(DependencyObject? originalSource, DependencyObject sender)
    {
        var current = originalSource;
        while (current != null)
        {
            if (current is Button || current is TextBox)
                return true;

            if (current == sender)
                break;

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private async void GroupRow_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1 || sender is not FrameworkElement { DataContext: LogGroupViewModel group })
            return;

        if (ShouldIgnoreGroupRowMouseDown(e.OriginalSource as DependencyObject, (DependencyObject)sender))
            return;

        _dragStartPoint = e.GetPosition(this);
        _dragSourceGroup = group;

        if (ViewModel == null)
            return;

        await ViewModel.HandleDashboardGroupInvokedAsync(group);
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

    private void RenameGroup_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetGroupFromRenameSource(sender, out var group))
        {
            ViewModel?.BeginDashboardTreeRename(group);
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

            await viewModel.CommitDashboardTreeRenameAsync(group);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ViewModel?.CancelDashboardTreeRename(group);
            e.Handled = true;
        }
    }

    private async void GroupNameTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: LogGroupViewModel group } && group.IsEditing)
            await CommitGroupEditAsync(group);
    }

    private void GroupNameTextBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox textBox && (bool)e.NewValue)
        {
            textBox.Focus();
            textBox.SelectAll();
        }
    }

    private async void HostWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var editingGroup = FindEditingGroup(ViewModel?.Groups);
        if (!ShouldCommitEditingGroupOnMouseDown(e.OriginalSource as DependencyObject, editingGroup))
            return;

        await CommitGroupEditAsync(editingGroup!);
    }

    internal static LogGroupViewModel? FindEditingGroup(IEnumerable<LogGroupViewModel>? groups)
    {
        if (groups == null)
            return null;

        return groups.FirstOrDefault(static group => group.IsEditing);
    }

    internal static bool ShouldCommitEditingGroupOnMouseDown(DependencyObject? originalSource, LogGroupViewModel? editingGroup)
    {
        return editingGroup != null &&
               editingGroup.IsEditing &&
               !IsWithinEditingTextBox(originalSource, editingGroup);
    }

    internal static bool IsWithinEditingTextBox(DependencyObject? source, LogGroupViewModel editingGroup)
    {
        var current = source;
        while (current != null)
        {
            if (current is TextBox textBox && ReferenceEquals(textBox.DataContext, editingGroup))
                return true;

            current = GetDependencyParent(current);
        }

        return false;
    }

    private static DependencyObject? GetDependencyParent(DependencyObject current)
    {
        return current switch
        {
            Visual or Visual3D => VisualTreeHelper.GetParent(current),
            FrameworkContentElement contentElement => contentElement.Parent,
            _ => null
        };
    }

    private async Task CommitGroupEditAsync(LogGroupViewModel group)
    {
        var viewModel = ViewModel;
        if (viewModel == null)
            return;

        await viewModel.CommitDashboardTreeRenameAsync(group);
    }

    internal static bool TryGetGroupFromRenameSource(
        object? sender,
        [NotNullWhen(true)] out LogGroupViewModel? group)
    {
        switch (sender)
        {
            case FrameworkElement { DataContext: LogGroupViewModel resolvedGroup }:
                group = resolvedGroup;
                return true;
            case MenuItem menuItem when menuItem.Parent is ContextMenu { PlacementTarget: FrameworkElement { DataContext: LogGroupViewModel resolvedGroup } }:
                group = resolvedGroup;
                return true;
            default:
                group = null;
                return false;
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

        await ViewModel.ApplyDashboardTreeModifierAsync(
            request.Group,
            request.DaysBack,
            request.Patterns,
            request.IsAdHoc);
    }

    private async void ClearDashboardModifierMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: LogGroupViewModel group } && ViewModel != null)
            await ViewModel.ClearDashboardTreeModifierAsync(group, isAdHoc: false);
    }

    private async void ClearAdHocModifierMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
            await ViewModel.ClearDashboardTreeModifierAsync(group: null, isAdHoc: true);
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

        await ViewModel.OpenDashboardMemberFileAsync(groupVm, fileVm);
        e.Handled = true;
    }

    private async void RemoveDashboardFile_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetDashboardFileMenuContext(sender, out var fileVm, out var groupVm) ||
            ViewModel == null)
        {
            return;
        }

        await ViewModel.RemoveDashboardMemberFileAsync(groupVm, fileVm);
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

    private void MemberFileRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DependencyObject source ||
            !TryGetDashboardFileRowContext(source, out var fileVm, out var groupVm))
        {
            return;
        }

        _memberDragStartPoint = e.GetPosition(this);
        _dragSourceMemberFile = fileVm;
        _dragSourceMemberGroup = groupVm;
    }

    private void MemberFileRow_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_memberDragStartPoint == null ||
            _dragSourceMemberFile == null ||
            _dragSourceMemberGroup == null ||
            e.LeftButton != MouseButtonState.Pressed)
        {
            ClearMemberDragState();
            return;
        }

        var position = e.GetPosition(this);
        var delta = position - _memberDragStartPoint.Value;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var request = new DashboardFileDragRequest(_dragSourceMemberGroup, _dragSourceMemberFile);
        ClearMemberDragState();

        var data = new DataObject(DashboardFileDragFormat, request);
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move);
    }

    private void MemberFileList_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not ListBox listBox ||
            ViewModel == null ||
            listBox.DataContext is not LogGroupViewModel groupVm ||
            !TryGetDashboardFileDragRequest(e, out var request))
        {
            e.Effects = DragDropEffects.None;
            HideDropAdorner();
            e.Handled = true;
            return;
        }

        var (targetFileVm, container) = HitTestDashboardFileItem(listBox, e.GetPosition(listBox));
        if (targetFileVm == null ||
            container == null ||
            string.Equals(targetFileVm.FileId, request.File.FileId, StringComparison.Ordinal))
        {
            e.Effects = DragDropEffects.None;
            HideDropAdorner();
            e.Handled = true;
            return;
        }

        var placement = GetDashboardFileDropPlacement(container, e);
        if (!ViewModel.CanDropDashboardFileOnFile(
                request.Group,
                groupVm,
                request.File.FileId,
                targetFileVm.FileId,
                placement))
        {
            e.Effects = DragDropEffects.None;
            HideDropAdorner();
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        var bounds = container.TransformToAncestor(listBox)
            .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
        EnsureDropAdorner(listBox);
        _dropAdorner!.Update(bounds, placement);
        e.Handled = true;
    }

    private void MemberFileList_DragLeave(object sender, DragEventArgs e)
    {
        HideDropAdorner();
    }

    private async void MemberFileList_Drop(object sender, DragEventArgs e)
    {
        HideDropAdorner();

        if (sender is not ListBox listBox ||
            listBox.DataContext is not LogGroupViewModel groupVm ||
            ViewModel == null ||
            !TryGetDashboardFileDragRequest(e, out var request))
        {
            return;
        }

        var (targetFileVm, container) = HitTestDashboardFileItem(listBox, e.GetPosition(listBox));
        if (targetFileVm == null ||
            container == null ||
            string.Equals(targetFileVm.FileId, request.File.FileId, StringComparison.Ordinal))
        {
            return;
        }

        var placement = GetDashboardFileDropPlacement(container, e);
        if (!ViewModel.CanDropDashboardFileOnFile(
                request.Group,
                groupVm,
                request.File.FileId,
                targetFileVm.FileId,
                placement))
            return;

        await ViewModel.ApplyDashboardFileDropAsync(
            request.Group,
            groupVm,
            request.File.FileId,
            targetFileVm.FileId,
            placement);
        e.Handled = true;
    }

    private void GroupTree_DragOver(object sender, DragEventArgs e)
    {
        if (ViewModel == null)
            return;

        if (TryGetDashboardFileDragRequest(e, out var fileRequest))
        {
            var (fileTargetGroup, fileTargetContainer) = HitTestGroupItem(GroupItemsControl, e.GetPosition(GroupItemsControl));
            if (fileTargetGroup == null ||
                fileTargetContainer == null ||
                !ViewModel.CanDropDashboardFileOnGroup(fileRequest.Group, fileTargetGroup, fileRequest.File.FileId))
            {
                e.Effects = DragDropEffects.None;
                HideDropAdorner();
                e.Handled = true;
                return;
            }

            e.Effects = DragDropEffects.Move;
            var fileBounds = fileTargetContainer.TransformToAncestor(GroupItemsControl)
                .TransformBounds(new Rect(0, 0, fileTargetContainer.ActualWidth, fileTargetContainer.ActualHeight));
            EnsureDropAdorner(GroupItemsControl);
            _dropAdorner!.Update(fileBounds, DropPlacement.Inside);
            e.Handled = true;
            return;
        }

        if (!e.Data.GetDataPresent(GroupDragFormat))
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

        if (ViewModel == null)
            return;

        if (TryGetDashboardFileDragRequest(e, out var fileRequest))
        {
            var (fileTargetGroup, _) = HitTestGroupItem(GroupItemsControl, e.GetPosition(GroupItemsControl));
            if (fileTargetGroup == null ||
                !ViewModel.CanDropDashboardFileOnGroup(fileRequest.Group, fileTargetGroup, fileRequest.File.FileId))
                return;

            await ViewModel.ApplyDashboardFileDropAsync(
                fileRequest.Group,
                fileTargetGroup,
                fileRequest.File.FileId,
                targetFileId: null,
                DropPlacement.Inside);
            e.Handled = true;
            return;
        }

        if (!e.Data.GetDataPresent(GroupDragFormat))
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

    private static DropPlacement GetDashboardFileDropPlacement(FrameworkElement container, DragEventArgs e)
    {
        var position = e.GetPosition(container);
        return position.Y < container.ActualHeight * 0.5
            ? DropPlacement.Before
            : DropPlacement.After;
    }

    private static (GroupFileMemberViewModel? fileVm, FrameworkElement? container) HitTestDashboardFileItem(
        ItemsControl itemsControl,
        Point position)
    {
        var hit = itemsControl.InputHitTest(position) as DependencyObject;
        while (hit != null)
        {
            if (TryGetDashboardFileRowContext(hit, out var fileVm, out _))
            {
                var container = hit as FrameworkElement;
                var parent = VisualTreeHelper.GetParent(hit);
                while (parent != null && parent != itemsControl)
                {
                    if (parent is FrameworkElement parentElement &&
                        parentElement.DataContext == fileVm)
                    {
                        container = parentElement;
                    }

                    parent = VisualTreeHelper.GetParent(parent);
                }

                return (fileVm, container);
            }

            hit = VisualTreeHelper.GetParent(hit);
        }

        return (null, null);
    }

    private static bool TryGetDashboardFileRowContext(
        DependencyObject source,
        [NotNullWhen(true)] out GroupFileMemberViewModel? fileVm,
        [NotNullWhen(true)] out LogGroupViewModel? groupVm)
    {
        var current = source;
        while (current != null)
        {
            if (current is FrameworkElement
                {
                    DataContext: GroupFileMemberViewModel resolvedFileVm,
                    Tag: LogGroupViewModel resolvedGroupVm
                })
            {
                fileVm = resolvedFileVm;
                groupVm = resolvedGroupVm;
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        fileVm = null;
        groupVm = null;
        return false;
    }

    private static bool TryGetDashboardFileDragRequest(
        DragEventArgs e,
        [NotNullWhen(true)] out DashboardFileDragRequest? request)
    {
        if (e.Data.GetDataPresent(DashboardFileDragFormat) &&
            e.Data.GetData(DashboardFileDragFormat) is DashboardFileDragRequest resolvedRequest)
        {
            request = resolvedRequest;
            return true;
        }

        request = null;
        return false;
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

    private void ClearMemberDragState()
    {
        _memberDragStartPoint = null;
        _dragSourceMemberFile = null;
        _dragSourceMemberGroup = null;
    }
}
