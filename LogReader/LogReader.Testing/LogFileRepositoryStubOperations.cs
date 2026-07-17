namespace LogReader.Testing;

using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public static class LogFileRepositoryStubOperations
{
    public static async Task<LogFileRegistrationBatch> RegisterByPathsAsync(
        ILogFileRepository repository,
        IEnumerable<string> filePaths)
    {
        var requestedPaths = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var existingEntries = await repository.GetByPathsAsync(requestedPaths);
        var entries = await repository.GetOrCreateByPathsAsync(requestedPaths);
        var createdEntries = entries
            .Where(pair => !existingEntries.ContainsKey(pair.Key))
            .Select(pair => pair.Value)
            .ToList();
        return new LogFileRegistrationBatch(entries, createdEntries);
    }

    public static async Task DeleteByIdsAsync(
        ILogFileRepository repository,
        IEnumerable<string> ids)
    {
        foreach (var id in ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
            await repository.DeleteAsync(id);
    }
}
