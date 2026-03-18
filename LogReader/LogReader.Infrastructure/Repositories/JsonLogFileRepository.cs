namespace LogReader.Infrastructure.Repositories;

using System.Text.Json;
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

    public async Task<LogFileEntry?> GetByIdAsync(string id)
    {
        var all = await GetAllAsync();
        return all.FirstOrDefault(f => f.Id == id);
    }

    public async Task<LogFileEntry?> GetByPathAsync(string filePath)
    {
        var all = await GetAllAsync();
        return all.FirstOrDefault(f => string.Equals(f.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
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

            return DeserializeEntries(document.RootElement);
        }
        catch (JsonException)
        {
            // Clean break: corrupt or incompatible file metadata is reset.
            return (new List<LogFileEntry>(), true);
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
}
