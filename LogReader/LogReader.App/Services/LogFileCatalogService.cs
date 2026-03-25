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

    public Task<LogFileEntry> RegisterOpenAsync(string filePath, DateTime openedAtUtc)
        => _fileRepository.GetOrCreateByPathAsync(filePath, openedAtUtc);

    public async Task<IReadOnlyDictionary<string, LogFileEntry>> EnsureRegisteredAsync(IEnumerable<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        var distinctPaths = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctPaths.Count == 0)
            return new Dictionary<string, LogFileEntry>(StringComparer.OrdinalIgnoreCase);

        return await _fileRepository.GetOrCreateByPathsAsync(distinctPaths);
    }
}
