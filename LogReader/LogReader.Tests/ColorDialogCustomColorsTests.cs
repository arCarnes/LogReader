namespace LogReader.Tests;

using LogReader.App.Services;

public class ColorDialogCustomColorsTests
{
    [Fact]
    public void Normalize_IgnoresInvalidAndBlankColors()
    {
        var colors = ColorDialogCustomColors.Normalize(["", "not-a-color", "#12345G", "#abcdef"]);

        Assert.Equal(["#ABCDEF"], colors);
    }

    [Fact]
    public void Normalize_DeduplicatesColors()
    {
        var colors = ColorDialogCustomColors.Normalize(["#ff4d4d", "#FF4D4D", "#00AA66"]);

        Assert.Equal(["#FF4D4D", "#00AA66"], colors);
    }

    [Fact]
    public void Normalize_CapsRecentColorsAtEight()
    {
        var colors = Enumerable.Range(0, 20).Select(i => $"#{i:X6}");

        var normalized = ColorDialogCustomColors.Normalize(colors);

        Assert.Equal(8, normalized.Count);
        Assert.Equal("#000000", normalized[0]);
        Assert.Equal("#000007", normalized[7]);
    }

    [Fact]
    public void AddRecentColor_AppendsNewColor()
    {
        var colors = ColorDialogCustomColors.AddRecentColor(["#FF4D4D"], "#00AA66");

        Assert.Equal(["#FF4D4D", "#00AA66"], colors);
    }

    [Fact]
    public void AddRecentColor_NormalizesSelectedColor()
    {
        var colors = ColorDialogCustomColors.AddRecentColor([], "#abcdef");

        Assert.Equal(["#ABCDEF"], colors);
    }

    [Fact]
    public void AddRecentColor_IgnoresInvalidSelectedColor()
    {
        var colors = ColorDialogCustomColors.AddRecentColor(["#FF4D4D"], "not-a-color");

        Assert.Equal(["#FF4D4D"], colors);
    }

    [Fact]
    public void AddRecentColor_MovesExistingColorToNewest()
    {
        var colors = ColorDialogCustomColors.AddRecentColor(["#FF4D4D", "#00AA66", "#112233"], "#ff4d4d");

        Assert.Equal(["#00AA66", "#112233", "#FF4D4D"], colors);
    }

    [Fact]
    public void AddRecentColor_RemovesOldestWhenFull()
    {
        var existingColors = Enumerable.Range(0, 8).Select(i => $"#{i:X6}");

        var colors = ColorDialogCustomColors.AddRecentColor(existingColors, "#000008");

        Assert.Equal(8, colors.Count);
        Assert.Equal("#000001", colors[0]);
        Assert.Equal("#000007", colors[6]);
        Assert.Equal("#000008", colors[7]);
    }

    [Fact]
    public void ToNewestFirst_ReversesNormalizedColors()
    {
        var colors = ColorDialogCustomColors.ToNewestFirst(["#ff4d4d", "#00AA66", "#112233"]);

        Assert.Equal(["#112233", "#00AA66", "#FF4D4D"], colors);
    }
}
