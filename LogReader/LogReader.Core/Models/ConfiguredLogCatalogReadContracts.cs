namespace LogReader.Core.Models;

public static class ConfiguredLogCatalogReadErrorCodes
{
    public const string StorageNotConfigured = "storage_not_configured";
    public const string InstallConfigurationInvalid = "install_configuration_invalid";
    public const string StorageAccessDenied = "storage_access_denied";
    public const string StorageUnavailable = "storage_unavailable";
    public const string CatalogUnstable = "catalog_unstable";
    public const string CatalogStoreMissing = "catalog_store_missing";
    public const string MigrationRequired = "migration_required";
    public const string RecoveryRequired = "recovery_required";
    public const string UnsupportedSchema = "unsupported_schema";
    public const string CatalogTooLarge = "catalog_too_large";
    public const string ReadCancelled = "read_cancelled";
}

public sealed record ConfiguredLogCatalogReadError(
    string Code,
    string Message,
    bool IsRetryable);

public sealed class ConfiguredLogCatalogReadResult
{
    private ConfiguredLogCatalogReadResult(
        ConfiguredLogCatalogSnapshot? snapshot,
        ConfiguredLogCatalogReadError? error,
        bool isCacheHit)
    {
        Snapshot = snapshot;
        Error = error;
        IsCacheHit = isCacheHit;
    }

    public ConfiguredLogCatalogSnapshot? Snapshot { get; }

    public ConfiguredLogCatalogReadError? Error { get; }

    public bool IsCacheHit { get; }

    public bool IsSuccess => Snapshot != null && Error == null;

    public static ConfiguredLogCatalogReadResult Success(
        ConfiguredLogCatalogSnapshot snapshot,
        bool isCacheHit = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new ConfiguredLogCatalogReadResult(snapshot, null, isCacheHit);
    }

    public static ConfiguredLogCatalogReadResult Failure(
        string code,
        string message,
        bool isRetryable = false)
        => new(
            null,
            new ConfiguredLogCatalogReadError(code, message, isRetryable),
            isCacheHit: false);
}
