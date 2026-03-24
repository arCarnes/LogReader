using LogReader.App.Services;

namespace LogReader.Tests;

public class DashboardPathFormatterTests
{
    [Fact]
    public void FormatToWidth_WhenSettingDisabled_ReturnsFileName()
    {
        var result = DashboardPathFormatter.FormatToWidth(
            @"\\host1\someotherfolder\anothersubdir\programs\services\inhouse\app1\app1_instance1.log",
            "app1_instance1.log",
            showFullPath: false,
            availableWidth: 10,
            measureWidth: static text => text.Length);

        Assert.Equal("app1_instance1.log", result);
    }

    [Fact]
    public void FormatToWidth_WhenFullPathFits_ReturnsFullPath()
    {
        var path = @"\\host1\someotherfolder\anothersubdir\programs\services\inhouse\app1\app1_instance1.log";

        var result = DashboardPathFormatter.FormatToWidth(
            path,
            "app1_instance1.log",
            showFullPath: true,
            availableWidth: path.Length,
            measureWidth: static text => text.Length);

        Assert.Equal(path, result);
    }

    [Fact]
    public void FormatToWidth_WhenSpaceIsModerate_PrefersBothSidesOfPath()
    {
        var path = @"\\host1\someotherfolder\anothersubdir\programs\services\inhouse\app1\app1_instance1.log";
        var expected = @"\\host1\someotherfolder\[...]\inhouse\app1\app1_instance1.log";

        var result = DashboardPathFormatter.FormatToWidth(
            path,
            "app1_instance1.log",
            showFullPath: true,
            availableWidth: expected.Length,
            measureWidth: static text => text.Length);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void FormatToWidth_WhenSpaceIsTight_FallsBackToRootEllipsisAndFileName()
    {
        var path = @"\\host1\someotherfolder\anothersubdir\programs\services\inhouse\app1\app1_instance1.log";
        var expected = @"\\host1\[...]\app1_instance1.log";

        var result = DashboardPathFormatter.FormatToWidth(
            path,
            "app1_instance1.log",
            showFullPath: true,
            availableWidth: expected.Length,
            measureWidth: static text => text.Length);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void FormatToWidth_WhenNothingButFileNameFits_ReturnsFileName()
    {
        var result = DashboardPathFormatter.FormatToWidth(
            @"C:\someotherfolder\anothersubdir\programs\services\inhouse\app1\app1_instance1.log",
            "app1_instance1.log",
            showFullPath: true,
            availableWidth: "app1_instance1.log".Length,
            measureWidth: static text => text.Length);

        Assert.Equal("app1_instance1.log", result);
    }
}
