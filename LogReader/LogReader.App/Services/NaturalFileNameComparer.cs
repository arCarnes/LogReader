namespace LogReader.App.Services;

using System.Runtime.InteropServices;

internal sealed class NaturalFileNameComparer : IComparer<string?>
{
    public static readonly NaturalFileNameComparer Instance = new();

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string x, string y);

    public int Compare(string? x, string? y) =>
        StrCmpLogicalW(x ?? string.Empty, y ?? string.Empty);
}
