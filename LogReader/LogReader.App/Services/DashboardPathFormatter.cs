namespace LogReader.App.Services;

using System.IO;

internal static class DashboardPathFormatter
{
    private const string EllipsisSegment = "[...]";

    public static string FormatToWidth(
        string? filePath,
        string? fileName,
        bool showFullPath,
        double availableWidth,
        Func<string, double> measureWidth)
    {
        var normalizedFileName = string.IsNullOrWhiteSpace(fileName)
            ? Path.GetFileName(filePath ?? string.Empty)
            : fileName;

        if (string.IsNullOrWhiteSpace(normalizedFileName))
            normalizedFileName = filePath ?? string.Empty;

        if (!showFullPath || string.IsNullOrWhiteSpace(filePath) || availableWidth <= 0)
            return normalizedFileName;

        if (measureWidth(filePath) <= availableWidth)
            return filePath;

        var segments = PathSegments.Parse(filePath, normalizedFileName);
        if (segments.Directories.Count == 0)
            return normalizedFileName;

        foreach (var candidate in BuildCandidates(segments))
        {
            if (measureWidth(candidate) <= availableWidth)
                return candidate;
        }

        return normalizedFileName;
    }

    internal static IReadOnlyList<string> BuildCandidates(string filePath, string fileName)
        => BuildCandidates(PathSegments.Parse(filePath, fileName));

    private static IReadOnlyList<string> BuildCandidates(PathSegments segments)
    {
        var candidates = new List<string>();
        candidates.Add(segments.FullPath);

        for (var visibleDirectoryCount = segments.Directories.Count - 1; visibleDirectoryCount >= 0; visibleDirectoryCount--)
            candidates.Add(BuildCandidate(segments, visibleDirectoryCount));

        return candidates;
    }

    private static string BuildCandidate(PathSegments segments, int visibleDirectoryCount)
    {
        var leftVisible = new List<string>();
        var rightVisible = new List<string>();
        var leftIndex = 0;
        var rightIndex = segments.Directories.Count - 1;
        var takeFromRight = true;

        while (visibleDirectoryCount > 0 && leftIndex <= rightIndex)
        {
            if (takeFromRight)
                rightVisible.Add(segments.Directories[rightIndex--]);
            else
                leftVisible.Add(segments.Directories[leftIndex++]);

            visibleDirectoryCount--;
            takeFromRight = !takeFromRight;
        }

        rightVisible.Reverse();

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(segments.Root))
            parts.Add(segments.Root);

        parts.AddRange(leftVisible);

        if (leftIndex <= rightIndex)
            parts.Add(EllipsisSegment);

        parts.AddRange(rightVisible);
        parts.Add(segments.FileName);

        return JoinParts(parts, segments.Separator);
    }

    private static string JoinParts(IReadOnlyList<string> parts, char separator)
    {
        if (parts.Count == 0)
            return string.Empty;

        var joined = string.Join(separator, parts.Where(static part => !string.IsNullOrEmpty(part)));
        if (parts[0] == separator.ToString())
            return separator + joined.TrimStart(separator);

        return joined;
    }

    private sealed record PathSegments(string FullPath, string Root, char Separator, IReadOnlyList<string> Directories, string FileName)
    {
        public static PathSegments Parse(string filePath, string fileName)
        {
            var separator = filePath.Contains('\\') ? '\\' : '/';
            var root = string.Empty;
            var remaining = filePath;

            if (remaining.StartsWith(@"\\", StringComparison.Ordinal) || remaining.StartsWith("//", StringComparison.Ordinal))
            {
                var trimmed = remaining.TrimStart('\\', '/');
                var parts = Split(trimmed);
                if (parts.Count > 0)
                {
                    root = $"{new string(separator, 2)}{parts[0]}";
                    remaining = string.Join(separator, parts.Skip(1));
                }
                else
                {
                    remaining = string.Empty;
                }
            }
            else if (remaining.Length >= 2 && char.IsLetter(remaining[0]) && remaining[1] == ':')
            {
                root = remaining[..2];
                remaining = remaining.Length > 2
                    ? remaining[2..].TrimStart('\\', '/')
                    : string.Empty;
            }
            else if (remaining.Length > 0 && (remaining[0] == '\\' || remaining[0] == '/'))
            {
                root = separator.ToString();
                remaining = remaining[1..];
            }

            var pathParts = Split(remaining);
            if (pathParts.Count > 0 && string.Equals(pathParts[^1], fileName, StringComparison.Ordinal))
                pathParts.RemoveAt(pathParts.Count - 1);

            return new PathSegments(filePath, root, separator, pathParts, fileName);
        }

        private static List<string> Split(string path)
            => path
                .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)
                .ToList();
    }
}
