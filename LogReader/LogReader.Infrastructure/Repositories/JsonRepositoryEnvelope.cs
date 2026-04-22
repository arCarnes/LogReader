namespace LogReader.Infrastructure.Repositories;

using System.Text.Json;

internal static class JsonRepositoryEnvelope
{
    public static (T Data, bool ShouldRewrite) Deserialize<T>(
        JsonElement root,
        int currentSchemaVersion,
        string repositoryName) where T : new()
    {
        if (TryGetData(root, repositoryName, out var schemaVersion, out var data))
        {
            if (schemaVersion != currentSchemaVersion)
                throw new JsonException($"Unsupported {repositoryName} schema version '{schemaVersion}'.");

            return (DeserializeModel<T>(data), false);
        }

        return (DeserializeModel<T>(root), true);
    }

    private static bool TryGetData(
        JsonElement root,
        string repositoryName,
        out int schemaVersion,
        out JsonElement data)
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
            throw new JsonException($"{repositoryName} payload is missing a valid schemaVersion.");
        }

        return true;
    }

    private static T DeserializeModel<T>(JsonElement element) where T : new()
        => element.Deserialize<T>(JsonStore.GetOptions()) ?? new T();
}
