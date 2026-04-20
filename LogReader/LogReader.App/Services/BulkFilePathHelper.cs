namespace LogReader.App.Services;

using System.IO;

internal static class BulkFilePathHelper
{
    public static IReadOnlyList<string> Parse(string? rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
            return Array.Empty<string>();

        var parsedPaths = new List<string>();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        using var reader = new StringReader(rawInput);
        while (reader.ReadLine() is { } line)
        {
            var parsedPath = ParseLine(line);
            if (parsedPath == null)
                continue;

            foreach (var resolvedPath in ExpandPattern(parsedPath))
            {
                if (!seenPaths.Add(resolvedPath))
                    continue;

                parsedPaths.Add(resolvedPath);
            }
        }

        return parsedPaths;
    }

    public static BulkFilePreview BuildPreview(
        string? rawInput,
        Func<string, bool>? fileExists = null)
    {
        var fileExistsEvaluator = fileExists ?? File.Exists;
        var parsedPaths = new List<string>();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<BulkFilePreviewItem>();
        var seenUnmatchedPatterns = new HashSet<string>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(rawInput))
        {
            using var reader = new StringReader(rawInput);
            while (reader.ReadLine() is { } line)
            {
                var parsedPath = ParseLine(line);
                if (parsedPath == null)
                    continue;

                if (ContainsWildcard(parsedPath))
                {
                    var resolvedPaths = ExpandWildcardFilePaths(parsedPath);
                    if (resolvedPaths.Count == 0)
                    {
                        if (seenUnmatchedPatterns.Add(parsedPath))
                            items.Add(new BulkFilePreviewItem(parsedPath, BulkFilePreviewItemStatus.NoMatches));

                        continue;
                    }

                    foreach (var resolvedPath in resolvedPaths)
                    {
                        if (!seenPaths.Add(resolvedPath))
                            continue;

                        parsedPaths.Add(resolvedPath);
                        items.Add(new BulkFilePreviewItem(
                            resolvedPath,
                            fileExistsEvaluator(resolvedPath) ? BulkFilePreviewItemStatus.Found : BulkFilePreviewItemStatus.Missing));
                    }

                    continue;
                }

                if (!seenPaths.Add(parsedPath))
                    continue;

                parsedPaths.Add(parsedPath);
                items.Add(new BulkFilePreviewItem(
                    parsedPath,
                    fileExistsEvaluator(parsedPath) ? BulkFilePreviewItemStatus.Found : BulkFilePreviewItemStatus.Missing));
            }
        }

        return new BulkFilePreview(parsedPaths, items);
    }

    private static string? ParseLine(string line)
    {
        var trimmedLine = line.Trim();
        if (trimmedLine.Length == 0)
            return null;

        if (trimmedLine.Length >= 2 &&
            (trimmedLine[0] == '"' || trimmedLine[0] == '\'') &&
            trimmedLine[0] == trimmedLine[^1])
        {
            trimmedLine = trimmedLine[1..^1];
        }

        return string.IsNullOrWhiteSpace(trimmedLine) ? null : trimmedLine;
    }

    private static IReadOnlyList<string> ExpandPattern(string pathOrPattern)
    {
        if (!ContainsWildcard(pathOrPattern))
            return new[] { pathOrPattern };

        return ExpandWildcardFilePaths(pathOrPattern);
    }

    private static List<string> ExpandWildcardFilePaths(string pathPattern)
    {
        try
        {
            var directory = Path.GetDirectoryName(pathPattern);
            var fileSegment = Path.GetFileName(pathPattern);
            if (string.IsNullOrWhiteSpace(fileSegment))
                return new List<string>();

            var resolvedDirectory = string.IsNullOrWhiteSpace(directory)
                ? Environment.CurrentDirectory
                : directory;
            if (ContainsWildcard(resolvedDirectory) || !Directory.Exists(resolvedDirectory))
                return new List<string>();

            if (!ContainsWildcard(fileSegment))
            {
                var candidatePath = Path.Combine(resolvedDirectory, fileSegment);
                return File.Exists(candidatePath)
                    ? new List<string> { candidatePath }
                    : new List<string>();
            }

            return Directory
                .EnumerateFiles(resolvedDirectory, fileSegment, SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName, NaturalFileNameComparer.Instance)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (ArgumentException)
        {
            return new List<string>();
        }
        catch (DirectoryNotFoundException)
        {
            return new List<string>();
        }
        catch (IOException)
        {
            return new List<string>();
        }
        catch (NotSupportedException)
        {
            return new List<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }

    private static bool ContainsWildcard(string path)
        => path.IndexOfAny(['*', '?']) >= 0;
}

internal enum BulkFilePreviewItemStatus
{
    Found,
    Missing,
    NoMatches
}

internal sealed record BulkFilePreviewItem(string FilePath, BulkFilePreviewItemStatus Status)
{
    public bool IsFound => Status == BulkFilePreviewItemStatus.Found;
}

internal sealed record BulkFilePreview(
    IReadOnlyList<string> ParsedPaths,
    IReadOnlyList<BulkFilePreviewItem> Items)
{
    public int FoundCount => Items.Count(item => item.IsFound);

    public int MissingCount => Items.Count - FoundCount;
}
