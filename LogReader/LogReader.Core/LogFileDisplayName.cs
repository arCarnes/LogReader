namespace LogReader.Core;

using System.IO;

public static class LogFileDisplayName
{
    public static string? Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        if (name.Any(character => char.IsControl(character) || character is '\u2028' or '\u2029'))
            throw new ArgumentException("Display names must be single-line text without control characters.", nameof(name));

        return name.Trim();
    }

    public static string Resolve(string? customName, string filePath, string fileId)
    {
        if (!string.IsNullOrWhiteSpace(customName))
            return customName;

        var fileName = Path.GetFileName(filePath);
        return string.IsNullOrWhiteSpace(fileName) ? fileId : fileName;
    }
}
