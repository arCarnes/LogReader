namespace LogReader.Core;

using System.Text.Json;

internal sealed class MsiUserStorageSelection
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public string? StorageRootPath { get; init; }

    public static string LoadStorageRoot(string selectionPath, string suggestedStorageRootPath)
    {
        if (!File.Exists(selectionPath))
        {
            throw CreateSetupRequiredException(
                "LogReader needs a storage location before it can finish starting.",
                selectionPath,
                suggestedStorageRootPath);
        }

        try
        {
            var json = File.ReadAllText(selectionPath);
            var selection = JsonSerializer.Deserialize<MsiUserStorageSelection>(json, SerializerOptions);
            if (selection == null || string.IsNullOrWhiteSpace(selection.StorageRootPath))
            {
                throw CreateSetupRequiredException(
                    "The MSI storage selection file did not contain a valid storageRootPath.",
                    selectionPath,
                    suggestedStorageRootPath);
            }

            return Path.GetFullPath(selection.StorageRootPath);
        }
        catch (StorageSetupRequiredException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw CreateSetupRequiredException(
                "The MSI storage selection file is not valid JSON.",
                selectionPath,
                suggestedStorageRootPath,
                ex);
        }
        catch (IOException ex)
        {
            throw CreateSetupRequiredException(
                "The MSI storage selection file could not be read.",
                selectionPath,
                suggestedStorageRootPath,
                ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw CreateSetupRequiredException(
                "The MSI storage selection file could not be read.",
                selectionPath,
                suggestedStorageRootPath,
                ex);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw CreateSetupRequiredException(
                "The MSI storage selection file did not specify a valid storageRootPath.",
                selectionPath,
                suggestedStorageRootPath,
                ex);
        }
    }

    public static void Save(string selectionPath, string storageRootPath)
    {
        var normalizedStorageRoot = Path.GetFullPath(storageRootPath);
        var selectionDirectory = Path.GetDirectoryName(selectionPath);
        if (!string.IsNullOrWhiteSpace(selectionDirectory))
            Directory.CreateDirectory(selectionDirectory);

        var selection = new MsiUserStorageSelection
        {
            StorageRootPath = normalizedStorageRoot
        };

        var json = JsonSerializer.Serialize(selection, SerializerOptions);
        File.WriteAllText(selectionPath, json);
    }

    private static StorageSetupRequiredException CreateSetupRequiredException(
        string message,
        string selectionPath,
        string suggestedStorageRootPath,
        Exception? innerException = null)
        => new(
            message,
            selectionPath,
            suggestedStorageRootPath,
            innerException);
}
