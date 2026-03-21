namespace LogReader.Testing;

using System.Text.Json;
using LogReader.Core;

public static class JsonRepositoryAssertions
{
    public static async Task<JsonDocument> LoadPersistedDocumentAsync(string fileName)
    {
        var path = Path.Combine(AppPaths.EnsureDirectory(AppPaths.DataDirectory), fileName);
        var json = await File.ReadAllTextAsync(path);
        return JsonDocument.Parse(json);
    }

    public static JsonElement AssertVersionedEnvelope(JsonDocument document, int expectedSchemaVersion = 1)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Expected the persisted repository payload to be a JSON object.");

        if (!root.TryGetProperty("schemaVersion", out var schemaVersion))
            throw new InvalidDataException("Expected the persisted repository payload to contain a schemaVersion property.");

        if (schemaVersion.GetInt32() != expectedSchemaVersion)
        {
            throw new InvalidDataException(
                $"Expected schemaVersion {expectedSchemaVersion}, but found {schemaVersion.GetInt32()}.");
        }

        if (!root.TryGetProperty("data", out var data))
            throw new InvalidDataException("Expected the persisted repository payload to contain a data property.");

        return data;
    }
}
