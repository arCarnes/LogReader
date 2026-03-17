namespace LogReader.Core.Interfaces;

using LogReader.Core.Models;

/// <summary>
/// Persists log groups (named collections of file IDs) with import/export support.
/// </summary>
public interface ILogGroupRepository
{
    /// <summary>Returns all log groups.</summary>
    Task<List<LogGroup>> GetAllAsync();

    /// <summary>Finds a group by its unique ID, or null if not found.</summary>
    Task<LogGroup?> GetByIdAsync(string id);

    /// <summary>Adds a new log group.</summary>
    Task AddAsync(LogGroup group);

    /// <summary>Updates an existing log group.</summary>
    Task UpdateAsync(LogGroup group);

    /// <summary>Deletes a log group by ID.</summary>
    Task DeleteAsync(string id);

    /// <summary>Persists the sort order for all groups.</summary>
    Task ReorderAsync(List<string> orderedIds);

    /// <summary>Exports the current dashboard view to a JSON file.</summary>
    Task ExportViewAsync(string exportPath);

    /// <summary>Imports a dashboard view from a JSON file, or returns null if the file doesn't exist.</summary>
    Task<ViewExport?> ImportViewAsync(string importPath);
}
