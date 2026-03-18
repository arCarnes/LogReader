namespace LogReader.Tests;

using System.Text.Json;
using LogReader.Infrastructure.Repositories;

public static class JsonRepositoryAssertions
{
    public static async Task<JsonDocument> LoadPersistedDocumentAsync(string fileName)
    {
        var path = JsonStore.GetFilePath(fileName);
        var json = await File.ReadAllTextAsync(path);
        return JsonDocument.Parse(json);
    }

    public static JsonElement AssertVersionedEnvelope(JsonDocument document, int expectedSchemaVersion = 1)
    {
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.True(root.TryGetProperty("schemaVersion", out var schemaVersion));
        Assert.Equal(expectedSchemaVersion, schemaVersion.GetInt32());
        Assert.True(root.TryGetProperty("data", out var data));
        return data;
    }
}
