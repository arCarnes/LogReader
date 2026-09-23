namespace LogReader.Mcp;

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using LogReader.Core.Models;
using Microsoft.Extensions.AI;

internal static class McpResponseJsonPolicy
{
    public static void Apply(JsonTypeInfo typeInfo, bool includeStatistics)
    {
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
                        "totalLineCount" or "generationChanged" or "lastLineUpdated" =>
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
                            "isTruncated" => "Included only when this excerpt line is truncated.",
                            "evaluatedThroughLine" => "Included only when file evaluation is incomplete and a boundary is available.",
                            "provenanceTotalCount" => "Included only when provenance is truncated.",
                            "examinedLineCount" => "Included for filtered tail reads; physical lines examined in this call.",
                            "skippedLineCount" => "Included for filtered tail reads; examined lines that did not match.",
                            "remainingLineCount" => "Included for filtered tail reads; physical lines remaining after the cursor in this snapshot.",
                            "removedLineNumber" => "Included when a previously matching unfinished final line no longer matches.",
                            "file" => "Omitted on idle and filtered no-match cursor polls; initial reads, matches, changes, and errors include it.",
                            "nextCursor" => "Omitted when isIdle is true; reuse the cursor supplied in that request.",
                            "totalLineCount" => "Omitted when isIdle is true; otherwise the snapshot line count.",
                            "generationChanged" or "lastLineUpdated" => "Omitted when isIdle is true; otherwise indicates a generation or unfinished-line change.",
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

    private static bool HasOptionalMetadata(Type type)
        => type == typeof(LogSearchResult) || type == typeof(LogCountResult) ||
           type == typeof(LogSearchFileResult) || type == typeof(LogCountFileResult) ||
           type == typeof(LogReadFileResult) || type == typeof(LogSearchExcerptLine) ||
           type == typeof(LogReadTailResult);

    private static bool IsOptionalMetadata(Type type, string name)
        => name is "incompleteReasons" or "pageIncompleteReasons" or "error" or "statistics" ||
           name == "provenanceTotalCount" &&
           (type == typeof(LogSearchFileResult) || type == typeof(LogCountFileResult) || type == typeof(LogReadFileResult)) ||
           type == typeof(LogSearchFileResult) && name is ("encoding" or "hits" or "excerpts" or "evaluatedThroughLine") ||
           type == typeof(LogSearchExcerptLine) && name == "isTruncated" ||
           type == typeof(LogReadTailResult) && name is
               ("file" or "nextCursor" or "totalLineCount" or "generationChanged" or "lastLineUpdated" or
                "examinedLineCount" or "skippedLineCount" or "remainingLineCount" or "removedLineNumber");
}
