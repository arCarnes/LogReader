namespace LogReader.Core.Models;

using System.Collections.Immutable;

public sealed record LogOperationEnvelope<T>(
    int SchemaVersion,
    string RequestId,
    string CatalogRevision,
    bool IsPartial,
    bool IsTruncated,
    ImmutableArray<string> TruncationReasons,
    ImmutableArray<ConfiguredLogRequestError> Errors,
    T? Result)
{
    public const int CurrentSchemaVersion = 1;
}
