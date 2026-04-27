namespace LogReader.App.Services;

using System.Globalization;

internal static class ColorDialogCustomColors
{
    private const int MaxCustomColors = 8;

    public static List<string> Normalize(IEnumerable<string>? colors)
    {
        if (colors == null)
            return new List<string>();

        return colors
            .Select(NormalizeHexColor)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxCustomColors)
            .ToList();
    }

    public static List<string> AddRecentColor(IEnumerable<string>? colors, string? selectedColor)
    {
        var normalizedColors = Normalize(colors);
        var normalizedSelectedColor = NormalizeHexColor(selectedColor);
        if (normalizedSelectedColor == null)
            return normalizedColors;

        normalizedColors.RemoveAll(color => string.Equals(color, normalizedSelectedColor, StringComparison.OrdinalIgnoreCase));
        normalizedColors.Add(normalizedSelectedColor);

        while (normalizedColors.Count > MaxCustomColors)
            normalizedColors.RemoveAt(0);

        return normalizedColors;
    }

    public static List<string> ToNewestFirst(IEnumerable<string>? colors)
        => Normalize(colors)
            .AsEnumerable()
            .Reverse()
            .ToList();

    private static string? NormalizeHexColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
            return null;

        var hex = color.Trim();
        if (hex.Length != 7 || hex[0] != '#')
            return null;

        return int.TryParse(hex[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)
            ? hex.ToUpperInvariant()
            : null;
    }
}
