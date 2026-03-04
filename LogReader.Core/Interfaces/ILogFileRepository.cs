namespace LogReader.Core.Interfaces;

using LogReader.Core.Models;

/// <summary>
/// Persists log file entries (path, display name, timestamps).
/// </summary>
public interface ILogFileRepository
{
    /// <summary>Returns all known log file entries.</summary>
    Task<List<LogFileEntry>> GetAllAsync();

    /// <summary>Finds a log file entry by its unique ID, or null if not found.</summary>
    Task<LogFileEntry?> GetByIdAsync(string id);

    /// <summary>Finds a log file entry by its file path (case-insensitive), or null if not found.</summary>
    Task<LogFileEntry?> GetByPathAsync(string filePath);

    /// <summary>Adds a new log file entry.</summary>
    Task AddAsync(LogFileEntry entry);

    /// <summary>Updates an existing log file entry.</summary>
    Task UpdateAsync(LogFileEntry entry);

    /// <summary>Deletes a log file entry by ID.</summary>
    Task DeleteAsync(string id);
}
