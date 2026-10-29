namespace LogReader.Mcp;

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using LogReader.Core.Models;
using Microsoft.Extensions.AI;

internal static class McpResponseJsonPolicy
{
    internal const string SnapshotSearchDescription =
        "Bounded snapshot: each file's extent is frozen when first opened; appended data is excluded. Once nextCursor is absent, report observed results with any incompleteReasons as caveats. Do not restart or rescan changed files unless the user requests a fresh scan. Use read_log_tail when the user requests ongoing monitoring.";
    internal const string SearchCursorDescription =
        "Resume nextCursor with all query arguments unchanged, including resultMode, context and hit/file limits. Only provenanceMode, includeStatistics and timeoutMilliseconds may change. Process-local; idle expiry defaults to 15 minutes (server_status). On mismatch restore original arguments; on stale catalog rediscover and restart; on expiry/restart start a new query.";
    internal const string CountCursorDescription =
        "Resume nextCursor with all query arguments unchanged, including bucketSize; replace cumulative totals/buckets/files. Only bucketMode, provenanceMode, includeStatistics and timeoutMilliseconds may change. Process-local; idle expiry defaults to 15 minutes (server_status). On mismatch restore original arguments; on stale catalog rediscover and restart; on expiry/restart start a new query.";
    public static void Apply(JsonTypeInfo typeInfo, bool includeStatistics)
    {
        if (IsEnvelope(typeInfo.Type))
        {
            foreach (var property in typeInfo.Properties)
            {
                if (property.Name == "schemaVersion")
                    property.Get = static _ => McpResponseProjector.SchemaVersion;
                if (property.Name == "result")
                    property.IsRequired = false;
            }
        }
        if (IsCompactEnvelope(typeInfo.Type))
        {
            foreach (var property in typeInfo.Properties)
            {
                if (property.Name is "errors" or "truncationReasons")
                {
                    property.IsRequired = false;
                    property.ShouldSerialize = static (_, value) => value is ImmutableArray<ConfiguredLogRequestError> errors
                        ? !errors.IsDefaultOrEmpty
                        : value is ImmutableArray<string> reasons && !reasons.IsDefaultOrEmpty;
                }
            }
        }

        if (typeInfo.Type == typeof(LogSearchResult) || typeInfo.Type == typeof(LogCountResult))
        {
            for (var index = typeInfo.Properties.Count - 1; index >= 0; index--)
            {
                if (typeInfo.Properties[index].Name == "effectiveLimits" ||
                    (!includeStatistics && typeInfo.Properties[index].Name == "statistics"))
                    typeInfo.Properties.RemoveAt(index);
            }
        }

        if (HasOptionalMetadata(typeInfo.Type))
        {
            foreach (var property in typeInfo.Properties)
            {
                if (IsOptionalMetadata(typeInfo.Type, property.Name))
                {
                    property.IsRequired = false;
                    property.ShouldSerialize = property.Name switch
                    {
                        "file" => static (instance, value) =>
                            instance is not LogReadTailResult tail || !tail.CompactFile && value is not null,
                        "nextCursor" => static (instance, value) =>
                            instance is not LogReadTailResult tail || !tail.IsIdle && value is not null,
                        "totalLineCount" =>
                            static (instance, _) => instance is not LogReadTailResult tail || !tail.IsIdle,
                        "examinedLineCount" or "skippedLineCount" or "remainingLineCount" =>
                            static (instance, value) => instance is not LogReadTailResult tail || !tail.IsIdle && value is not null,
                        "provenanceTotalCount" => static (instance, _) => instance switch
                        {
                            LogSearchFileResult file => file.IsProvenanceTruncated,
                            LogCountFileResult file => file.IsProvenanceTruncated,
                            LogReadFileResult file => file.IsProvenanceTruncated,
                            _ => false
                        },
                        "evaluatedThroughLine" => static (instance, value) =>
                            instance is LogSearchFileResult file && !file.IsCountExact && value is not null,
                        "isTruncated" => static (_, value) => value is true,
                        "generationChanged" or "lastLineUpdated" or "isProvenanceTruncated" =>
                            static (_, value) => value is true,
                        _ => static (_, value) => value switch
                        {
                            null => false,
                            ImmutableArray<string> reasons => !reasons.IsDefaultOrEmpty,
                            ImmutableArray<LogLineResult> lines => !lines.IsDefaultOrEmpty,
                            ImmutableArray<LogSearchHit> hits => !hits.IsDefaultOrEmpty,
                            ImmutableArray<LogSearchExcerpt> excerpts => !excerpts.IsDefaultOrEmpty,
                            _ => true
                        }
                    };
                }
            }
        }
    }

