namespace LogReader.Core;

public sealed class PersistedStateRecoveryException : IOException
{
    public PersistedStateRecoveryException(
        string storeDisplayName,
        string storePath,
        string failureReason,
        Exception? innerException = null)
        : base(
            $"The saved {storeDisplayName} data is invalid and needs recovery:{Environment.NewLine}{failureReason}",
            innerException)
    {
        StoreDisplayName = storeDisplayName;
        StorePath = storePath;
        FailureReason = failureReason;
    }

    public string StoreDisplayName { get; }

    public string StorePath { get; }

    public string FailureReason { get; }
}

public sealed record PersistedStateRecoveryResult(
    string StoreDisplayName,
    string StorePath,
    string BackupPath,
    string NotePath,
    string FailureReason);
