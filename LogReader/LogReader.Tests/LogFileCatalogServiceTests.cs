using LogReader.App.Services;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

namespace LogReader.Tests;

public class LogFileCatalogServiceTests
{
    [Fact]
    public async Task EnsureRegisteredAsync_UsesBulkRepositoryPathForKnownFiles()
    {
        var existingEntries = new[]
        {
            new LogFileEntry { FilePath = @"C:\logs\a.log" },
            new LogFileEntry { FilePath = @"C:\logs\b.log" }
        };
        var repo = new RecordingLogFileRepository(existingEntries);
        var service = new LogFileCatalogService(repo);

        var entries = await service.EnsureRegisteredAsync(new[]
        {
            @"C:\logs\a.log",
            @"C:\logs\b.log",
            @"C:\logs\A.log"
        });

        Assert.Equal(1, repo.GetOrCreateByPathsCallCount);
        Assert.Equal(0, repo.GetOrCreateByPathCallCount);
        Assert.Equal(2, entries.Count);
        Assert.Equal(existingEntries[0].Id, entries[@"C:\logs\a.log"].Id);
        Assert.Equal(existingEntries[1].Id, entries[@"C:\logs\b.log"].Id);
    }

    private sealed class RecordingLogFileRepository : ILogFileRepository
    {
        private readonly List<LogFileEntry> _entries;

        public RecordingLogFileRepository(IEnumerable<LogFileEntry> entries)
        {
            _entries = entries.ToList();
        }

        public int GetOrCreateByPathsCallCount { get; private set; }

        public int GetOrCreateByPathCallCount { get; private set; }

        public Task<List<LogFileEntry>> GetAllAsync() => Task.FromResult(_entries.ToList());

        public Task<IReadOnlyDictionary<string, LogFileEntry>> GetByIdsAsync(IEnumerable<string> ids)
        {
            var idSet = ids.ToHashSet(StringComparer.Ordinal);
            return Task.FromResult<IReadOnlyDictionary<string, LogFileEntry>>(
                _entries
                    .Where(entry => idSet.Contains(entry.Id))
                    .ToDictionary(entry => entry.Id, StringComparer.Ordinal));
        }

        public Task<IReadOnlyDictionary<string, LogFileEntry>> GetByPathsAsync(IEnumerable<string> filePaths)
        {
            var pathSet = filePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return Task.FromResult<IReadOnlyDictionary<string, LogFileEntry>>(
                _entries
                    .Where(entry => pathSet.Contains(entry.FilePath))
                    .ToDictionary(entry => entry.FilePath, StringComparer.OrdinalIgnoreCase));
        }

        public Task<IReadOnlyDictionary<string, LogFileEntry>> GetOrCreateByPathsAsync(IEnumerable<string> filePaths)
        {
            GetOrCreateByPathsCallCount++;

            var result = new Dictionary<string, LogFileEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var filePath in filePaths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
                result[filePath] = GetOrCreateEntry(filePath);

            return Task.FromResult<IReadOnlyDictionary<string, LogFileEntry>>(result);
        }

        public Task<LogFileEntry> GetOrCreateByPathAsync(string filePath, DateTime? lastOpenedAtUtc = null)
        {
            GetOrCreateByPathCallCount++;
            var entry = GetOrCreateEntry(filePath);
            if (lastOpenedAtUtc.HasValue)
                entry.LastOpenedAt = lastOpenedAtUtc.Value;

            return Task.FromResult(entry);
        }

        public Task AddAsync(LogFileEntry entry)
        {
            _entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(LogFileEntry entry) => Task.CompletedTask;

        public Task DeleteAsync(string id)
        {
            _entries.RemoveAll(entry => entry.Id == id);
            return Task.CompletedTask;
        }

        private LogFileEntry GetOrCreateEntry(string filePath)
        {
            var existing = _entries.FirstOrDefault(entry =>
                string.Equals(entry.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                return existing;

            var entry = new LogFileEntry { FilePath = filePath };
            _entries.Add(entry);
            return entry;
        }
    }
}
