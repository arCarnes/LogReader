namespace LogReader.App.Services;

using System.IO;

internal static class BulkFilePathHelper
{
    public static IReadOnlyList<string> Parse(string? rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
            return Array.Empty<string>();

        var parsedPaths = new List<string>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
        Func<string, DashboardFileProbeResult>? fileProbe = null)
    {
        var fileProbeEvaluator = fileProbe ?? DashboardFileProbe.Probe;
        var parsedPaths = new List<string>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<BulkFilePreviewItem>();
        var seenUnmatchedPatterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                    var expansion = ExpandWildcardFilePaths(parsedPath);
                    if (expansion.Paths.Count == 0)
                    {
                        if (seenUnmatchedPatterns.Add(parsedPath))
                        {
                            items.Add(new BulkFilePreviewItem(
                                parsedPath,
                                expansion.FailureStatus ?? BulkFilePreviewItemStatus.NoMatches));
                        }

                        continue;
                    }

                    foreach (var resolvedPath in expansion.Paths)
                    {
                        if (!seenPaths.Add(resolvedPath))
                            continue;

                        parsedPaths.Add(resolvedPath);
                        items.Add(new BulkFilePreviewItem(
                            resolvedPath,
                            ToPreviewStatus(fileProbeEvaluator(resolvedPath))));
                    }

                    continue;
                }

                if (!seenPaths.Add(parsedPath))
                    continue;

                parsedPaths.Add(parsedPath);
                items.Add(new BulkFilePreviewItem(
                    parsedPath,
                    ToPreviewStatus(fileProbeEvaluator(parsedPath))));
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

        return ExpandWildcardFilePaths(pathOrPattern).Paths;
    }

    private static WildcardExpansion ExpandWildcardFilePaths(string pathPattern)
    {
        try
        {
            var directory = Path.GetDirectoryName(pathPattern);
            var fileSegment = Path.GetFileName(pathPattern);
            if (string.IsNullOrWhiteSpace(fileSegment))
                return WildcardExpansion.NoMatches;

            var resolvedDirectory = string.IsNullOrWhiteSpace(directory)
                ? Environment.CurrentDirectory
                : directory;
            if (ContainsWildcard(resolvedDirectory))
                return WildcardExpansion.NoMatches;

            var directoryFailureStatus = GetDirectoryExpansionFailureStatus(resolvedDirectory);
            if (directoryFailureStatus != null)
                return new WildcardExpansion(Array.Empty<string>(), directoryFailureStatus);

            if (!ContainsWildcard(fileSegment))
            {
                var candidatePath = Path.Combine(resolvedDirectory, fileSegment);
                return File.Exists(candidatePath)
                    ? new WildcardExpansion(new List<string> { candidatePath }, null)
                    : WildcardExpansion.NoMatches;
            }

            return new WildcardExpansion(
                Directory
                    .EnumerateFiles(resolvedDirectory, fileSegment, SearchOption.TopDirectoryOnly)
                    .OrderBy(Path.GetFileName, NaturalFileNameComparer.Instance)
                    .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                null);
        }
        catch (ArgumentException)
        {
            return WildcardExpansion.InvalidPath;
        }
        catch (DirectoryNotFoundException)
        {
            return WildcardExpansion.NoMatches;
        }
        catch (IOException)
        {
            return WildcardExpansion.Unavailable;
        }
        catch (NotSupportedException)
        {
            return WildcardExpansion.InvalidPath;
        }
        catch (UnauthorizedAccessException)
        {
            return WildcardExpansion.AccessDenied;
        }
    }

    private static BulkFilePreviewItemStatus? GetDirectoryExpansionFailureStatus(string directory)
    {
        try
        {
            var attributes = File.GetAttributes(directory);
            return attributes.HasFlag(FileAttributes.Directory)
                ? null
                : BulkFilePreviewItemStatus.NoMatches;
        }
        catch (DirectoryNotFoundException)
        {
            return BulkFilePreviewItemStatus.NoMatches;
        }
        catch (FileNotFoundException)
        {
            return BulkFilePreviewItemStatus.NoMatches;
        }
        catch (UnauthorizedAccessException)
        {
            return BulkFilePreviewItemStatus.AccessDenied;
        }
        catch (ArgumentException)
        {
            return BulkFilePreviewItemStatus.InvalidPath;
        }
        catch (NotSupportedException)
        {
            return BulkFilePreviewItemStatus.InvalidPath;
        }
        catch (PathTooLongException)
        {
            return BulkFilePreviewItemStatus.InvalidPath;
        }
        catch (IOException)
        {
            return BulkFilePreviewItemStatus.Unavailable;
        }
    }

    private static BulkFilePreviewItemStatus ToPreviewStatus(DashboardFileProbeResult probeResult)
    {
        return probeResult.Status switch
        {
            DashboardFileProbeStatus.Found => BulkFilePreviewItemStatus.Found,
            DashboardFileProbeStatus.Missing => BulkFilePreviewItemStatus.Missing,
            DashboardFileProbeStatus.AccessDenied => BulkFilePreviewItemStatus.AccessDenied,
            DashboardFileProbeStatus.InvalidPath => BulkFilePreviewItemStatus.InvalidPath,
            DashboardFileProbeStatus.Unavailable => BulkFilePreviewItemStatus.Unavailable,
            _ => BulkFilePreviewItemStatus.Unavailable
        };
    }

    private static bool ContainsWildcard(string path)
        => path.IndexOfAny(['*', '?'], GetWildcardScanStart(path)) >= 0;

    private static int GetWildcardScanStart(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\?\UNC\".Length;

        if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            return @"\\?\".Length;

        return 0;
    }
}

internal enum BulkFilePreviewItemStatus
{
    Found,
    Missing,
    NoMatches,
    AccessDenied,
    InvalidPath,
    Unavailable
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

    public int MissingCount => Items.Count(item => item.Status == BulkFilePreviewItemStatus.Missing);

    public int UnavailableCount => Items.Count(item =>
        item.Status is BulkFilePreviewItemStatus.AccessDenied
            or BulkFilePreviewItemStatus.InvalidPath
            or BulkFilePreviewItemStatus.Unavailable);
}

internal sealed record WildcardExpansion(
    IReadOnlyList<string> Paths,
    BulkFilePreviewItemStatus? FailureStatus)
{
    public static WildcardExpansion NoMatches { get; } = new(Array.Empty<string>(), null);

    public static WildcardExpansion AccessDenied { get; } = new(Array.Empty<string>(), BulkFilePreviewItemStatus.AccessDenied);

    public static WildcardExpansion InvalidPath { get; } = new(Array.Empty<string>(), BulkFilePreviewItemStatus.InvalidPath);

    public static WildcardExpansion Unavailable { get; } = new(Array.Empty<string>(), BulkFilePreviewItemStatus.Unavailable);
}
