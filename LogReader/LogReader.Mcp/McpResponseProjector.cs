namespace LogReader.Mcp;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using LogReader.Core.Models;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;

internal static class McpResponseProjector
{
    internal const int SchemaVersion = 4;

    public static CallToolResult Serialize<T>(
        LogOperationEnvelope<T> response,
        JsonSerializerOptions options,
        string? bucketMode = null,
        string provenanceMode = "shared")
    {
        // All transformation state belongs to this response, including replayed calls.
        var envelope = JsonSerializer.SerializeToNode(response, options)!.AsObject();
        if (response.Result is LogCountResult count && envelope["result"] is JsonObject result)
            ProjectBuckets(count, result, bucketMode ?? "sparse");
        if (provenanceMode == "shared" && envelope["result"] is JsonObject fileResult)
            ProjectProvenance(fileResult, options);
        var content = JsonSerializer.SerializeToElement(envelope, options);
        return new CallToolResult
        {
            StructuredContent = content,
            Content = [new TextContentBlock { Text = content.GetRawText() }]
        };
    }

    public static CallToolResult InvalidMode(string name, string choices)
        => new()
        {
            IsError = true,
            Content = [new TextContentBlock { Text = $"{name} must be one of: {choices}." }]
        };

    private static void ProjectProvenance(JsonObject result, JsonSerializerOptions options)
    {
        var table = new JsonArray();
        // Record equality covers every field; string equality is ordinal.
        var indices = new Dictionary<ConfiguredLogProvenance, int>();
        var visibleFiles = result["files"] is JsonArray files
            ? files.OfType<JsonObject>()
            : result["file"] is JsonObject file ? [file] : Enumerable.Empty<JsonObject>();
        var hasFiles = false;
        foreach (var visibleFile in visibleFiles)
        {
            hasFiles = true;
            var references = new JsonArray();
            if (visibleFile["provenance"] is JsonArray provenance)
            {
                foreach (var route in provenance)
                {
                    var key = route!.Deserialize<ConfiguredLogProvenance>(options)!;
                    if (!indices.TryGetValue(key, out var index))
                    {
                        index = indices.Count;
                        indices.Add(key, index);
                        table.Add(route!.DeepClone());
                    }
                    references.Add(index);
                }
            }
            visibleFile.Remove("provenance");
            visibleFile["provenanceRefs"] = references;
        }
        if (hasFiles)
            result["provenanceTable"] = table;
    }

    internal static void TransformProvenanceFileSchema(JsonObject schema)
    {
        if (schema["properties"] is not JsonObject properties)
            return;
        if (schema["required"] is JsonArray required)
        {
            for (var index = required.Count - 1; index >= 0; index--)
                if (required[index]?.GetValue<string>() == "provenance")
                    required.RemoveAt(index);
        }
        properties["provenanceRefs"] = new JsonObject
        {
            ["type"] = "array", ["items"] = Integer(),
            ["description"] = "Ordered indices into this response's provenanceTable; expands to retained provenance. Existing truncation flags/totals still apply."
        };
        schema["oneOf"] = new JsonArray(
            new JsonObject
            {
                ["required"] = new JsonArray("provenanceRefs"),
                ["not"] = new JsonObject { ["required"] = new JsonArray("provenance") }
            },
            new JsonObject
            {
                ["required"] = new JsonArray("provenance"),
                ["not"] = new JsonObject { ["required"] = new JsonArray("provenanceRefs") }
            });
    }