    public static JsonNode TransformSchema(AIJsonSchemaCreateContext context, JsonNode schema)
    {
        if (schema is JsonObject inputObject && inputObject["properties"] is JsonObject inputProperties)
        {
            ConstrainStringChoices(inputProperties);
            // Positional record parameters are inferred as required even when the
            // serializer omits their null values (e.g. tree continuation, generation).
            if (context.TypeInfo.Options.DefaultIgnoreCondition == JsonIgnoreCondition.WhenWritingNull &&
                inputObject["required"] is JsonArray nullableRequired)
            {
                for (var index = nullableRequired.Count - 1; index >= 0; index--)
                    if (AllowsNull(inputProperties[nullableRequired[index]!.GetValue<string>()]))
                        nullableRequired.RemoveAt(index);
            }
        }
        if (IsEnvelope(context.TypeInfo.Type) && schema is JsonObject wireSchema)
        {
            if (wireSchema["properties"]?["schemaVersion"] is JsonObject version)
                version["const"] = McpResponseProjector.SchemaVersion;
            if (wireSchema["required"] is JsonArray wireRequired)
            {
                for (var index = wireRequired.Count - 1; index >= 0; index--)
                    if (wireRequired[index]?.GetValue<string>() == "result")
                        wireRequired.RemoveAt(index);
            }
        }
        if (context.TypeInfo.Type == typeof(LogCountResult) && schema is JsonObject countSchema)
            McpResponseProjector.TransformCountSchema(countSchema);
        if (context.TypeInfo.Type == typeof(LogSearchFileResult) || context.TypeInfo.Type == typeof(LogCountFileResult) ||
            context.TypeInfo.Type == typeof(LogReadFileResult))
        {
            if (schema is JsonObject fileSchema)
                McpResponseProjector.TransformProvenanceFileSchema(fileSchema);
        }
        if (context.TypeInfo.Type == typeof(LogSearchResult) || context.TypeInfo.Type == typeof(LogCountResult) ||
            context.TypeInfo.Type == typeof(LogReadLinesResult) || context.TypeInfo.Type == typeof(LogReadTailResult))
        {
            if (schema["properties"] is JsonObject resultProperties)
                resultProperties["provenanceTable"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Distinct retained routes, indexed in first-occurrence file order. Shared mode only; absent when no file record is visible. References never depend on earlier responses.",
                    ["items"] = new JsonObject { ["$ref"] = "#/$defs/provenance" }
                };
            if (schema is JsonObject resultSchema)
                McpResponseProjector.TransformProvenanceResultSchema(resultSchema,
                    context.TypeInfo.Type == typeof(LogSearchResult) || context.TypeInfo.Type == typeof(LogCountResult));
        }
        if (IsCompactEnvelope(context.TypeInfo.Type) && schema is JsonObject envelopeSchema)
        {
            if (envelopeSchema["required"] is JsonArray envelopeRequired)
            {
                for (var index = envelopeRequired.Count - 1; index >= 0; index--)
                {
                    if (envelopeRequired[index]?.GetValue<string>() is "errors" or "truncationReasons")
                        envelopeRequired.RemoveAt(index);
                }
            }
            if (envelopeSchema["properties"] is JsonObject envelopeProperties)
            {
                if (envelopeProperties["errors"] is JsonObject errors)
                    errors["description"] = "Omitted when there are no request errors.";
                if (envelopeProperties["truncationReasons"] is JsonObject reasons)
                    reasons["description"] = "Omitted when there are no truncation reasons.";
            }
        }

        if (context.TypeInfo.Type == typeof(ConfiguredLogRequestError) && schema["properties"]?["reason"] is JsonObject reasonSchema)
            reasonSchema["description"] = "Capacity failure reason: session_limit_exceeded, memory_budget_exceeded, or query_too_large. Omitted for other errors.";

        if (HasOptionalMetadata(context.TypeInfo.Type) && schema is JsonObject objectSchema)
        {
            // The SDK infers required properties from record constructor parameters
            // even when ShouldSerialize permits omission. Keep the wire schema aligned.
            if (objectSchema["required"] is JsonArray required)
            {
                for (var index = required.Count - 1; index >= 0; index--)
                {
                    if (IsOptionalMetadata(context.TypeInfo.Type, required[index]!.GetValue<string>()))
                        required.RemoveAt(index);
                }
            }
            if (objectSchema["properties"] is JsonObject properties)
            {
                if (context.TypeInfo.Type == typeof(LogSearchResult) || context.TypeInfo.Type == typeof(LogCountResult))
                {
                    if (properties["isTraversalComplete"] is JsonObject traversal)
                        traversal["description"] = context.TypeInfo.Type == typeof(LogSearchResult)
                            ? "All candidates were visited or terminated with explicit errors. Counts may be inexact. A missing nextCursor can also mean a terminal query hit cap; check isQueryComplete and incompleteReasons."
                            : "All candidates were visited or terminated with explicit errors. Counts may be inexact; check isComplete and incompleteReasons even without nextCursor.";
                    if (properties["stopReason"] is JsonObject stop)
                    {
                        stop["description"] = context.TypeInfo.Type == typeof(LogSearchResult)
                            ? "Reason this response stopped. Work/page output limits yield continuations. The query hit cap terminates text search with query_hit_limit, no nextCursor and incomplete traversal. countsOnly is unaffected."
                            : "Reason this count call stopped. Work limits yield continuations; scope_exhausted ends traversal. Numeric exactness is determined by isComplete and incompleteReasons.";
                        stop["enum"] = new JsonArray("time_slice", "scan_budget", "hit_limit", "response_limit", "scope_exhausted");
                    }
                    if (properties["nextCursor"] is JsonObject next)
                        next["description"] = context.TypeInfo.Type == typeof(LogSearchResult)
                            ? SearchCursorDescription : CountCursorDescription;
                }
                if (context.TypeInfo.Type == typeof(LogReadTailResult) && properties["isIdle"] is JsonObject idleSchema)
                    idleSchema["description"] = "True only for a cursor poll with no physical-line change or update event. Reuse the submitted cursor when nextCursor is omitted.";
                foreach (var property in properties)
                {
                    if (IsOptionalMetadata(context.TypeInfo.Type, property.Key) && property.Value is JsonObject propertySchema)
                    {
                        propertySchema["description"] = property.Key switch
                        {
                            "error" => "Omitted when there is no file error.",
                            "encoding" => "Omitted for countsOnly search results or when unavailable.",
                            "hits" => "One record per retained matching line, locating its first match in excerpts; occurrences may exceed hits. Omitted when empty. Append new hits, avoid duplicates on cursor replay.",
                            "excerpts" => "Omitted when no search text is returned.",
                            "isTruncated" => "Included only when this file or excerpt line is truncated.",
                            "isProvenanceTruncated" => "Included only when provenance is truncated.",
                            "evaluatedThroughLine" => "Included only when file evaluation is incomplete and a boundary is available.",
                            "provenanceTotalCount" => "Included only when provenance is truncated.",
                            "examinedLineCount" => "Included for filtered tail reads; physical lines examined in this call.",
                            "skippedLineCount" => "Included for filtered tail reads; examined lines that did not match.",
                            "remainingLineCount" => "Filtered tail: physical lines remaining after the cursor in this snapshot. If positive, continue with nextCursor to drain the backlog.",
                            "removedLineNumber" => "Remove this retained line from the current file generation: the previously matching unfinished final line no longer matches.",
                            "file" => "Omitted on idle and filtered no-match cursor polls; initial reads, matches, changes, and errors include it.",
                            "nextCursor" => "Use the advanced cursor even when no matches/file are returned. Omitted on idle: reuse the submitted cursor. Repeat query/useRegex/caseSensitive unchanged.",
                            "totalLineCount" => "Omitted when isIdle is true; otherwise the snapshot line count.",
                            "generationChanged" => "True: retained results belong to an obsolete file generation; reset them before applying this bounded current tail. Omitted means false.",
                            "lastLineUpdated" => "True: replace the retained unfinished line with the returned line having the same number in this file generation; apply removedLineNumber if present. Omitted means false.",
                            _ => "Omitted when empty."
                        };
                    }
                }
                if (properties["statistics"] is JsonObject statistics)
                    statistics["description"] = context.TypeInfo.Type == typeof(LogSearchResult)
                        ? "Included only when includeStatistics is true. Performance statistics for this search page."
                        : "Included only when includeStatistics is true. Performance statistics for this count call.";
            }
        }
        DescribeOutput(context.TypeInfo.Type, schema);
        return schema;
    }

