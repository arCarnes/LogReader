namespace LogReader.Core;

using System.Collections.Immutable;
using LogReader.Core.Models;

public static class ConfiguredDatePathResolver
{
    public static bool TryResolveCandidates(
        string basePath,
        ImmutableArray<ConfiguredDatePathPattern> patterns,
        DateOnly referenceDate,
        int dateOffsetDays,
        out ImmutableArray<string> candidates,
        out string? errorCode,
        out string? errorMessage)
    {
        candidates = ImmutableArray<string>.Empty;
        errorCode = null;
        errorMessage = null;

        if (dateOffsetDays < 0)
        {
            errorCode = "invalid_date_offset";
            errorMessage = "dateOffsetDays cannot be negative.";
            return false;
        }

        if (!TryNormalizePath(basePath, out var normalizedBasePath))
        {
            errorCode = "invalid_configured_path";
            errorMessage = "The configured log file path is invalid.";
            return false;
        }

        if (dateOffsetDays == 0)
        {
            candidates = [normalizedBasePath];
            return true;
        }

        DateTime targetDate;
        try
        {
            targetDate = referenceDate.AddDays(-dateOffsetDays).ToDateTime(TimeOnly.MinValue);
        }
        catch (ArgumentOutOfRangeException)
        {
            errorCode = "invalid_date_offset";
            errorMessage = "dateOffsetDays is outside the supported calendar range.";
            return false;
        }

        if (patterns.IsDefaultOrEmpty)
        {
            errorCode = "date_patterns_not_configured";
            errorMessage = "No date rolling patterns are configured in WeezTail.";
            return false;
        }

        var resolved = new List<string>();
        string? firstError = null;
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern.FindPattern))
            {
                firstError ??= "A configured date rolling pattern has an empty find value.";
                continue;
            }

            if (!ReplacementTokenParser.TryExpand(
                    pattern.ReplacePattern,
                    targetDate,
                    out var expandedReplacement,
                    out var expansionError))
            {
                firstError ??= expansionError ?? "A configured date rolling pattern is invalid.";
                continue;
            }

            if (basePath.IndexOf(pattern.FindPattern, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            if (!TryReplaceBounded(
                    basePath,
                    pattern.FindPattern,
                    expandedReplacement,
                    out var transformed))
            {
                firstError ??= "A configured date rolling pattern produced a path that is too long.";
                continue;
            }
            if (!TryNormalizePath(transformed, out var normalizedCandidate))
            {
                firstError ??= "A configured date rolling pattern produced an invalid path.";
                continue;
            }

            if (!resolved.Contains(normalizedCandidate, StringComparer.OrdinalIgnoreCase))
                resolved.Add(normalizedCandidate);
        }

        if (resolved.Count == 0)
        {
            errorCode = "date_pattern_no_match";
            errorMessage = firstError ?? "No configured date rolling pattern matched this log file.";
            return false;
        }

        candidates = resolved.ToImmutableArray();
        return true;
    }

    private static bool TryReplaceBounded(
        string source,
        string find,
        string replacement,
        out string transformed)
    {
        transformed = string.Empty;
        var occurrenceCount = 0;
        var position = 0;
        while ((position = source.IndexOf(find, position, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            occurrenceCount++;
            position += find.Length;
        }

        var estimatedLength = (long)source.Length +
                              ((long)occurrenceCount * (replacement.Length - find.Length));
        if (estimatedLength < 0 || estimatedLength > ConfiguredLogLimits.DefaultMaxPhysicalPathCharacters)
            return false;

        transformed = source.Replace(find, replacement, StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private static bool TryNormalizePath(string path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            normalized = Path.GetFullPath(path);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
