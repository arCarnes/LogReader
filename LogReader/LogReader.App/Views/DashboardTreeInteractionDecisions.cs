namespace LogReader.App.Views;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using LogReader.App.Models;
using LogReader.App.ViewModels;
using LogReader.Core.Models;

internal static class DashboardTreeInteractionDecisions
{
    public static bool ShouldIgnoreGroupRowMouseDown(DependencyObject? originalSource, DependencyObject sender)
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

    public static bool HasExceededGroupDragThreshold(Point start, Point position)
    {
        var delta = position - start;
        return Math.Abs(delta.X) >= SystemParameters.MinimumHorizontalDragDistance ||
               Math.Abs(delta.Y) >= SystemParameters.MinimumVerticalDragDistance;
    }

    public static bool ShouldCommitEditingGroupOnMouseDown(DependencyObject? originalSource, LogGroupViewModel? editingGroup)
    {
        return editingGroup != null &&
               editingGroup.IsEditing &&
               !IsWithinEditingTextBox(originalSource, editingGroup);
    }

    public static bool IsWithinEditingTextBox(DependencyObject? source, LogGroupViewModel editingGroup)
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

    public static DropPlacement GetGroupDropPlacement(LogGroupViewModel target, double containerHeight, double positionY)
    {
        if (target.Kind == LogGroupKind.Branch)
        {
            if (positionY < containerHeight * 0.25)
                return DropPlacement.Before;

            if (positionY > containerHeight * 0.75)
                return DropPlacement.After;

            return DropPlacement.Inside;
        }

        return positionY < containerHeight * 0.5 ? DropPlacement.Before : DropPlacement.After;
    }

    public static DropPlacement GetDashboardFileDropPlacement(double containerHeight, double positionY)
        => positionY < containerHeight * 0.5
            ? DropPlacement.Before
            : DropPlacement.After;

    public static string? ResolveFileContextPath(object? dataContext)
        => dataContext switch
        {
            GroupFileMemberViewModel fileVm => fileVm.FilePath,
            LogTabViewModel tabVm => tabVm.FilePath,
            _ => null
        };

    private static DependencyObject? GetDependencyParent(DependencyObject current)
    {
        return current switch
        {
            Visual or Visual3D => VisualTreeHelper.GetParent(current),
            FrameworkContentElement contentElement => contentElement.Parent,
            _ => null
        };
    }
}
