namespace LogReader.Infrastructure.Repositories;

using System.Text.Json;
using System.Text.Json.Serialization;
using LogReader.Core;

internal static class JsonStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string BasePath => AppPaths.DataDirectory;

    /// <summary>
    /// Override the storage base path for tests. Pass null to restore the default.
    /// Each async execution flow (xUnit test class) gets its own isolated value.
    /// </summary>
    internal static void SetBasePathForTests(string? path) => AppPaths.SetRootPathForTests(path);

    public static string GetFilePath(string fileName)
    {
        AppPaths.EnsureDirectory(BasePath);
        return Path.Combine(BasePath, fileName);
    }

    public static async Task<T> LoadAsync<T>(string fileName) where T : new()
    {
        var path = GetFilePath(fileName);
        if (!File.Exists(path)) return new T();
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, Options).ConfigureAwait(false) ?? new T();
    }

    public static async Task SaveAsync<T>(string fileName, T data)
    {
        var path = GetFilePath(fileName);
        var tempPath = path + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, data, Options).ConfigureAwait(false);
        }
        File.Move(tempPath, path, overwrite: true);
    }

    public static JsonSerializerOptions GetOptions() => Options;
}
