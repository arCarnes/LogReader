namespace LogReader.App.Services;

using LogReader.Core.Interfaces;
using LogReader.Core.Models;

internal sealed class LogFileCatalogService
{
    private readonly ILogFileRepository _fileRepository;

    public LogFileCatalogService(ILogFileRepository fileRepository)
    {
        _fileRepository = fileRepository;
    }

    public Task<IReadOnlyDictionary<string, LogFileEntry>> GetByIdsAsync(IEnumerable<string> ids)
        => _fileRepository.GetByIdsAsync(ids);

    public Task<IReadOnlyDictionary<string, LogFileEntry>> GetByPathsAsync(IEnumerable<string> filePaths)
        => _fileRepository.GetByPathsAsync(filePaths);

    public async Task<LogFileEntry> RegisterOpenAsync(string filePath, DateTime openedAtUtc)
    {
        var existingByPath = await _fileRepository.GetByPathsAsync(new[] { filePath });
        if (existingByPath.TryGetValue(filePath, out var existing))
        {
            existing.LastOpenedAt = openedAtUtc;
            await _fileRepository.UpdateAsync(existing);
            return existing;
        }

        var entry = new LogFileEntry
        {
            FilePath = filePath,
            LastOpenedAt = openedAtUtc
        };
        await _fileRepository.AddAsync(entry);
        return entry;
    }

    public async Task<IReadOnlyDictionary<string, LogFileEntry>> EnsureRegisteredAsync(IEnumerable<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        var distinctPaths = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctPaths.Count == 0)
            return new Dictionary<string, LogFileEntry>(StringComparer.OrdinalIgnoreCase);

        var existingByPath = await _fileRepository.GetByPathsAsync(distinctPaths);
        var result = new Dictionary<string, LogFileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in distinctPaths)
        {
            if (existingByPath.TryGetValue(path, out var existing))
            {
                result[path] = existing;
                continue;
            }

            var entry = new LogFileEntry { FilePath = path };
            await _fileRepository.AddAsync(entry);
            result[path] = entry;
        }

        return result;
    }
}
