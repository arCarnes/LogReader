namespace LogReader.App.Services;

using System.IO;
using LogReader.Core.Models;

internal static class ImportedViewPathTrustAnalyzer
{
    private const int DefaultSampleCount = 3;

    public static ImportedViewPathTrustAssessment Assess(ViewExport export, int sampleCount = DefaultSampleCount)
    {
        ArgumentNullException.ThrowIfNull(export);

        sampleCount = Math.Max(1, sampleCount);

        var suspiciousPaths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in (export.Groups ?? new List<ViewExportGroup>())
                     .Where(group => group.Kind == LogGroupKind.Dashboard)
                     .SelectMany(group => group.FilePaths))
        {
            if (!RequiresConfirmation(path) || !seen.Add(path))
                continue;

            suspiciousPaths.Add(path);
        }

        return new ImportedViewPathTrustAssessment(
            suspiciousPaths.Count,
            suspiciousPaths.Take(sampleCount).ToList());
    }

    private static bool RequiresConfirmation(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (IsDevicePrefixedPath(path))
            return true;

        if (IsUncPath(path))
            return false;

        if (!Path.IsPathFullyQualified(path))
            return true;

        return !IsDriveQualifiedLocalPath(path);
    }

    private static bool IsDriveQualifiedLocalPath(string path)
        => path.Length >= 3 &&
           char.IsAsciiLetter(path[0]) &&
           path[1] == ':' &&
           IsDirectorySeparator(path[2]);

    private static bool IsUncPath(string path)
        => path.StartsWith(@"\\", StringComparison.Ordinal) ||
           path.StartsWith("//", StringComparison.Ordinal);

    private static bool IsDevicePrefixedPath(string path)
        => path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
           path.StartsWith(@"\\.\", StringComparison.Ordinal) ||
           path.StartsWith("//?/", StringComparison.Ordinal) ||
           path.StartsWith("//./", StringComparison.Ordinal);

    private static bool IsDirectorySeparator(char value)
        => value is '\\' or '/';
}

internal sealed record ImportedViewPathTrustAssessment(
    int SuspiciousPathCount,
    IReadOnlyList<string> SamplePaths)
{
    public bool RequiresConfirmation => SuspiciousPathCount > 0;
}
