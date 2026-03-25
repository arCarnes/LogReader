namespace LogReader.Infrastructure.Repositories;

using System.Text.Json;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public class JsonLogFileRepository : ILogFileRepository
{
    private const string FileName = "logfiles.json";
    private const int CurrentSchemaVersion = 1;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<List<LogFileEntry>> GetAllAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var (entries, shouldRewrite) = await LoadEntriesCoreAsync().ConfigureAwait(false);
            if (shouldRewrite)
                await SaveEntriesCoreAsync(entries).ConfigureAwait(false);

            return entries;
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyDictionary<string, LogFileEntry>> GetByIdsAsync(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var requestedIds = ids
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (requestedIds.Count == 0)
            return new Dictionary<string, LogFileEntry>(StringComparer.Ordinal);

        await _lock.WaitAsync();
        try
        {
            var (entries, shouldRewrite) = await LoadEntriesCoreAsync().ConfigureAwait(false);
            if (shouldRewrite)
                await SaveEntriesCoreAsync(entries).ConfigureAwait(false);

            var requestedIdSet = requestedIds.ToHashSet(StringComparer.Ordinal);
            return entries
                .Where(entry => requestedIdSet.Contains(entry.Id))
                .ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyDictionary<string, LogFileEntry>> GetByPathsAsync(IEnumerable<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        var requestedPaths = NormalizeRequestedPaths(filePaths);
        if (requestedPaths.Count == 0)
            return new Dictionary<string, LogFileEntry>(StringComparer.OrdinalIgnoreCase);

        await _lock.WaitAsync();
        try
        {
            var (entries, shouldRewrite) = await LoadEntriesCoreAsync().ConfigureAwait(false);
            if (shouldRewrite)
                await SaveEntriesCoreAsync(entries).ConfigureAwait(false);

            var requestedPathSet = requestedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return entries
                .Where(entry => requestedPathSet.Contains(entry.FilePath))
                .ToDictionary(entry => entry.FilePath, StringComparer.OrdinalIgnoreCase);
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyDictionary<string, LogFileEntry>> GetOrCreateByPathsAsync(IEnumerable<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        var requestedPaths = NormalizeRequestedPaths(filePaths);
        if (requestedPaths.Count == 0)
            return new Dictionary<string, LogFileEntry>(StringComparer.OrdinalIgnoreCase);

        await _lock.WaitAsync();
        try
        {
            var (all, shouldRewrite) = await LoadEntriesCoreAsync().ConfigureAwait(false);
            var entriesByPath = all.ToDictionary(entry => entry.FilePath, StringComparer.OrdinalIgnoreCase);
            var result = new Dictionary<string, LogFileEntry>(StringComparer.OrdinalIgnoreCase);
            var createdAny = false;

            foreach (var path in requestedPaths)
            {
                if (!entriesByPath.TryGetValue(path, out var entry))
                {
                    entry = new LogFileEntry
                    {
                        FilePath = path
                    };
                    all.Add(entry);
                    entriesByPath[path] = entry;
                    createdAny = true;
                }

                result[path] = entry;
            }

            if (createdAny)
            {
                ValidateEntries(all);
                await SaveEntriesCoreAsync(all).ConfigureAwait(false);
            }
            else if (shouldRewrite)
            {
                await SaveEntriesCoreAsync(all).ConfigureAwait(false);
            }

            return result;
        }
        finally { _lock.Release(); }
    }

    public async Task<LogFileEntry> GetOrCreateByPathAsync(string filePath, DateTime? lastOpenedAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        await _lock.WaitAsync();
        try
        {
            var (all, shouldRewrite) = await LoadEntriesCoreAsync().ConfigureAwait(false);
            if (shouldRewrite)
                await SaveEntriesCoreAsync(all).ConfigureAwait(false);

            var existing = all.FirstOrDefault(entry =>
                string.Equals(entry.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (lastOpenedAtUtc.HasValue && existing.LastOpenedAt != lastOpenedAtUtc.Value)
                {
                    existing.LastOpenedAt = lastOpenedAtUtc.Value;
                    ValidateEntries(all);
                    await SaveEntriesCoreAsync(all).ConfigureAwait(false);
                }

                return existing;
            }

            var entry = new LogFileEntry
            {
                FilePath = filePath
            };
            if (lastOpenedAtUtc.HasValue)
                entry.LastOpenedAt = lastOpenedAtUtc.Value;

            all.Add(entry);
            ValidateEntries(all);
            await SaveEntriesCoreAsync(all).ConfigureAwait(false);
            return entry;
        }
        finally { _lock.Release(); }
    }

    public async Task AddAsync(LogFileEntry entry)
    {
        await _lock.WaitAsync();
        try
        {
            var (all, shouldRewrite) = await LoadEntriesCoreAsync().ConfigureAwait(false);
            if (shouldRewrite)
                await SaveEntriesCoreAsync(all).ConfigureAwait(false);

            all.Add(entry);
            ValidateEntries(all);
            await SaveEntriesCoreAsync(all).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    public async Task UpdateAsync(LogFileEntry entry)
    {
        await _lock.WaitAsync();
        try
        {
            var (all, shouldRewrite) = await LoadEntriesCoreAsync().ConfigureAwait(false);
            if (shouldRewrite)
                await SaveEntriesCoreAsync(all).ConfigureAwait(false);

            var idx = all.FindIndex(f => f.Id == entry.Id);
            if (idx >= 0) all[idx] = entry;
            ValidateEntries(all);
            await SaveEntriesCoreAsync(all).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            var (all, shouldRewrite) = await LoadEntriesCoreAsync().ConfigureAwait(false);
            if (shouldRewrite)
                await SaveEntriesCoreAsync(all).ConfigureAwait(false);

            all.RemoveAll(f => f.Id == id);
            ValidateEntries(all);
            await SaveEntriesCoreAsync(all).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    private static async Task<(List<LogFileEntry> Entries, bool ShouldRewrite)> LoadEntriesCoreAsync()
    {
        try
        {
            using var document = await JsonStore.LoadDocumentAsync(FileName).ConfigureAwait(false);
            if (document == null)
                return (new List<LogFileEntry>(), false);

            var (entries, shouldRewrite) = DeserializeEntries(document.RootElement);
            ValidateEntries(entries);
            return (entries, shouldRewrite);
        }
        catch (PersistedStateRecoveryException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw CreateRecoveryException(
                "The saved log file metadata is not valid JSON.",
                ex);
        }
        catch (InvalidDataException ex)
        {
            throw CreateRecoveryException(ex.Message, ex);
        }
    }

    private static (List<LogFileEntry> Entries, bool ShouldRewrite) DeserializeEntries(JsonElement root)
    {
        if (TryGetEnvelopeData(root, out var schemaVersion, out var data))
        {
            if (schemaVersion != CurrentSchemaVersion)
                throw new JsonException($"Unsupported log file schema version '{schemaVersion}'.");

            return (DeserializeModel<List<LogFileEntry>>(data), false);
        }

        return (DeserializeModel<List<LogFileEntry>>(root), true);
    }

    private static Task SaveEntriesCoreAsync(List<LogFileEntry> entries)
        => JsonStore.SaveAsync(
            FileName,
            new VersionedRepositoryEnvelope<List<LogFileEntry>>
            {
                SchemaVersion = CurrentSchemaVersion,
                Data = entries
            });

    private static bool TryGetEnvelopeData(JsonElement root, out int schemaVersion, out JsonElement data)
    {
        schemaVersion = 0;
        data = default;

        if (root.ValueKind != JsonValueKind.Object)
            return false;

        if (!root.TryGetProperty("data", out data))
            return false;

        if (!root.TryGetProperty("schemaVersion", out var schemaVersionElement) ||
            !schemaVersionElement.TryGetInt32(out schemaVersion))
        {
            throw new JsonException("Log file payload is missing a valid schemaVersion.");
        }

        return true;
    }

    private static T DeserializeModel<T>(JsonElement element) where T : new()
        => element.Deserialize<T>(JsonStore.GetOptions()) ?? new T();

    private static List<string> NormalizeRequestedPaths(IEnumerable<string> filePaths)
        => filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static void ValidateEntries(IReadOnlyList<LogFileEntry> entries)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Id))
                throw new InvalidDataException("A saved log file entry is missing its ID.");

            if (!seenIds.Add(entry.Id))
                throw new InvalidDataException($"Duplicate saved log file entry ID '{entry.Id}'.");

            if (string.IsNullOrWhiteSpace(entry.FilePath))
                throw new InvalidDataException($"Saved log file entry '{entry.Id}' is missing its file path.");

            if (!seenPaths.Add(entry.FilePath))
                throw new InvalidDataException($"Duplicate saved log file path '{entry.FilePath}'.");
        }
    }

    private static PersistedStateRecoveryException CreateRecoveryException(string reason, Exception innerException)
        => new(
            "log file metadata",
            JsonStore.GetFilePath(FileName),
            reason,
            innerException);
}
