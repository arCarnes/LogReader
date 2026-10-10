namespace LogReader.Infrastructure.Repositories;

using System.Text.Json;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public sealed class JsonUiStateRepository : IUiStateRepository
{
    private const string FileName = "ui-state.json";
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<UiState> LoadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var document = await JsonStore.LoadDocumentAsync(FileName).ConfigureAwait(false);
            if (document == null)
                return new UiState();
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) ||
                !root.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var number) || number != 1 || data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("groupExpansionById", out var expansion) || expansion.ValueKind != JsonValueKind.Object)
                throw new JsonException("UI state is missing its version-1 expansion data or uses an unsupported version.");
            var state = data.Deserialize<UiState>(JsonStore.GetOptions())!;
            return state;
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(UiState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        // Snapshot before awaiting so callers cannot change data during serialization.
        var snapshot = new UiState { GroupExpansionById = new(state.GroupExpansionById, StringComparer.Ordinal) };
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await JsonStore.SaveAsync(FileName, new VersionedRepositoryEnvelope<UiState>
            {
                SchemaVersion = 1,
                Data = snapshot
            }).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
