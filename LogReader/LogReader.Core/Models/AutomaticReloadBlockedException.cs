namespace LogReader.Core.Models;

internal enum AutomaticReloadReason
{
    Cooldown,
    MetadataUnavailable,
    MetadataInconsistent,
    ReplacementChanged,
    ReloadFailed
}

internal sealed class AutomaticReloadBlockedException : IOException
{
    public AutomaticReloadBlockedException(
        string message,
        TimeSpan? retryAfter = null,
        Exception? innerException = null,
        AutomaticReloadReason reason = AutomaticReloadReason.MetadataInconsistent,
        bool isRetryable = true)
        : base(message, innerException)
    {
        RetryAfter = retryAfter;
        Reason = reason;
        IsRetryable = isRetryable;
    }

    public TimeSpan? RetryAfter { get; }
    public AutomaticReloadReason Reason { get; }
    public bool IsRetryable { get; }
}
