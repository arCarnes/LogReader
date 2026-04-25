namespace LogReader.App.Services;

using System.Globalization;

internal static class ColorDialogCustomColors
{
    private const int MaxCustomColors = 16;
    private const int MaxColorRef = 0xFFFFFF;

    public static int[] ToDialogCustomColors(IEnumerable<string>? colors)
        => Normalize(colors)
            .Select(ToColorRef)
            .ToArray();

    public static List<string> FromDialogCustomColors(IEnumerable<int>? customColors)
    {
        if (customColors == null)
            return new List<string>();

        return customColors
            .Where(colorRef => colorRef is > 0 and <= MaxColorRef)
            .Take(MaxCustomColors)
            .Select(FromColorRef)
            .ToList();
    }

    public static List<string> Merge(
        IEnumerable<string>? existingColors,
        IEnumerable<int>? dialogCustomColors,
        string? selectedColor)
    {
        var merged = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddColors(merged, seen, Normalize(existingColors));
        AddColors(merged, seen, FromDialogCustomColors(dialogCustomColors));
        if (selectedColor != null)
            AddColors(merged, seen, Normalize([selectedColor]));

        return merged.Take(MaxCustomColors).ToList();
    }

    public static List<string> Normalize(IEnumerable<string>? colors)
    {
        if (colors == null)
            return new List<string>();

        return colors
            .Select(NormalizeHexColor)
            .OfType<string>()
            .Take(MaxCustomColors)
            .ToList();
    }

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

    private static void AddColors(List<string> colors, HashSet<string> seen, IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (colors.Count >= MaxCustomColors)
                return;

            if (seen.Add(candidate))
                colors.Add(candidate);
        }
    }

    private static int ToColorRef(string color)
    {
        var red = int.Parse(color.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var green = int.Parse(color.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var blue = int.Parse(color.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        return red | (green << 8) | (blue << 16);
    }

    private static string FromColorRef(int colorRef)
    {
        var red = colorRef & 0xFF;
        var green = (colorRef >> 8) & 0xFF;
        var blue = (colorRef >> 16) & 0xFF;

        return $"#{red:X2}{green:X2}{blue:X2}";
    }
}
