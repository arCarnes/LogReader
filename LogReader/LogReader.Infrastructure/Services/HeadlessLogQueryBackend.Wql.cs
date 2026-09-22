namespace LogReader.Infrastructure.Services;

using System.Collections.Immutable;
using LogReader.Core;
using LogReader.Core.Models;

public sealed partial class HeadlessLogQueryBackend
{
    public async Task<LogOperationEnvelope<FieldProfilesResult>> ListFieldProfilesAsync(int startIndex = 0, CancellationToken ct = default)
    {
        using var lease = BeginRequest();
        var requestId = CreateRequestId();
        using var scope = CreateDeadlineScope(null, ct);
        if (startIndex < 0) return Rejected<FieldProfilesResult>(requestId, [Error("invalid_start_index", "startIndex cannot be negative.")]);
        try
        {
            var read = await _catalogReader.ReadAsync(scope.Token).ConfigureAwait(false);
            if (!read.IsSuccess) return Failure<FieldProfilesResult>(requestId, read.Error!);
            var profiles = read.Snapshot!.FieldProfiles;
            if (profiles.Select(profile => profile.Id).Distinct(StringComparer.Ordinal).Count() != profiles.Length)
                return Rejected<FieldProfilesResult>(requestId, [Error("invalid_field_profile", "Saved field profile IDs must be unique.")]);
            var output = ImmutableArray.CreateBuilder<FieldProfileSchema>();
            var budget = new ResponseCharacterBudget(_limits.MaximumResponseCharacters);
            var index = startIndex;
            for (; index < profiles.Length && output.Count < 50; index++)
            {
                scope.Token.ThrowIfCancellationRequested();
                var profile = profiles[index];
                var extractor = StructuredFieldExtractor.Compile(profile);
                var cost = profile.Id.Length + profile.Name.Length + 64 + profile.Fields.Sum(field => field.Name.Length + 16);
                if (cost > budget.Remaining) break;
                budget.Consume(cost);
                output.Add(new(profile.Id, LogContentSanitizer.Normalize(profile.Name), extractor.Revision,
                    profile.Fields.Select(field => new FieldSchema(field.Name, field.Type)).ToImmutableArray()));
            }
            int? next = index < profiles.Length ? index : null;
            if (next == startIndex)
                return Rejected<FieldProfilesResult>(requestId, [Error("profile_response_limit", "The next profile schema does not fit the response budget.")]);
            return Envelope(requestId, read.Snapshot.Revision, false, next.HasValue,
                next.HasValue ? ["profile_page_limit"] : [], [],
                new FieldProfilesResult(output.ToImmutable(), [new("raw", StructuredFieldType.Text), new("line_number", StructuredFieldType.Number)], next));
        }
        catch (OperationCanceledException) { return Cancelled<FieldProfilesResult>(requestId, ct); }
        catch (ArgumentException) { return Rejected<FieldProfilesResult>(requestId, [Error("invalid_field_profile", "A saved field profile is invalid. Repair it in Settings.")]); }
    }

    public async Task<LogOperationEnvelope<LogWqlResult>> QueryLogsAsync(LogWqlQuery request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await SearchLogsCoreAsync(new LogSearchQuery
        {
            Targets = request.Targets, Query = request.Query, CaseSensitive = request.CaseSensitive,
            Cursor = request.Cursor, DateOffsetDays = request.DateOffsetDays,
            StartTimestamp = request.StartTimestamp, EndTimestamp = request.EndTimestamp,
            MaxFiles = request.MaxFiles, MaxHitsPerFile = request.MaxHitsPerFile, MaxTotalHits = request.MaxTotalHits,
            IncludeContextBefore = request.IncludeContextBefore, IncludeContextAfter = request.IncludeContextAfter,
            TimeoutMilliseconds = request.TimeoutMilliseconds
        }, isWql: true, request.ProfileId, ct).ConfigureAwait(false);
        var result = response.Result;
        var mapped = result == null ? null : new LogWqlResult(
            result.WqlPlan!.Extractor.ProfileId, result.WqlPlan.Extractor.Revision,
            result.Files.Select(file => MapWqlFile(file, request.IncludeContextBefore, request.IncludeContextAfter)).ToImmutableArray(),
            result.NextCursor, result.IsPageComplete, result.IsQueryComplete, result.IncompleteReasons,
            result.EffectiveLimits with { MaximumQueryCharacters = StructuredFieldExtractor.MaximumTextLength });
        return new(response.SchemaVersion, response.RequestId, response.CatalogRevision, response.IsPartial,
            response.IsTruncated, response.TruncationReasons, response.Errors, mapped);
    }

