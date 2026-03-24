namespace LogReader.Infrastructure.Repositories;

using System.IO;
using System.Text.Json;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public class JsonReplacementPatternRepository : IReplacementPatternRepository
{
    private const string FileName = "date-rolling-patterns.json";
    private const int CurrentSchemaVersion = 1;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<List<ReplacementPattern>> LoadAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            return await LoadPatternsCoreAsync().ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    public async Task SaveAsync(List<ReplacementPattern> patterns)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            await SavePatternsCoreAsync(patterns).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    private static async Task<List<ReplacementPattern>> LoadPatternsCoreAsync()
    {
        try
        {
            using var document = await JsonStore.LoadDocumentAsync(FileName).ConfigureAwait(false);
            if (document == null)
                return new List<ReplacementPattern>();

            return DeserializePatterns(document.RootElement);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "Date rolling patterns could not be loaded because the saved data is malformed or uses an unsupported version.",
                ex);
        }
    }

    private static List<ReplacementPattern> DeserializePatterns(JsonElement root)
    {
        if (TryGetEnvelopeData(root, out var schemaVersion, out var data))
        {
            if (schemaVersion != CurrentSchemaVersion)
                throw new JsonException($"Unsupported patterns schema version '{schemaVersion}'.");

            return DeserializeModel<List<ReplacementPattern>>(data);
        }

        return DeserializeModel<List<ReplacementPattern>>(root);
    }

    private static Task SavePatternsCoreAsync(List<ReplacementPattern> patterns)
        => JsonStore.SaveAsync(
            FileName,
            new VersionedRepositoryEnvelope<List<ReplacementPattern>>
            {
                SchemaVersion = CurrentSchemaVersion,
                Data = patterns
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
            throw new JsonException("Patterns payload is missing a valid schemaVersion.");
        }

        return true;
    }

    private static T DeserializeModel<T>(JsonElement element) where T : new()
        => element.Deserialize<T>(JsonStore.GetOptions()) ?? new T();
}
