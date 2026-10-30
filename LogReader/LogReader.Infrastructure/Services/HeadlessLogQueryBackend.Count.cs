namespace LogReader.Infrastructure.Services;

using System.Collections.Immutable;
using LogReader.Core;
using LogReader.Core.Models;

public sealed partial class HeadlessLogQueryBackend
{
    public async Task<LogOperationEnvelope<LogCountResult>> CountLogsAsync(
        LogCountQuery request,
        CancellationToken ct = default)
    {
        using var requestLease = BeginRequest();
        ArgumentNullException.ThrowIfNull(request);
        var requestId = CreateRequestId();
        var validation = ValidateCountRequest(request);
        if (!validation.IsEmpty)
            return Rejected<LogCountResult>(requestId, validation);

        return await RunResumableAsync<LogCountResult>(new LogSearchQuery
        {
            Targets = request.Targets, Query = request.Query, UseRegex = request.UseRegex,
            CaseSensitive = request.CaseSensitive, DateOffsetDays = request.DateOffsetDays,
            ResultMode = "countsOnly", TimeoutMilliseconds = request.TimeoutMilliseconds
        }, request, _limits.MaximumFiles, 0, 0, ct).ConfigureAwait(false);
    }

    private static ImmutableArray<LogCountBucket> BuildCountBuckets(
        CountTimeResolution time,
        IReadOnlyDictionary<int, SearchTimestampBucketCount> bucketCounts,
        ResponseCharacterBudget contentBudget)
    {
        if (time.AggregationPlan == null)
            return [];

        var buckets = ImmutableArray.CreateBuilder<LogCountBucket>();
        foreach (var definition in time.AggregationPlan.Buckets)
        {
            var kind = time.AggregationPlan.Kind == SearchTimestampBucketKind.Dated
                ? "dated"
                : "timeOfDay";
            contentBudget.Consume(definition.Start.Length + definition.EndExclusive.Length + kind.Length);
            bucketCounts.TryGetValue(definition.Index, out var count);
            buckets.Add(new LogCountBucket(
                kind,
                definition.Start,
                definition.EndExclusive,
                count?.MatchingLineCount ?? 0,
                count?.MatchOccurrenceCount ?? 0));
        }

        return buckets.ToImmutable();
    }

    private ImmutableArray<ConfiguredLogRequestError> ValidateCountRequest(LogCountQuery request)
    {
        var errors = ImmutableArray.CreateBuilder<ConfiguredLogRequestError>();
        if (request.Targets == null || request.Targets.Count == 0)
            errors.Add(Error("targets_required", "At least one configured target is required."));
        else if (request.Targets.Count > _limits.MaximumTargets)
            errors.Add(Error("target_limit_exceeded", $"No more than {_limits.MaximumTargets} targets may be requested."));
        if (string.IsNullOrEmpty(request.Query))
            errors.Add(Error("query_required", "A non-empty count query is required."));
        else if (request.Query.Length > _limits.MaximumQueryCharacters)
            errors.Add(Error("query_too_long", $"The query cannot exceed {_limits.MaximumQueryCharacters} characters."));
        if (request.StartTimestamp is { Length: > ConfiguredLogLimits.DefaultMaxTimestampCharacters } ||
            request.EndTimestamp is { Length: > ConfiguredLogLimits.DefaultMaxTimestampCharacters } ||
            request.RelativeWindow is { Length: > ConfiguredLogLimits.DefaultMaxTimestampCharacters })
        {
            errors.Add(Error(
                "timestamp_too_long",
                $"Timestamp and relative-window values cannot exceed {ConfiguredLogLimits.DefaultMaxTimestampCharacters} characters."));
        }
        if (request.DateOffsetDays < 0)
            errors.Add(Error("invalid_date_offset", "dateOffsetDays cannot be negative."));
        if (request.UseRegex && !RegexPatternFactory.TryCreate(request.Query, request.CaseSensitive, out _))
            errors.Add(Error("invalid_regex", "The regular expression is invalid."));
        ValidateTimeout(request.TimeoutMilliseconds, errors);
        return errors.ToImmutable();
    }

}