    private static LogWqlFileResult MapWqlFile(LogSearchFileResult file, int contextBefore, int contextAfter)
    {
        var excerptLines = file.Excerpts
            .SelectMany(static excerpt => excerpt.Lines)
            .ToDictionary(static line => line.LineNumber);
        var hits = file.Hits.Select(hit =>
        {
            var hitLine = excerptLines[hit.LineNumber];
            var before = MapWqlContext(excerptLines, hit.LineNumber, contextBefore, before: true);
            var after = MapWqlContext(excerptLines, hit.LineNumber, contextAfter, before: false);
            return new LogWqlHit(
                hit.LineNumber,
                hitLine.Text,
                hitLine.IsTruncated,
                hit.WqlFieldsTruncated,
                hit.WqlFields ?? ImmutableDictionary<string, StructuredFieldValue>.Empty,
                before,
                after);
        }).ToImmutableArray();

        return new LogWqlFileResult(
            file.FileId,
            file.DisplayName,
            file.Generation,
            hits,
            file.WqlParsing,
            file.Error,
            file.IsTruncated || hits.Any(static hit => hit.IsTextTruncated || hit.AreFieldsTruncated),
            file.IncompleteReasons);
    }

    private static ImmutableArray<LogLineResult> MapWqlContext(
        IReadOnlyDictionary<long, LogSearchExcerptLine> lines,
        long hitLineNumber,
        int contextCount,
        bool before)
    {
        if (contextCount <= 0)
            return [];

        var startLine = before ? Math.Max(1, hitLineNumber - contextCount) : hitLineNumber + 1;
        var endLine = before ? hitLineNumber - 1 : hitLineNumber + contextCount;
        return lines.Values
            .Where(line => line.LineNumber >= startLine && line.LineNumber <= endLine)
            .Where(static line => line.LineNumber <= int.MaxValue)
            .OrderBy(static line => line.LineNumber)
            .Select(static line => new LogLineResult((int)line.LineNumber, line.Text, line.IsTruncated))
            .ToImmutableArray();
    }

    private static ImmutableDictionary<string, StructuredFieldValue>? MapWqlFields(SearchHit hit, ResponseCharacterBudget budget)
    {
        if (hit.Fields == null) return null;
        var values = ImmutableDictionary.CreateBuilder<string, StructuredFieldValue>(StringComparer.OrdinalIgnoreCase);
        var metadataCost = hit.Fields.Sum(field => field.Key.Length + 64);
        foreach (var field in StructuredFieldOutput.Retain(hit.Fields, Math.Max(0, budget.Remaining - metadataCost), LogContentSanitizer.Normalize))
        {
            // Values, field names and scalar/status metadata all consume the shared response budget.
            var cost = field.Key.Length + 32 + (field.Value.Text?.Length ?? 32);
            if (cost > budget.Remaining)
            {
                budget.Consume(budget.Remaining);
                break;
            }
            budget.Consume(cost);
            values.Add(field.Key, field.Value);
        }
        return values.ToImmutable();
    }

    private static LogSearchHit AttachWqlFields(LogSearchHit mapped, SearchHit raw, ResponseCharacterBudget budget)
    {
        var fields = MapWqlFields(raw, budget);
        return mapped with
        {
            WqlFields = fields,
            WqlFieldsTruncated = fields != null && (fields.Count != raw.Fields!.Count || fields.Values.Any(value => value.IsTruncated))
        };
    }

    private static LogWqlParsingStatistics? MapWqlParsing(SearchResult raw, WqlQueryPlan? plan, ResponseCharacterBudget budget)
    {
        if (plan == null) return null;
        var fields = ImmutableDictionary.CreateBuilder<string, StructuredFieldStatistics>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in plan.Extractor.Fields.Keys.OrderBy(name => name))
        {
            if (name.Length + 64 > budget.Remaining) break;
            budget.Consume(name.Length + 64);
            var statistics = raw.FieldStatistics?.GetValueOrDefault(name);
            fields.Add(name, new()
            {
                ValueCount = statistics?.ValueCount ?? 0,
                MissingCount = statistics?.MissingCount ?? 0,
                InvalidCount = statistics?.InvalidCount ?? 0
            });
        }
        return new(raw.WqlEvaluatedLineCount,
            raw.IsEvaluationComplete && raw.Error == null && !raw.WasCancelled && !raw.FileChangedDuringOrAfterScan &&
            raw.GenerationEvidence.Correlation == FileGenerationCorrelation.Current,
            fields.ToImmutable(), fields.Count != plan.Extractor.Fields.Count);
    }
}