    private static void DescribeOutput(Type type, JsonNode schema)
    {
        if (schema is not JsonObject objectSchema || objectSchema["properties"] is not JsonObject properties)
            return;
        if (IsEnvelope(type))
        {
            Describe("errors", "Request-level errors; inspect before interpreting result. Empty/omitted means none. MCP isError and nested file errors must also be checked.");
            Describe("result", "Absent/null when the operation is rejected; do not interpret as zero matches. Check envelope.errors and result.files[*].error or result.file.error even when partial/truncated flags are false.");
        }
        if (type == typeof(ConfiguredLogRequestError))
        {
            Describe("code", "Sanitized recovery code. Cursor mismatch: restore original arguments. Stale catalog/unknown target: rediscover IDs and restart. Expired/prior-process cursor: start without a cursor.");
            Describe("isRetryable", "Retry hint, not permission for a blind identical retry; use code-specific recovery and correct invalid arguments first.");
        }
        if (type == typeof(LogSearchResult))
        {
            Describe("incompleteReasons", SnapshotSearchDescription);
            Describe("pageIncompleteReasons", SnapshotSearchDescription);
            Describe("files", "Current-page matching, error, incomplete or truncated file evidence; clean exact zero-hit segments are omitted and counted by pageOmittedZeroHitFileCount. Not a full file inventory.");
            Describe("matchingLineCount", "Cumulative observed matching lines across committed pages; replace the previous total. Exact only when isQueryComplete is true.");
            Describe("matchOccurrenceCount", "Cumulative observed occurrences, possibly multiple per matching line; replace the previous total. Exact only when isQueryComplete is true.");
            Describe("pageMatchingLineCount", "Matching lines observed in this response's committed file segments, not the cumulative query total.");
            Describe("pageMatchOccurrenceCount", "Occurrences observed in this response's committed file segments; a line may contribute more than one.");
            Describe("returnedHitCount", "Hit-line records returned in this response, not all observed matches or occurrences.");
            Describe("queryReturnedHitCount", "Cumulative hit-line records emitted so far; replace the previous value. Bounded by maxQueryHits in text modes.");
            Describe("isPageComplete", "Current committed page segments have no scan errors/incomplete reasons. May be true while a file's frozen extent remains unfinished; check isQueryComplete and per-file isCountExact.");
            Describe("isQueryComplete", "True only when the whole selected frozen scope has finished successfully with exact counts. Text/provenance truncation alone does not make numeric counts inexact. No cursor alone does not prove completion.");
            Describe("pageOmittedZeroHitFileCount", "Clean exact zero-hit file segments omitted from this response's files array.");
        }
        if (type == typeof(LogSearchFileResult))
        {
            Describe("matchingLineCount", "Observed matching lines in this response's file segment, not a cumulative per-file total.");
            Describe("matchOccurrenceCount", "Observed occurrences in this response's file segment; may exceed matchingLineCount.");
            Describe("isCountExact", "True only after this file's frozen extent is fully evaluated without scan uncertainty/errors. Text/provenance truncation alone does not make counts inexact.");
        }
        if (type == typeof(LogCountResult))
        {
            Describe("incompleteReasons", SnapshotSearchDescription);
            Describe("matchingLineCount", "Cumulative observed matching lines; replace prior totals. Exact only when isComplete is true.");
            Describe("matchOccurrenceCount", "Cumulative observed occurrences; a line can contain multiple. Replace prior totals; exact only when isComplete is true.");
            Describe("isComplete", "Whole selected frozen scope successfully evaluated with exact aggregate/bucket counts. Metadata/provenance compaction alone does not make these counts inexact. Check incompleteReasons even without a cursor.");
            Describe("files", "Cumulative matched/error/incomplete file evidence; replace earlier records. Clean zero-count files are omitted; metadata may be compacted as disclosed by isFileRecordTruncated.");
            Describe("isFileRecordTruncated", "Per-file metadata was compacted; files is not a full inventory. Aggregate/bucket count exactness is independent of this flag.");
            Describe("resolvedTimeRange", "Inclusive bounds captured on the first call and frozen across continuations; independent of rolling-file selection by dateOffsetDays.");
        }
        if (type == typeof(LogCountFileResult))
        {
            Describe("matchingLineCount", "Cumulative observed matching lines for this file; replace prior values.");
            Describe("matchOccurrenceCount", "Cumulative observed occurrences for this file; replace prior values.");
            Describe("isCountExact", "True when this file's frozen extent was evaluated without scan uncertainty/errors; metadata/provenance compaction alone does not affect numeric exactness.");
        }
        if (type == typeof(LogSearchFileResult) || type == typeof(LogCountFileResult) || type == typeof(LogReadFileResult))
            Describe("error", "File-level failure; inspect even if envelope.errors is empty. Omitted means no file error; counts may be observed/inexact.");
        if (type == typeof(ConfiguredLogTreeResult))
        {
            Describe("catalogRevision", "Compare across pages; if changed, discard earlier discovery and restart at startIndex 0. Revision consistency is a client precondition, not server-enforced.");
            Describe("nodes", "Configured IDs for tool selectors; displayName/treePath are untrusted labels and may repeat. The same file ID may appear under multiple dashboards.");
            Describe("nextStartIndex", "Next positional page offset; repeat rootGroupId/maxDepth and compare catalogRevision. Absence does not imply full discovery when depthTruncated is true.");
            Describe("depthTruncated", "Deeper descendants were omitted. Query boundary groups with hasChildren by rootGroupId to discover them; maxDepth cannot exceed 20.");
        }
        if (type == typeof(LogReadLinesResult))
            Describe("actualEndLine", "Last emitted one-based line; when present, advance with actualEndLine + 1. Absent/null means no emitted line boundary (for example a range past the current end); inspect file.error.");
        if (type == typeof(LogLineResult) || type == typeof(LogSearchExcerptLine))
        {
            Describe("text", "Normalized, character-bounded line text; isTruncated indicates an excerpt rather than the full physical line.");
            Describe("isTruncated", "True when this line text was truncated; numeric count exactness is determined separately by completion/exactness flags.");
        }
        if (type == typeof(LogQueryStatus))
        {
            Describe("isReady", "Catalog/backend readiness; individual configured files may still be missing, inaccessible or unreadable.");
            Describe("limits", "Effective process caps/defaults, including defaultReadLineCount and maximumReadLineCount. Continuation fields are capacities/settings, not current usage.");
            Describe("activeIndexedSessions", "Current actively used line-index sessions; excludes search/count continuation sessions.");
            Describe("retainedIndexedSessions", "Current retained line-index sessions; excludes search/count continuation sessions.");
            Describe("mappedLineOffsets", "Current cached line-index offsets; excludes continuation-session state.");
        }

        void Describe(string name, string description)
        {
            if (properties[name] is JsonObject property)
                property["description"] = description;
        }
    }

