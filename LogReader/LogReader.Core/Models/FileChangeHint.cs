namespace LogReader.Core.Models;

public enum FileChangeHint
{
    None,
    UnspecifiedReplacement,
    IdentityChanged,
    Truncated,
    RecreatedAfterMissing
}
