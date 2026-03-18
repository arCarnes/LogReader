namespace LogReader.Core;

using System.Text.Json;
using System.Text.Json.Serialization;

public static class AppPaths
{
    private const string ProductDirectoryName = "LogReader";
    private const string RuntimeConfigurationFileName = "LogReader.runtime.json";
    private static readonly AsyncLocal<string?> TestRootPath = new();
    private static readonly AsyncLocal<string?> TestBaseDirectory = new();

    public static string RootDirectory => TestRootPath.Value ?? ResolveConfiguredRootDirectory();

    public static string DataDirectory => Path.Combine(RootDirectory, "Data");

    public static string ViewsDirectory => Path.Combine(DataDirectory, "Views");

    public static string CacheDirectory => Path.Combine(RootDirectory, "Cache");

    public static string IndexDirectory => Path.Combine(CacheDirectory, "idx");

    public static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    public static void SetRootPathForTests(string? path) => TestRootPath.Value = path;

    public static void SetBaseDirectoryForTests(string? path) => TestBaseDirectory.Value = path;

    private static string ResolveConfiguredRootDirectory()
    {
        var configurationPath = Path.Combine(GetBaseDirectory(), RuntimeConfigurationFileName);
        if (!File.Exists(configurationPath))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ProductDirectoryName);
        }

        RuntimeConfiguration configuration;

        try
        {
            using var stream = File.OpenRead(configurationPath);
            configuration = JsonSerializer.Deserialize<RuntimeConfiguration>(stream) ?? new RuntimeConfiguration();
        }
        catch (JsonException ex)
        {
            throw new AppRuntimeConfigurationException(
                configurationPath,
                "The runtime configuration file is not valid JSON.",
                ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AppRuntimeConfigurationException(
                configurationPath,
                "The runtime configuration file could not be read.",
                ex);
        }

        if (string.IsNullOrWhiteSpace(configuration.StorageRoot))
        {
            throw new AppRuntimeConfigurationException(
                configurationPath,
                "The runtime configuration file must define a non-empty storageRoot value.");
        }

        try
        {
            return NormalizeDirectoryPath(configuration.StorageRoot, GetBaseDirectory());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new AppRuntimeConfigurationException(
                configurationPath,
                "The runtime configuration file contains an invalid storageRoot value.",
                ex);
        }
    }

    private static string GetBaseDirectory() => TestBaseDirectory.Value ?? AppContext.BaseDirectory;

    private static string NormalizeDirectoryPath(string storageRoot, string baseDirectory)
    {
        var trimmedStorageRoot = storageRoot.Trim();
        var combinedPath = Path.IsPathRooted(trimmedStorageRoot)
            ? trimmedStorageRoot
            : Path.Combine(baseDirectory, trimmedStorageRoot);
        var fullPath = Path.GetFullPath(combinedPath);
        var pathRoot = Path.GetPathRoot(fullPath);

        if (!string.IsNullOrEmpty(pathRoot) &&
            string.Equals(fullPath, pathRoot, StringComparison.OrdinalIgnoreCase))
        {
            return fullPath;
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private sealed class RuntimeConfiguration
    {
        [JsonPropertyName("storageRoot")]
        public string? StorageRoot { get; set; }
    }
}
