namespace LogReader.Tests;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using LogReader.App.Controls;

public class DashboardMemberRowPanelTests
{
    [Fact]
    public void Arrange_UsesSizeOnlyMetadataWhenFullMetadataWouldCrowdPrimary()
    {
        WpfTestHost.Run(() =>
        {
            var primary = CreateElement(300);
            var host = CreateElement(70);
            var separator = CreateElement(1);
            var size = CreateElement(40);
            var panel = CreatePanel(primary, host, separator, size);

            Arrange(panel, 220);

            Assert.Equal(180, GetLayoutWidth(primary));
            Assert.Equal(0, GetLayoutWidth(host));
            Assert.Equal(0, GetLayoutWidth(separator));
            Assert.Equal(40, GetLayoutWidth(size));
        });
    }

    [Fact]
    public void Arrange_ShowsFullMetadataWhenPrimaryLabelIsShort()
    {
        WpfTestHost.Run(() =>
        {
            var primary = CreateElement(90);
            var host = CreateElement(40);
            var separator = CreateElement(1);
            var size = CreateElement(35);
            var panel = CreatePanel(primary, host, separator, size);

            Arrange(panel, 170);

            Assert.Equal(94, GetLayoutWidth(primary));
            Assert.Equal(40, GetLayoutWidth(host));
            Assert.Equal(1, GetLayoutWidth(separator));
            Assert.Equal(35, GetLayoutWidth(size));
        });
    }

    private static DashboardMemberRowPanel CreatePanel(
        FrameworkElement primary,
        FrameworkElement host,
        FrameworkElement separator,
        FrameworkElement size)
    {
        DashboardMemberRowPanel.SetElementRole(primary, DashboardMemberRowElementRole.Primary);
        DashboardMemberRowPanel.SetElementRole(host, DashboardMemberRowElementRole.Host);
        DashboardMemberRowPanel.SetElementRole(separator, DashboardMemberRowElementRole.Separator);
        DashboardMemberRowPanel.SetElementRole(size, DashboardMemberRowElementRole.Size);

        var panel = new DashboardMemberRowPanel();
        panel.Children.Add(primary);
        panel.Children.Add(host);
        panel.Children.Add(separator);
        panel.Children.Add(size);
        return panel;
    }

    private static Border CreateElement(double width)
        => new()
        {
            Width = width,
            Height = 10
        };

    private static void Arrange(DashboardMemberRowPanel panel, double width)
    {
        var size = new Size(width, 20);
        panel.Measure(size);
        panel.Arrange(new Rect(size));
    }

    private static double GetLayoutWidth(FrameworkElement element)
        => LayoutInformation.GetLayoutSlot(element).Width;
}
