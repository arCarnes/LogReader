namespace LogReader.App.Services;

using System.IO;
using LogReader.Core.Models;

internal readonly struct RecentTabStateKey : IEquatable<RecentTabStateKey>
{
    public RecentTabStateKey(string filePath, string? scopeDashboardId)
    {
        FilePath = Path.GetFullPath(filePath ?? string.Empty);
        ScopeDashboardId = scopeDashboardId ?? string.Empty;
    }

    public string FilePath { get; }

    public string ScopeDashboardId { get; }

    public bool Equals(RecentTabStateKey other)
        => StringComparer.OrdinalIgnoreCase.Equals(FilePath, other.FilePath) &&
           StringComparer.Ordinal.Equals(ScopeDashboardId, other.ScopeDashboardId);

    public override bool Equals(object? obj)
        => obj is RecentTabStateKey other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(FilePath),
            StringComparer.Ordinal.GetHashCode(ScopeDashboardId));
}

internal sealed class RecentTabState
{
    public FileEncoding RequestedEncoding { get; init; }

    public bool IsPinned { get; init; }

    public int ViewportStartLine { get; init; }

    public int NavigateToLineNumber { get; init; }

    public LogFilterSession.FilterSnapshot? FilterSnapshot { get; init; }
}
