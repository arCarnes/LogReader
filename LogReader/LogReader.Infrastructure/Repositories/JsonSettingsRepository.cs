namespace LogReader.Infrastructure.Repositories;

using System.Text.Json;
using LogReader.Core;
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
        catch (PersistedStateRecoveryException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw CreateRecoveryException(
                "The saved settings are not valid JSON.",
                ex);
        }
        catch (InvalidDataException ex)
        {
            throw CreateRecoveryException(ex.Message, ex);
        }
    }

    private static (AppSettings Settings, bool ShouldRewrite) DeserializeSettings(JsonElement root)
        => JsonRepositoryEnvelope.Deserialize<AppSettings>(
            root,
            CurrentSchemaVersion,
            "settings");

    private static Task SaveSettingsCoreAsync(AppSettings settings)
        => JsonStore.SaveAsync(
            FileName,
            new VersionedRepositoryEnvelope<AppSettings>
            {
                SchemaVersion = CurrentSchemaVersion,
                Data = settings
            });

    private static PersistedStateRecoveryException CreateRecoveryException(string reason, Exception innerException)
        => new(
            "settings",
            JsonStore.GetFilePath(FileName),
            reason,
            innerException);
}
