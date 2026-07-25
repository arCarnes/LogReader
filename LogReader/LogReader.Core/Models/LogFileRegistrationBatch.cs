namespace LogReader.Core.Models;

public sealed record LogFileRegistrationBatch(
    IReadOnlyDictionary<string, LogFileEntry> EntriesByPath,
    IReadOnlyList<LogFileEntry> CreatedEntries);
