namespace LogReader.Core;

public sealed class InstallConfigurationException : InvalidOperationException
{
    public InstallConfigurationException(string message, string? configurationPath = null, Exception? innerException = null)
        : base(message, innerException)
    {
        ConfigurationPath = configurationPath;
    }

    public string? ConfigurationPath { get; }
}

public sealed class StorageSetupRequiredException : InvalidOperationException
{
    public StorageSetupRequiredException(
        string message,
        string selectionFilePath,
        string suggestedStorageRootPath,
        Exception? innerException = null)
        : base(message, innerException)
    {
        SelectionFilePath = selectionFilePath;
        SuggestedStorageRootPath = suggestedStorageRootPath;
    }

    public string SelectionFilePath { get; }

    public string SuggestedStorageRootPath { get; }
}

public sealed class ProtectedStorageLocationException : UnauthorizedAccessException
{
    public ProtectedStorageLocationException(string storagePath)
        : base($"The storage location is protected and cannot be used by LogReader:{Environment.NewLine}{storagePath}")
    {
        StoragePath = storagePath;
    }

    public string StoragePath { get; }
}

public sealed class StorageValidationException : IOException
{
    public StorageValidationException(string storagePath, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        StoragePath = storagePath;
    }

    public string StoragePath { get; }
}
