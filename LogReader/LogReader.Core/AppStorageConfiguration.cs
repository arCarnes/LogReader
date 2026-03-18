namespace LogReader.Core;

using System.Text.Json;
using System.Text.Json.Serialization;

internal enum AppInstallMode
{
    Portable,
    Msi
}

internal enum StorageMode
{
    ExeDirectory,
    Absolute
}

internal sealed class AppStorageConfiguration
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public AppInstallMode InstallMode { get; init; }

    public StorageMode StorageMode { get; init; }

    public string? StorageRootPath { get; init; }

    public static AppStorageConfiguration Load(string configPath)
    {
        try
        {
            var json = File.ReadAllText(configPath);
            var configuration = JsonSerializer.Deserialize<AppStorageConfiguration>(json, SerializerOptions);
            if (configuration == null)
            {
                throw new InstallConfigurationException(
                    "The install configuration file did not contain a valid configuration.",
                    configPath);
            }

            configuration.Validate(configPath);
            return configuration;
        }
        catch (InstallConfigurationException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new InstallConfigurationException(
                "The install configuration file is not valid JSON.",
                configPath,
                ex);
        }
        catch (IOException ex)
        {
            throw new InstallConfigurationException(
                "The install configuration file could not be read.",
                configPath,
                ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InstallConfigurationException(
                "The install configuration file could not be read.",
                configPath,
                ex);
        }
    }

    public string ResolveStorageRoot(string baseDirectory, string? configPath = null)
    {
        try
        {
            return StorageMode switch
            {
                StorageMode.ExeDirectory => Path.GetFullPath(baseDirectory),
                StorageMode.Absolute => Path.GetFullPath(StorageRootPath!),
                _ => throw new InstallConfigurationException(
                    $"Unsupported storage mode '{StorageMode}'.",
                    configPath)
            };
        }
        catch (InstallConfigurationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InstallConfigurationException(
                "The install configuration file did not specify a valid storageRootPath.",
                configPath,
                ex);
        }
    }

    internal static AppStorageConfiguration CreateDebugFallback(string storageRootPath) => new()
    {
        InstallMode = AppInstallMode.Msi,
        StorageMode = StorageMode.Absolute,
        StorageRootPath = storageRootPath
    };

    private void Validate(string configPath)
    {
        switch (InstallMode)
        {
            case AppInstallMode.Portable when StorageMode != StorageMode.ExeDirectory:
                throw new InstallConfigurationException(
                    "Portable installs must use storageMode 'ExeDirectory'.",
                    configPath);

            case AppInstallMode.Portable:
                return;

            case AppInstallMode.Msi when StorageMode != StorageMode.Absolute:
                throw new InstallConfigurationException(
                    "MSI installs must use storageMode 'Absolute'.",
                    configPath);

            case AppInstallMode.Msi when string.IsNullOrWhiteSpace(StorageRootPath):
                throw new InstallConfigurationException(
                    "MSI installs must specify a non-empty storageRootPath.",
                    configPath);

            case AppInstallMode.Msi:
                return;

            default:
                throw new InstallConfigurationException(
                    $"Unsupported install mode '{InstallMode}'.",
                    configPath);
        }
    }
}
