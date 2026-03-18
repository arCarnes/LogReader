namespace LogReader.Core.Models;

public readonly record struct TimestampNavigationResult(
    long LineNumber,
    bool HasMatch,
    bool WasExactMatch,
    string StatusMessage);
