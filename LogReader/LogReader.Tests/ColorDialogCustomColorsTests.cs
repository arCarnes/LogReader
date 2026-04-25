namespace LogReader.Tests;

using LogReader.App.Services;

public class ColorDialogCustomColorsTests
{
    [Fact]
    public void ToDialogCustomColors_ConvertsHexColorsToColorRefs()
    {
        var colorRefs = ColorDialogCustomColors.ToDialogCustomColors(["#FF4D4D", "#00AA66"]);

        Assert.Equal([0x4D4DFF, 0x66AA00], colorRefs);
    }

    [Fact]
    public void FromDialogCustomColors_ConvertsColorRefsToHexColors()
    {
        var colors = ColorDialogCustomColors.FromDialogCustomColors([0x4D4DFF, 0x66AA00]);

        Assert.Equal(["#FF4D4D", "#00AA66"], colors);
    }

    [Fact]
    public void FromDialogCustomColors_IgnoresBlankAndInvalidColorRefs()
    {
        var colors = ColorDialogCustomColors.FromDialogCustomColors([0, 0x1000000, -1, 0x332211]);

        Assert.Equal(["#112233"], colors);
    }

    [Fact]
    public void Normalize_IgnoresInvalidAndBlankColors()
    {
        var colors = ColorDialogCustomColors.Normalize(["", "not-a-color", "#12345G", "#abcdef"]);

        Assert.Equal(["#ABCDEF"], colors);
    }

    [Fact]
    public void Normalize_CapsCustomColorsAtSixteen()
    {
        var colors = Enumerable.Range(0, 20).Select(i => $"#{i:X6}");

        var normalized = ColorDialogCustomColors.Normalize(colors);

        Assert.Equal(16, normalized.Count);
        Assert.Equal("#000000", normalized[0]);
        Assert.Equal("#00000F", normalized[15]);
    }

    [Fact]
    public void FromDialogCustomColors_CapsCustomColorsAtSixteen()
    {
        var colorRefs = Enumerable.Range(1, 20);

        var colors = ColorDialogCustomColors.FromDialogCustomColors(colorRefs);

        Assert.Equal(16, colors.Count);
        Assert.Equal("#010000", colors[0]);
        Assert.Equal("#100000", colors[15]);
    }

    [Fact]
    public void Merge_CombinesExistingDialogAndSelectedColors()
    {
        var colors = ColorDialogCustomColors.Merge(
            ["#FF4D4D"],
            [0x66AA00],
            "#112233");

        Assert.Equal(["#FF4D4D", "#00AA66", "#112233"], colors);
    }

    [Fact]
    public void Merge_AddsSelectedColorWhenMissingFromDialogCustomColors()
    {
        var colors = ColorDialogCustomColors.Merge(
            ["#FF4D4D"],
            [],
            "#00AA66");

        Assert.Equal(["#FF4D4D", "#00AA66"], colors);
    }

    [Fact]
    public void Merge_DeduplicatesColorsWhilePreservingFirstSeenOrder()
    {
        var colors = ColorDialogCustomColors.Merge(
            ["#ff4d4d", "#00AA66"],
            [0x4D4DFF, 0x332211],
            "#00aa66");

        Assert.Equal(["#FF4D4D", "#00AA66", "#112233"], colors);
    }

    [Fact]
    public void Merge_IgnoresInvalidAndBlankEntries()
    {
        var colors = ColorDialogCustomColors.Merge(
            ["", "not-a-color", "#ABCDEF"],
            [0, -1, 0x1000000, 0x332211],
            "#12345G");

        Assert.Equal(["#ABCDEF", "#112233"], colors);
    }

    [Fact]
    public void Merge_CapsMergedPaletteAtSixteenWithEarlierColorsWinning()
    {
        var existingColors = Enumerable.Range(0, 15).Select(i => $"#{i:X6}");

        var colors = ColorDialogCustomColors.Merge(
            existingColors,
            [0x100000, 0x110000],
            "#000012");

        Assert.Equal(16, colors.Count);
        Assert.Equal("#000000", colors[0]);
        Assert.Equal("#00000E", colors[14]);
        Assert.Equal("#000010", colors[15]);
        Assert.DoesNotContain("#000011", colors);
        Assert.DoesNotContain("#000012", colors);
    }
}
