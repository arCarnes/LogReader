namespace LogReader.Infrastructure.Services;

internal sealed class FilteredLogLineTooLargeException : IOException
{
    public FilteredLogLineTooLargeException()
        : base("A physical log line exceeds the filtered-tail size limit.")
    {
    }
}
