namespace LogReader.Core;

using System.IO;
using LogReader.Core.Models;

public static class LogFileDisplayName
{
    public static string? Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        if (name.Any(character => char.IsControl(character) || character is '\u2028' or '\u2029'))
            throw new ArgumentException("Display names must be single-line text without control characters.", nameof(name));

        var normalizedName = name.Trim();
        if (normalizedName.Length > ConfiguredLogLimits.DefaultMaxNameCharacters)
            throw new ArgumentException($"Display names must be at most {ConfiguredLogLimits.DefaultMaxNameCharacters:N0} characters.", nameof(name));

        return normalizedName;
    }

    public static string Resolve(string? customName, string filePath, string fileId)
    {
        if (!string.IsNullOrWhiteSpace(customName))
            return customName;

        var fileName = Path.GetFileName(filePath);
        return string.IsNullOrWhiteSpace(fileName) ? fileId : fileName;
    }
}
