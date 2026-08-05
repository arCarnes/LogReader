namespace LogReader.Core.Models;

using System.Collections.Immutable;

public enum LogOperationBackendKind
{
    Headless,
    LiveUi
}

public sealed record LogOperationEnvelope<T>(
    int SchemaVersion,
    string RequestId,
    LogOperationBackendKind Backend,
    string CatalogRevision,
    bool IsPartial,
    bool IsTruncated,
    ImmutableArray<string> TruncationReasons,
    ImmutableArray<ConfiguredLogRequestError> Errors,
    T? Result)
{
    public const int CurrentSchemaVersion = 1;
}