    internal static void ConstrainInputs(JsonObject schema, string toolName)
    {
        var properties = schema["properties"]!.AsObject();
        ConstrainStringChoices(properties);
        Range("startLine", 1);
        Range("dateOffsetDays", 0);
        Range("startIndex", 0);
        Range("maxDepth", 0, ConfiguredLogLimits.DefaultTreeMaxDepth);
        Range("maxNodes", 1, ConfiguredLogLimits.DefaultTreeMaxNodes);
        Range("maxFiles", 1, ConfiguredLogLimits.MaximumResolvedFiles);
        foreach (var name in new[] { "maxHitsPerFile", "maxTotalHits", "maxQueryHits", "count", "maxLines", "timeoutMilliseconds" })
            Range(name, 1);
        Range("includeContextBefore", 0);
        Range("includeContextAfter", 0);
        if (properties["query"] is JsonObject query)
            query["minLength"] = 1;
        if (properties["fileId"] is JsonObject fileId)
        {
            fileId["minLength"] = 1;
            fileId["maxLength"] = ConfiguredLogLimits.DefaultMaxIdCharacters;
        }
        if (properties["rootGroupId"] is JsonObject root)
            root["maxLength"] = ConfiguredLogLimits.DefaultMaxIdCharacters;
        foreach (var name in new[] { "startTimestamp", "endTimestamp" })
            if (properties[name] is JsonObject timestamp)
                timestamp["maxLength"] = ConfiguredLogLimits.DefaultMaxTimestampCharacters;
        if (properties["targets"] is JsonObject targets)
        {
            targets["minItems"] = 1;
            if (targets["items"]?["properties"]?["id"] is JsonObject id)
            {
                id["minLength"] = 1;
                id["maxLength"] = ConfiguredLogLimits.DefaultMaxIdCharacters;
            }
            targets["description"] = "Nonempty typed configured targets: {kind: folder|dashboard|logFile, id}. server_status reports the effective target limit.";
        }
        if (toolName == "read_log_tail")
            schema["allOf"] = new JsonArray(new JsonObject
            {
                ["if"] = new JsonObject { ["anyOf"] = new JsonArray(Enabled("useRegex"), Enabled("caseSensitive")) },
                ["then"] = new JsonObject
                {
                    ["required"] = new JsonArray("query"),
                    ["properties"] = new JsonObject { ["query"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 } }
                }
            });

        void Range(string name, int minimum, int? maximum = null)
        {
            if (properties[name] is not JsonObject property)
                return;
            property["minimum"] = minimum;
            if (maximum.HasValue)
                property["maximum"] = maximum.Value;
        }
        static JsonObject Enabled(string name) => new()
        {
            ["required"] = new JsonArray(name),
            ["properties"] = new JsonObject { [name] = new JsonObject { ["const"] = true } }
        };
    }

