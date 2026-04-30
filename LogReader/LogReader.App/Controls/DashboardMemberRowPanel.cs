namespace LogReader.App.Controls;

using System.Windows;
using System.Windows.Controls;

public enum DashboardMemberRowElementRole
{
    Primary,
    Host,
    Separator,
    Size
}

public class DashboardMemberRowPanel : Panel
{
    public static readonly DependencyProperty ElementRoleProperty = DependencyProperty.RegisterAttached(
        "ElementRole",
        typeof(DashboardMemberRowElementRole),
        typeof(DashboardMemberRowPanel),
        new FrameworkPropertyMetadata(DashboardMemberRowElementRole.Primary, FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static readonly DependencyProperty PrimaryMinimumWidthProperty = DependencyProperty.Register(
        nameof(PrimaryMinimumWidth),
        typeof(double),
        typeof(DashboardMemberRowPanel),
        new FrameworkPropertyMetadata(140d, FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty PrimaryWidthRatioProperty = DependencyProperty.Register(
        nameof(PrimaryWidthRatio),
        typeof(double),
        typeof(DashboardMemberRowPanel),
        new FrameworkPropertyMetadata(0.65d, FrameworkPropertyMetadataOptions.AffectsArrange));

    public double PrimaryMinimumWidth
    {
        get => (double)GetValue(PrimaryMinimumWidthProperty);
        set => SetValue(PrimaryMinimumWidthProperty, value);
    }

    public double PrimaryWidthRatio
    {
        get => (double)GetValue(PrimaryWidthRatioProperty);
        set => SetValue(PrimaryWidthRatioProperty, value);
    }

    public DashboardMemberRowPanel()
    {
        ClipToBounds = true;
    }

    public static DashboardMemberRowElementRole GetElementRole(UIElement element)
        => (DashboardMemberRowElementRole)element.GetValue(ElementRoleProperty);

    public static void SetElementRole(UIElement element, DashboardMemberRowElementRole value)
        => element.SetValue(ElementRoleProperty, value);

    protected override Size MeasureOverride(Size availableSize)
    {
        var childConstraint = new Size(
            double.PositiveInfinity,
            double.IsInfinity(availableSize.Height) ? double.PositiveInfinity : availableSize.Height);

        double desiredWidth = 0;
        double desiredHeight = 0;

        foreach (UIElement child in InternalChildren)
        {
            child.Measure(childConstraint);

            if (child.Visibility == Visibility.Collapsed)
                continue;

            desiredWidth += child.DesiredSize.Width;
            desiredHeight = Math.Max(desiredHeight, child.DesiredSize.Height);
        }

        return new Size(
            double.IsInfinity(availableSize.Width) ? desiredWidth : availableSize.Width,
            desiredHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var primary = FindVisibleChild(DashboardMemberRowElementRole.Primary);
        var host = FindVisibleChild(DashboardMemberRowElementRole.Host);
        var separator = FindVisibleChild(DashboardMemberRowElementRole.Separator);
        var size = FindVisibleChild(DashboardMemberRowElementRole.Size);

        var metadata = SelectMetadata(finalSize.Width, primary, host, separator, size);
        var metadataWidth = metadata.Sum(static child => child.DesiredSize.Width);
        var primaryWidth = Math.Max(0, finalSize.Width - metadataWidth);

        if (primary != null)
            primary.Arrange(new Rect(0, 0, primaryWidth, finalSize.Height));

        var metadataX = primaryWidth;
        foreach (var child in metadata)
        {
            var childWidth = child.DesiredSize.Width;
            child.Arrange(new Rect(metadataX, 0, childWidth, finalSize.Height));
            metadataX += childWidth;
        }

        foreach (UIElement child in InternalChildren)
        {
            if (child != primary && !metadata.Contains(child))
                child.Arrange(new Rect(finalSize.Width, 0, 0, finalSize.Height));
        }

        return finalSize;
    }

    private UIElement? FindVisibleChild(DashboardMemberRowElementRole role)
    {
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility != Visibility.Collapsed && GetElementRole(child) == role)
                return child;
        }

        return null;
    }

    private IReadOnlyList<UIElement> SelectMetadata(
        double availableWidth,
        UIElement? primary,
        UIElement? host,
        UIElement? separator,
        UIElement? size)
    {
        var primaryProtectedWidth = GetPrimaryProtectedWidth(availableWidth, primary);
        var metadataBudget = Math.Max(0, availableWidth - primaryProtectedWidth);

        if (host != null && separator != null && size != null)
        {
            var fullMetadata = new[] { host, separator, size };
            if (Fits(fullMetadata, metadataBudget))
                return fullMetadata;
        }

        if (size != null && size.DesiredSize.Width <= metadataBudget)
            return new[] { size };

        return Array.Empty<UIElement>();
    }

    private double GetPrimaryProtectedWidth(double availableWidth, UIElement? primary)
    {
        if (primary == null || availableWidth <= 0)
            return availableWidth;

        var ratioWidth = availableWidth * Math.Clamp(PrimaryWidthRatio, 0, 1);
        var protectedWidth = Math.Max(PrimaryMinimumWidth, ratioWidth);
        return Math.Min(primary.DesiredSize.Width, protectedWidth);
    }

    private static bool Fits(IReadOnlyCollection<UIElement> children, double availableWidth)
        => children.Sum(static child => child.DesiredSize.Width) <= availableWidth;
}
