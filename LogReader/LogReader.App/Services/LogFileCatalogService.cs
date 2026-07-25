namespace LogReader.App.Services;

using LogReader.Core.Interfaces;
using LogReader.Core.Models;

internal sealed class LogFileCatalogService
{
    private readonly ILogFileRepository _fileRepository;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly HashSet<string> _pendingCreatedFileIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _openedPendingFileIds = new(StringComparer.Ordinal);

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
        await _mutationGate.WaitAsync();
        try
        {
            var entry = await _fileRepository.GetOrCreateByPathAsync(filePath, openedAtUtc);
            if (_pendingCreatedFileIds.Contains(entry.Id))
                _openedPendingFileIds.Add(entry.Id);
            return entry;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<IReadOnlyDictionary<string, LogFileEntry>> EnsureRegisteredAsync(IEnumerable<string> filePaths)
        => (await EnsureRegisteredWithChangesAsync(filePaths)).EntriesByPath;

    public async Task<LogFileRegistrationBatch> EnsureRegisteredWithChangesAsync(IEnumerable<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        var distinctPaths = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctPaths.Count == 0)
        {
            return new LogFileRegistrationBatch(
                new Dictionary<string, LogFileEntry>(StringComparer.OrdinalIgnoreCase),
                Array.Empty<LogFileEntry>());
        }

        await _mutationGate.WaitAsync();
        try
        {
            var registration = await _fileRepository.RegisterByPathsAsync(distinctPaths);
            foreach (var entry in registration.CreatedEntries)
                _pendingCreatedFileIds.Add(entry.Id);

            return registration;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task RemoveCreatedEntriesIfUnreferencedAsync(
        IEnumerable<LogFileEntry> createdEntries,
        Func<Task<IReadOnlySet<string>>> getReferencedFileIdsAsync)
    {
        ArgumentNullException.ThrowIfNull(createdEntries);
        ArgumentNullException.ThrowIfNull(getReferencedFileIdsAsync);

        var createdIds = createdEntries
            .Select(entry => entry.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (createdIds.Count == 0)
            return;

        await _mutationGate.WaitAsync();
        try
        {
            var referencedIds = await getReferencedFileIdsAsync();
            var removableIds = createdIds
                .Where(id =>
                    !referencedIds.Contains(id) &&
                    !_openedPendingFileIds.Contains(id))
                .ToList();
            try
            {
                if (removableIds.Count > 0)
                    await _fileRepository.DeleteByIdsAsync(removableIds);
            }
            finally
            {
                CompleteRegistrationCore(createdIds);
            }
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task CompleteRegistrationAsync(IEnumerable<LogFileEntry> createdEntries)
    {
        ArgumentNullException.ThrowIfNull(createdEntries);

        var createdIds = createdEntries
            .Select(entry => entry.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (createdIds.Count == 0)
            return;

        await _mutationGate.WaitAsync();
        try
        {
            CompleteRegistrationCore(createdIds);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private void CompleteRegistrationCore(IEnumerable<string> createdIds)
    {
        foreach (var id in createdIds)
        {
            _pendingCreatedFileIds.Remove(id);
            _openedPendingFileIds.Remove(id);
        }
    }
}
