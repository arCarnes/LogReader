namespace LogReader.Infrastructure.Repositories;

using System.Text.Json;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public class JsonSessionRepository : ISessionRepository
{
    private const string FileName = "session.json";
    private const int CurrentSchemaVersion = 1;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<SessionState> LoadAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var (state, shouldRewrite) = await LoadStateCoreAsync().ConfigureAwait(false);
            if (shouldRewrite)
                await SaveStateCoreAsync(state).ConfigureAwait(false);

            return state;
        }
        finally { _lock.Release(); }
    }

    public async Task SaveAsync(SessionState state)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            await SaveStateCoreAsync(state).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    private static async Task<(SessionState State, bool ShouldRewrite)> LoadStateCoreAsync()
    {
        using var document = await JsonStore.LoadDocumentAsync(FileName).ConfigureAwait(false);
        if (document == null)
            return (new SessionState(), false);

        return DeserializeState(document.RootElement);
    }

    private static (SessionState State, bool ShouldRewrite) DeserializeState(JsonElement root)
    {
        if (TryGetEnvelopeData(root, out var schemaVersion, out var data))
        {
            if (schemaVersion != CurrentSchemaVersion)
                throw new JsonException($"Unsupported session schema version '{schemaVersion}'.");

            return (DeserializeModel<SessionState>(data), false);
        }

        return (DeserializeModel<SessionState>(root), true);
    }

    private static Task SaveStateCoreAsync(SessionState state)
        => JsonStore.SaveAsync(
            FileName,
            new VersionedRepositoryEnvelope<SessionState>
            {
                SchemaVersion = CurrentSchemaVersion,
                Data = state
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
            throw new JsonException("Session payload is missing a valid schemaVersion.");
        }

        return true;
    }

    private static T DeserializeModel<T>(JsonElement element) where T : new()
        => element.Deserialize<T>(JsonStore.GetOptions()) ?? new T();
}
