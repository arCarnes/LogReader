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

    public static async Task UpdateDisplayNamesAsync(
        ILogFileRepository repository,
        IReadOnlyDictionary<string, string?> names)
    {
        var normalized = names.ToDictionary(pair => pair.Key, pair => LogReader.Core.LogFileDisplayName.Normalize(pair.Value));
        var entries = await repository.GetByIdsAsync(normalized.Keys);
        foreach (var id in normalized.Keys)
        {
            if (!entries.ContainsKey(id))
                throw new KeyNotFoundException(id);
        }
        foreach (var (id, name) in normalized)
        {
            entries[id].DisplayName = name;
            await repository.UpdateAsync(entries[id]);
        }
    }

    public static async Task DeleteByIdsAsync(
        ILogFileRepository repository,
        IEnumerable<string> ids)
    {
        foreach (var id in ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
            await repository.DeleteAsync(id);
    }
}
