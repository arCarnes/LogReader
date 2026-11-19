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
    public const int CurrentSchemaVersion = 3;

    internal LogFileOutcomes? FileOutcomes { get; init; }
}

internal sealed record LogFileOutcomes(
    bool WhollyUnevaluable,
    ImmutableArray<ConfiguredLogRequestError> Issues,
    int OmittedIssueCount,
    int RetainedCharacters)
{
    public long RetainedBytes => 64 + 64L * Issues.Length + 2L * RetainedCharacters;

    internal static LogFileOutcomes Capture(
        IEnumerable<ConfiguredLogRequestError> issues, bool whollyUnevaluable, int responseCharacters)
    {
        const int maximumIssues = 64;
        var characterBudget = Math.Min(16 * 1024, responseCharacters);
        var retained = ImmutableArray.CreateBuilder<ConfiguredLogRequestError>();
        var characters = 0;
        var omitted = 0;
        foreach (var issue in issues)
        {
            var size = issue.Code.Length + issue.Message.Length + (issue.TargetId?.Length ?? 0) +
                       (issue.Reason?.Length ?? 0);
            if (retained.Count == maximumIssues || size > characterBudget - characters)
            {
                omitted++;
                continue;
            }

            retained.Add(issue);
            characters += size;
        }

        return new(whollyUnevaluable, retained.ToImmutable(), omitted, characters);
    }
}
