namespace LogReader.Core;

using System.Text.Json;
using System.Text.Json.Serialization;

internal enum AppInstallMode
{
    Portable,
    Msi,
    Dev
}

internal enum StorageMode
{
    ExeDirectory,
    Absolute,
    PerUserChoice
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

    public string ResolveStorageRoot(
        string baseDirectory,
        string? configPath = null,
        string? msiUserSelectionPath = null,
        string? suggestedStorageRootPath = null)
    {
        try
        {
            return StorageMode switch
            {
                StorageMode.ExeDirectory => Path.GetFullPath(baseDirectory),
                StorageMode.Absolute => Path.GetFullPath(StorageRootPath!),
                StorageMode.PerUserChoice => ResolvePerUserChoiceStorageRoot(
                    msiUserSelectionPath,
                    suggestedStorageRootPath),
                _ => throw new InstallConfigurationException(
                    $"Unsupported storage mode '{StorageMode}'.",
                    configPath)
            };
        }
        catch (StorageSetupRequiredException)
        {
            throw;
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
        InstallMode = AppInstallMode.Dev,
        StorageMode = StorageMode.Absolute,
        StorageRootPath = storageRootPath
    };

    private void Validate(string configPath)
    {
        if (InstallMode == AppInstallMode.Portable)
        {
            if (StorageMode != StorageMode.ExeDirectory)
            {
                throw new InstallConfigurationException(
                    "Portable installs must use storageMode 'ExeDirectory'.",
                    configPath);
            }

            return;
        }

        if (InstallMode == AppInstallMode.Msi)
        {
            if (StorageMode == StorageMode.Absolute)
            {
                if (string.IsNullOrWhiteSpace(StorageRootPath))
                {
                    throw new InstallConfigurationException(
                        "MSI installs must specify a non-empty storageRootPath.",
                        configPath);
                }

                return;
            }

            if (StorageMode == StorageMode.PerUserChoice)
                return;

            throw new InstallConfigurationException(
                "MSI installs must use storageMode 'Absolute' or 'PerUserChoice'.",
                configPath);
        }

        if (InstallMode == AppInstallMode.Dev)
        {
#if DEBUG
            if (StorageMode == StorageMode.Absolute)
            {
                if (string.IsNullOrWhiteSpace(StorageRootPath))
                {
                    throw new InstallConfigurationException(
                        "Dev installs must specify a non-empty storageRootPath.",
                        configPath);
                }

                return;
            }

            throw new InstallConfigurationException(
                "Dev installs must use storageMode 'Absolute'.",
                configPath);
#else
            throw new InstallConfigurationException(
                "Dev install mode is only supported by Debug builds.",
                configPath);
#endif
        }

        throw new InstallConfigurationException(
            $"Unsupported install mode '{InstallMode}'.",
            configPath);
    }

    private static string ResolvePerUserChoiceStorageRoot(
        string? msiUserSelectionPath,
        string? suggestedStorageRootPath)
    {
        if (string.IsNullOrWhiteSpace(msiUserSelectionPath) || string.IsNullOrWhiteSpace(suggestedStorageRootPath))
            throw new InvalidOperationException("The MSI user storage selection paths were not provided.");

        return MsiUserStorageSelection.LoadStorageRoot(msiUserSelectionPath, suggestedStorageRootPath);
    }
}
