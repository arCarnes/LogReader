namespace LogReader.Core.Interfaces;

using LogReader.Core.Models;

/// <summary>
/// Persists known log file entries (stable ID, file path, and last-opened timestamp).
/// </summary>
public interface ILogFileRepository
{
    /// <summary>Returns all known log file entries.</summary>
    Task<List<LogFileEntry>> GetAllAsync();

    /// <summary>Finds known log file entries by unique ID.</summary>
    Task<IReadOnlyDictionary<string, LogFileEntry>> GetByIdsAsync(IEnumerable<string> ids);

    /// <summary>Finds known log file entries by file path (case-insensitive).</summary>
    Task<IReadOnlyDictionary<string, LogFileEntry>> GetByPathsAsync(IEnumerable<string> filePaths);

    /// <summary>Finds a log file entry by its unique ID, or null if not found.</summary>
    async Task<LogFileEntry?> GetByIdAsync(string id)
    {
        var entries = await GetByIdsAsync(new[] { id });
        return entries.TryGetValue(id, out var entry) ? entry : null;
    }

    /// <summary>Finds a log file entry by its file path (case-insensitive), or null if not found.</summary>
    async Task<LogFileEntry?> GetByPathAsync(string filePath)
    {
        var entries = await GetByPathsAsync(new[] { filePath });
        return entries.TryGetValue(filePath, out var entry) ? entry : null;
    }

    /// <summary>Gets existing entries for file paths, or creates missing ones atomically in one batch.</summary>
    Task<IReadOnlyDictionary<string, LogFileEntry>> GetOrCreateByPathsAsync(IEnumerable<string> filePaths);

    /// <summary>Gets the existing entry for a file path, or creates one atomically when missing.</summary>
    Task<LogFileEntry> GetOrCreateByPathAsync(string filePath, DateTime? lastOpenedAtUtc = null);

    /// <summary>Adds a new log file entry.</summary>
    Task AddAsync(LogFileEntry entry);

    /// <summary>Updates an existing log file entry.</summary>
    Task UpdateAsync(LogFileEntry entry);

    /// <summary>Deletes a log file entry by ID.</summary>
    Task DeleteAsync(string id);
}
