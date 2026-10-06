namespace LogReader.Mcp;

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using LogReader.Core.Models;
using Microsoft.Extensions.AI;

internal static class McpResponseJsonPolicy
{
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
                        traversal["description"] = "All candidates were visited or terminated with explicit errors. This does not imply counts are exact. A null nextCursor can also mean a terminal query hit cap; check isTraversalComplete and incompleteReasons.";
                    if (properties["stopReason"] is JsonObject stop)
                    {
                        stop["description"] = "Reason this response stopped. Work and page output limits yield continuations. The query-wide hit cap terminates text search with query_hit_limit, no nextCursor, and incomplete traversal. Count-only traversal is unaffected.";
                        stop["enum"] = new JsonArray("time_slice", "scan_budget", "hit_limit", "response_limit", "scope_exhausted");
                    }
                    if (properties["nextCursor"] is JsonObject next)
                        next["description"] = "Resume the same query. Presentation modes, timeout and statistics may change. Process-local, 15-minute idle expiry. Check completeness even without a cursor.";
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
                            "hits" => "Omitted when no hit references are returned.",
                            "excerpts" => "Omitted when no search text is returned.",
                            "isTruncated" => "Included only when this file or excerpt line is truncated.",
                            "isProvenanceTruncated" => "Included only when provenance is truncated.",
                            "evaluatedThroughLine" => "Included only when file evaluation is incomplete and a boundary is available.",
                            "provenanceTotalCount" => "Included only when provenance is truncated.",
                            "examinedLineCount" => "Included for filtered tail reads; physical lines examined in this call.",
                            "skippedLineCount" => "Included for filtered tail reads; examined lines that did not match.",
                            "remainingLineCount" => "Included for filtered tail reads; physical lines remaining after the cursor in this snapshot.",
                            "removedLineNumber" => "Included when a previously matching unfinished final line no longer matches.",
                            "file" => "Omitted on idle and filtered no-match cursor polls; initial reads, matches, changes, and errors include it.",
                            "nextCursor" => "Omitted when isIdle is true; reuse the cursor supplied in that request.",
                            "totalLineCount" => "Omitted when isIdle is true; otherwise the snapshot line count.",
                            "generationChanged" or "lastLineUpdated" => "Included only when a generation or unfinished-line change occurs.",
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
        return schema;
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
