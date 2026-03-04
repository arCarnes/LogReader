namespace LogReader.Infrastructure.Repositories;

using System.Text.Json;
using System.Text.Json.Serialization;

internal static class JsonStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly string BasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LogReader");

    public static string GetFilePath(string fileName)
    {
        Directory.CreateDirectory(BasePath);
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