    internal static void ConstrainStringChoices(JsonObject properties)
    {
        SetEnum(properties, "resultMode", "samples", "matchesOnly", "countsOnly");
        SetEnum(properties, "bucketSize", "none", "minute", "hour", "day");
        SetEnum(properties, "bucketMode", "sparse", "dense");
        SetEnum(properties, "provenanceMode", "shared", "inline");
    }

    private static bool AllowsNull(JsonNode? schema)
        => schema is JsonObject obj &&
           (obj["type"] is JsonArray types && types.Any(type => type?.GetValue<string>() == "null") ||
            obj["type"] is JsonValue typeValue && typeValue.TryGetValue<string>(out var typeName) && typeName == "null" ||
            obj["anyOf"] is JsonArray alternatives && alternatives.Any(AllowsNull));

    private static void SetEnum(JsonObject properties, string name, params string[] choices)
    {
        if (properties[name] is JsonObject property)
            property["enum"] = new JsonArray(choices.Select(choice => (JsonNode?)JsonValue.Create(choice)).ToArray());
    }

    private static bool HasOptionalMetadata(Type type)
        => type == typeof(LogSearchResult) || type == typeof(LogCountResult) ||
           type == typeof(LogSearchFileResult) || type == typeof(LogCountFileResult) ||
           type == typeof(LogReadFileResult) || type == typeof(LogSearchExcerptLine) ||
           type == typeof(LogReadTailResult);

    private static bool IsEnvelope(Type type)
        => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(LogOperationEnvelope<>);

    private static bool IsCompactEnvelope(Type type)
        => type == typeof(LogOperationEnvelope<LogSearchResult>) ||
           type == typeof(LogOperationEnvelope<LogReadTailResult>);

    private static bool IsOptionalMetadata(Type type, string name)
        => name is "incompleteReasons" or "pageIncompleteReasons" or "error" or "statistics" ||
           name == "provenanceTotalCount" &&
           (type == typeof(LogSearchFileResult) || type == typeof(LogCountFileResult) || type == typeof(LogReadFileResult)) ||
           type == typeof(LogSearchFileResult) && name is ("isTruncated" or "isProvenanceTruncated") ||
           type == typeof(LogSearchFileResult) && name is ("encoding" or "hits" or "excerpts" or "evaluatedThroughLine") ||
           type == typeof(LogSearchExcerptLine) && name == "isTruncated" ||
           type == typeof(LogReadTailResult) && name is
               ("file" or "nextCursor" or "totalLineCount" or "generationChanged" or "lastLineUpdated" or
                "examinedLineCount" or "skippedLineCount" or "remainingLineCount" or "removedLineNumber");
}
