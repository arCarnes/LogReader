namespace LogReader.Infrastructure.Repositories;

using System.Text.Json;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public class JsonSettingsRepository : ISettingsRepository
{
    private const string FileName = "settings.json";
    private const int CurrentSchemaVersion = 1;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<AppSettings> LoadAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var (settings, shouldRewrite) = await LoadSettingsCoreAsync().ConfigureAwait(false);
            if (shouldRewrite)
                await SaveSettingsCoreAsync(settings).ConfigureAwait(false);

            return settings;
        }
        finally { _lock.Release(); }
    }

    public async Task SaveAsync(AppSettings settings)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            await SaveSettingsCoreAsync(settings).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    private static async Task<(AppSettings Settings, bool ShouldRewrite)> LoadSettingsCoreAsync()
    {
        try
        {
            using var document = await JsonStore.LoadDocumentAsync(FileName).ConfigureAwait(false);
            if (document == null)
                return (new AppSettings(), false);

            return DeserializeSettings(document.RootElement);
        }
        catch (JsonException)
        {
            // Clean break: corrupt or incompatible settings are reset.
            return (new AppSettings(), true);
        }
    }

    private static (AppSettings Settings, bool ShouldRewrite) DeserializeSettings(JsonElement root)
    {
        if (TryGetEnvelopeData(root, out var schemaVersion, out var data))
        {
            if (schemaVersion != CurrentSchemaVersion)
                throw new JsonException($"Unsupported settings schema version '{schemaVersion}'.");

            return (DeserializeModel<AppSettings>(data), false);
        }

        return (DeserializeModel<AppSettings>(root), true);
    }

    private static Task SaveSettingsCoreAsync(AppSettings settings)
        => JsonStore.SaveAsync(
            FileName,
            new VersionedRepositoryEnvelope<AppSettings>
            {
                SchemaVersion = CurrentSchemaVersion,
                Data = settings
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
            throw new JsonException("Settings payload is missing a valid schemaVersion.");
        }

        return true;
    }

    private static T DeserializeModel<T>(JsonElement element) where T : new()
        => element.Deserialize<T>(JsonStore.GetOptions()) ?? new T();
}