    internal static JsonElement ShareProvenanceSchema(JsonElement schema, JsonSerializerOptions options)
    {
        var root = JsonNode.Parse(schema.GetRawText())!.AsObject();
        var definitions = root["$defs"] as JsonObject ?? new JsonObject();
        if (root["$defs"] is null)
            root["$defs"] = definitions;
        definitions["provenance"] = JsonNode.Parse(AIJsonUtilities.CreateJsonSchema(
            typeof(ConfiguredLogProvenance), serializerOptions: options).GetRawText());
        ShareItems(root);
        return JsonSerializer.SerializeToElement(root, options);

        static void ShareItems(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if (obj["properties"]?["provenance"] is JsonObject provenance)
                    provenance["items"] = new JsonObject { ["$ref"] = "#/$defs/provenance" };
                foreach (var property in obj)
                    ShareItems(property.Value);
            }
            else if (node is JsonArray array)
            {
                foreach (var item in array)
                    ShareItems(item);
            }
        }
    }

    internal static void TransformProvenanceResultSchema(JsonObject schema, bool multipleFiles)
    {
        var fileProperty = multipleFiles ? "files" : "file";
        schema["allOf"] = new JsonArray(new JsonObject
        {
            ["if"] = new JsonObject { ["required"] = new JsonArray("provenanceTable") },
            ["then"] = new JsonObject
            {
                ["required"] = new JsonArray(fileProperty),
                ["properties"] = new JsonObject { [fileProperty] = FileConstraint("provenanceRefs", multipleFiles, visible: true) }
            },
            ["else"] = new JsonObject
            {
                ["properties"] = new JsonObject { [fileProperty] = FileConstraint("provenance", multipleFiles, visible: false) }
            }
        });

        static JsonObject FileConstraint(string property, bool multiple, bool visible)
        {
            var file = new JsonObject { ["required"] = new JsonArray(property) };
            if (!multiple)
            {
                if (visible)
                    file["type"] = "object";
                return file;
            }
            var files = new JsonObject { ["items"] = file };
            if (visible)
                files["minItems"] = 1;
            return files;
        }
    }

    private static void ProjectBuckets(LogCountResult count, JsonObject result, string mode)
    {
        result["bucketMode"] = mode;
        if (mode == "dense")
            return;

        result.Remove("buckets");
        var counts = new JsonArray();
        result["bucketCounts"] = counts;
        if (count.Buckets.IsDefaultOrEmpty)
            return;
        for (var index = 0; index < count.Buckets.Length; index++)
        {
            var bucket = count.Buckets[index];
            if (bucket.MatchingLineCount != 0 || bucket.MatchOccurrenceCount != 0)
                counts.Add(new JsonArray(index, bucket.MatchingLineCount, bucket.MatchOccurrenceCount));
        }
        var stepSeconds = count.BucketSize switch
        {
            "minute" => 60,
            "hour" => 3_600,
            "day" => 86_400,
            _ => throw new InvalidOperationException("Cannot project an unknown bucket size.")
        };
        var kind = count.Buckets[0].Kind;
        var anchors = new JsonArray();
        var anchorIndex = 0;
        var anchorText = count.Buckets[0].Start;
        anchors.Add(new JsonArray(0, anchorText));
        for (var index = 1; index <= count.Buckets.Length; index++)
        {
            var boundary = count.Buckets[index - 1].EndExclusive;
            if (index < count.Buckets.Length &&
                !StringComparer.Ordinal.Equals(boundary, count.Buckets[index].Start))
                throw new InvalidOperationException("Cannot project a noncontiguous bucket grid.");

            var seconds = (long)(index - anchorIndex) * stepSeconds;
            var matches = kind switch
            {
                "dated" => DateTimeOffset.Parse(anchorText, CultureInfo.InvariantCulture)
                    .AddSeconds(seconds).EqualsExact(DateTimeOffset.Parse(boundary, CultureInfo.InvariantCulture)),
                "timeOfDay" => TimeSpan.Parse(anchorText, CultureInfo.InvariantCulture)
                    .Add(TimeSpan.FromSeconds(seconds)) == TimeSpan.Parse(boundary, CultureInfo.InvariantCulture),
                _ => throw new InvalidOperationException("Cannot project an unknown bucket kind.")
            };
            if (!matches)
            {
                anchors.Add(new JsonArray(index, boundary));
                anchorIndex = index;
                anchorText = boundary;
            }
        }
        result["bucketGrid"] = new JsonObject
        {
            ["kind"] = kind,
            ["count"] = count.Buckets.Length,
            ["stepSeconds"] = stepSeconds,
            ["anchors"] = anchors
        };
    }

    internal static void TransformCountSchema(JsonObject schema)
    {
        if (schema["properties"] is not JsonObject properties)
            return;
        if (schema["required"] is null)
            schema["required"] = new JsonArray();
        schema["required"]!.AsArray().Add("bucketMode");
        if (schema["required"] is JsonArray required)
        {
            for (var index = required.Count - 1; index >= 0; index--)
                if (required[index]?.GetValue<string>() == "buckets")
                    required.RemoveAt(index);
        }
        properties["bucketMode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("sparse", "dense") };
        properties["bucketCounts"] = new JsonObject
        {
            ["type"] = "array",
            ["description"] = "Sparse ordered [bucketIndex, matchingLineCount, matchOccurrenceCount] tuples; 64-bit counts. Omitted tuples are observed zeros, exact only if isComplete is true.",
            ["items"] = Tuple(Integer(), Integer(), Integer())
        };
        properties["bucketGrid"] = new JsonObject
        {
            ["type"] = "object",
            ["description"] = "Sparse boundaries: latest preceding anchor plus index difference times stepSeconds. Preserve the anchor's UTC offset; use duration arithmetic for timeOfDay. Bucket i spans boundaries i and i+1. Omitted for bucketSize none.",
            ["required"] = new JsonArray("kind", "count", "stepSeconds", "anchors"),
            ["properties"] = new JsonObject
            {
                ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("dated", "timeOfDay") },
                ["count"] = Integer(),
                ["stepSeconds"] = new JsonObject { ["type"] = "integer", ["enum"] = new JsonArray(60, 3_600, 86_400) },
                ["anchors"] = new JsonObject
                {
                    ["type"] = "array", ["minItems"] = 1,
                    ["description"] = "Ordered [boundaryIndex, timestamp] pairs, starting at index 0; includes offset/step changes and the final exclusive boundary when needed.",
                    ["items"] = Tuple(Integer(), new JsonObject { ["type"] = "string" })
                }
            }
        };
        schema["oneOf"] = new JsonArray(
            new JsonObject
            {
                ["properties"] = new JsonObject { ["bucketMode"] = new JsonObject { ["const"] = "sparse" } },
                ["required"] = new JsonArray("bucketCounts"),
                ["not"] = new JsonObject { ["required"] = new JsonArray("buckets") }
            },
            new JsonObject
            {
                ["properties"] = new JsonObject { ["bucketMode"] = new JsonObject { ["const"] = "dense" } },
                ["required"] = new JsonArray("buckets"),
                ["not"] = new JsonObject { ["anyOf"] = new JsonArray(
                    new JsonObject { ["required"] = new JsonArray("bucketCounts") },
                    new JsonObject { ["required"] = new JsonArray("bucketGrid") }) }
            });
        schema["if"] = new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["bucketMode"] = new JsonObject { ["const"] = "sparse" },
                ["bucketSize"] = new JsonObject { ["enum"] = new JsonArray("minute", "hour", "day") }
            },
            ["required"] = new JsonArray("bucketMode", "bucketSize")
        };
        schema["then"] = new JsonObject { ["required"] = new JsonArray("bucketGrid") };
        schema["else"] = new JsonObject { ["not"] = new JsonObject { ["required"] = new JsonArray("bucketGrid") } };
    }

    private static JsonObject Integer() => new() { ["type"] = "integer", ["minimum"] = 0 };

    private static JsonObject Tuple(params JsonNode[] items)
        => new()
        {
            ["type"] = "array", ["minItems"] = items.Length, ["maxItems"] = items.Length,
            ["prefixItems"] = new JsonArray(items), ["items"] = false
        };
}
