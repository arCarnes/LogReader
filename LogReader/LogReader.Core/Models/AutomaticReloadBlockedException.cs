namespace LogReader.Core.Models;

internal sealed class AutomaticReloadBlockedException : IOException
{
    public AutomaticReloadBlockedException(
        string message,
        TimeSpan? retryAfter = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        RetryAfter = retryAfter;
    }

    public TimeSpan? RetryAfter { get; }
}
