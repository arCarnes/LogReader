namespace LogReader.Mcp;

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using LogReader.Core.Models;
using Microsoft.Extensions.AI;

internal static class McpResponseJsonPolicy
{
    public static void Apply(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Type == typeof(LogSearchResult) || typeInfo.Type == typeof(LogCountResult))
        {
            for (var index = typeInfo.Properties.Count - 1; index >= 0; index--)
            {
                if (typeInfo.Properties[index].Name is "statistics" or "effectiveLimits")
                    typeInfo.Properties.RemoveAt(index);
            }
        }

        if (HasOptionalMetadata(typeInfo.Type))
        {
            foreach (var property in typeInfo.Properties)
            {
                // Keep false/zero exactness and truncation signals explicit. Only
                // these optional fields have an unambiguous empty interpretation.
                if (IsOptionalMetadata(property.Name))
                {
                    property.IsRequired = false;
                    property.ShouldSerialize = static (_, value) => value switch
                    {
                        null => false,
                        ImmutableArray<string> reasons => !reasons.IsDefaultOrEmpty,
                        ImmutableArray<LogLineResult> lines => !lines.IsDefaultOrEmpty,
                        _ => true
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
                    if (IsOptionalMetadata(required[index]!.GetValue<string>()))
                        required.RemoveAt(index);
                }
            }
            if (objectSchema["properties"] is JsonObject properties)
            {
                foreach (var property in properties)
                {
                    if (IsOptionalMetadata(property.Key) && property.Value is JsonObject propertySchema)
                        propertySchema["description"] = property.Key == "error"
                            ? "Omitted when there is no file error."
                            : "Omitted when empty.";
                }
            }
        }
        return schema;
    }

    private static bool HasOptionalMetadata(Type type)
        => type == typeof(LogSearchResult) || type == typeof(LogCountResult) ||
           type == typeof(LogSearchFileResult) || type == typeof(LogCountFileResult) ||
           type == typeof(LogReadFileResult) || type == typeof(LogSearchHit);

    private static bool IsOptionalMetadata(string name)
        => name is "incompleteReasons" or "pageIncompleteReasons" or "contextBefore" or "contextAfter" or "error";
}
