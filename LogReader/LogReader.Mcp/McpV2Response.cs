namespace LogReader.Mcp;

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using LogReader.Core.Models;
using Microsoft.Extensions.AI;
using Json.Schema;
using ModelContextProtocol.Protocol;

internal sealed record McpIssue(string Code, string Scope, string Message, string Recovery,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ArgumentPath = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FileId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null);

internal sealed record McpContinuation(string Tool, string Cursor);
internal sealed record McpToolEnvelope(int SchemaVersion, string RequestId, string CatalogRevision, string Status,
    JsonNode? Data, IReadOnlyList<McpIssue> Issues, McpContinuation? Continuation);

internal static class McpV2Response
{
    internal const int SchemaVersion = 5;
    internal static JsonSchema CompileSchema(JsonElement schema) => JsonSchema.FromText(schema.GetRawText());

    internal static CallToolResult Failure(params McpIssue[] issues)
        => Serialize(new(SchemaVersion, Guid.NewGuid().ToString("N"), "", "error", null, issues, null));

    internal static CallToolResult Project<T>(LogOperationEnvelope<T> response, bool statistics)
    {
        // Keep the proven sparse bucket/shared provenance projection; Core types never become the wire envelope.
        var compact = McpLogTools.SerializeResponse(response, statistics);
        var source = JsonNode.Parse(compact.StructuredContent!.Value.GetRawText())!.AsObject();
        var data = source["result"]?.DeepClone();
        var next = data?["nextCursor"]?.GetValue<string>();
        var issues = new List<McpIssue>();
        foreach (var error in response.Errors)
        {
            // A retained continuation is the normal way to finish bounded work after a slice deadline.
            if (error.Code == "deadline_exceeded" && next != null) continue;
            issues.Add(Issue(error, dataType: typeof(T)));
        }
        if (data?["errors"] is JsonArray treeErrors)
            foreach (var error in treeErrors)
                issues.Add(Issue(error!.Deserialize<ConfiguredLogRequestError>(McpV2Tools.JsonOptions)!));
        var wholeFailure = data == null || issues.Any(i => i.Scope is "argument" or "continuity") ||
            issues.Any(i => i.Scope == "request") && next == null;
        if (response.FileOutcomes is { } outcomes)
        {
            wholeFailure |= outcomes.WhollyUnevaluable;
            issues.AddRange(outcomes.Issues.Select(error => Issue(error, error.TargetId)));
            if (outcomes.OmittedIssueCount > 0)
                issues.Add(new("file_issues_truncated", "request",
                    $"{outcomes.OmittedIssueCount} additional file issues were omitted.", "report_failure"));
        }
        else
        {
            wholeFailure |= AddFileIssues(data, next, issues);
        }
        var status = wholeFailure ? "error" : issues.Count > 0 ? "partial" : "success";
        if (data != null)
        {
            NormalizeData(data);
            data["isTruncated"] = response.IsTruncated;
            data["truncationReasons"] = JsonSerializer.SerializeToNode(response.TruncationReasons, McpV2Tools.JsonOptions);
        }
        var continuationTool = typeof(T) == typeof(LogSearchResult) ? "continue_log_search"
            : typeof(T) == typeof(LogCountResult) ? "continue_log_count" : "poll_log_monitor";
        return Serialize(new(SchemaVersion, response.RequestId, response.CatalogRevision, status,
            wholeFailure ? null : data, issues.Distinct().ToArray(), !wholeFailure && next != null ? new(continuationTool, next) : null));
    }

    private static bool AddFileIssues(JsonNode? data, string? next, List<McpIssue> issues)
    {
        var files = data?["files"] is JsonArray many ? many.OfType<JsonObject>().ToArray()
            : data?["file"] is JsonObject one ? [one] : Array.Empty<JsonObject>();
        foreach (var file in files)
        {
            if (file["error"] is JsonNode error)
                issues.Add(Issue(error.Deserialize<ConfiguredLogRequestError>(McpV2Tools.JsonOptions)!, file["fileId"]?.GetValue<string>()));
            else if (file["incompleteReasons"] is JsonArray reasons)
                foreach (var reason in reasons.Select(r => r!.GetValue<string>()).Where(r => r is "file_generation_unverified" or "timestamp_bucket_unassigned"))
                    issues.Add(new(reason, "file", "The captured file evidence could not be fully verified. Report the available evidence without restarting automatically.",
                        "report_failure", FileId: file["fileId"]?.GetValue<string>()));
        }
        var selected = data?["selectedFileCount"]?.GetValue<int>() ?? files.Length;
        return next == null && files.Length > 0 && files.Length >= selected && files.All(f => f["error"] != null);
    }

    private static McpIssue Issue(ConfiguredLogRequestError error, string? fileId = null, Type? dataType = null)
    {
        var continuity = error.Code.Contains("cursor", StringComparison.Ordinal) &&
            !error.Code.EndsWith("_required", StringComparison.Ordinal) || error.Code == "handoff_boundary_unavailable";
        var path = error.ArgumentPath ?? (error.Code switch
        {
            "query_required" or "query_too_long" => "/query",
            "invalid_exclude_query" => "/excludeQuery",
            "invalid_regex" => error.Message.Contains("excludeQuery", StringComparison.Ordinal) ? "/excludeQuery" : "/query",
            "targets_required" or "invalid_targets" or "target_limit_exceeded" => "/targets",
            "file_id_required" or "invalid_file_id" => "/fileId",
            "invalid_date_offset" => "/dateOffsetDays",
            "invalid_bucket_size" or "count_bucket_range_required" or "unsupported_time_bucket" or "count_bucket_limit_exceeded" => "/bucketSize",
            "invalid_relative_window" or "relative_window_limit_exceeded" => "/relativeWindow",
            "invalid_timeout" => "/timeoutMilliseconds",
            "invalid_line_count" => dataType == typeof(LogMonitorResult) ? "/maxLines" : "/count",
            "invalid_file_limit" => "/maxFiles",
            "invalid_hit_limit" => "/maxHitsPerFile",
            "invalid_total_hit_limit" => "/maxTotalHits",
            "invalid_query_hit_limit" => "/maxQueryHits",
            "invalid_observation_period" => "/observeForMilliseconds",
            _ => (string?)null
        });
        var scope = fileId != null ? "file" : continuity ? "continuity" : path != null ? "argument" : "request";
        var recovery = scope == "argument" ? "correct_arguments" : continuity ? "report_gap"
            : error.Code is "unknown_target" or "target_kind_mismatch" ? "rediscover"
            : error.IsRetryable ? "retry_later" : "report_failure";
        return new(error.Code, scope, error.Message, recovery, path, fileId, error.Reason);
    }

    private static CallToolResult Serialize(McpToolEnvelope envelope)
    {
        var json = JsonSerializer.SerializeToElement(envelope, McpV2Tools.JsonOptions);
        return new() { IsError = envelope.Status == "error", StructuredContent = json,
            Content = [new TextContentBlock { Text = json.GetRawText() }] };
    }

    private static readonly HashSet<string> Removed = new(StringComparer.Ordinal)
    {
        "errors", "error", "nextCursor", "contractVersion", "resultMode", "bucketMode", "requestedStartLine"
    };
    private static string Rename(string name) => name is "isCountExact" or "isQueryComplete" or "isComplete" ? "scanComplete" : name;

    private static void NormalizeData(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToArray())
            {
                if (Removed.Contains(property.Key)) { obj.Remove(property.Key); continue; }
                if (property.Value != null) NormalizeData(property.Value);
                var name = Rename(property.Key);
                if (name != property.Key) { obj.Remove(property.Key); obj[name] = property.Value; }
            }
        }
        else if (node is JsonArray array)
            foreach (var item in array) if (item != null) NormalizeData(item);
    }

    internal static JsonElement Schema<T>()
    {
        var options = McpLogTools.CreateSerializerOptions(true);
        var original = AIJsonUtilities.CreateJsonSchema(typeof(LogOperationEnvelope<T>), serializerOptions: options,
            inferenceOptions: new() { TransformSchemaNode = McpResponseJsonPolicy.TransformSchema });
        var shared = McpResponseProjector.ShareProvenanceSchema(original, options);
        var root = JsonNode.Parse(shared.GetRawText())!.AsObject();
        var data = root["properties"]!["result"]!.DeepClone();
        NormalizeSchema(data);
        data["description"] = "Operation data. scanComplete requires evaluation of captured extents with verifiable file contents. Null on rejection or wholly unevaluable scope.";
        data["properties"]!["isTruncated"] = new JsonObject { ["type"] = "boolean" };
        data["properties"]!["truncationReasons"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } };
        var schema = JsonNode.Parse("""
        {
          "type":"object","additionalProperties":false,
          "required":["schemaVersion","requestId","catalogRevision","status","data","issues","continuation"],
          "properties":{
            "schemaVersion":{"type":"integer","const":5},
            "requestId":{"type":"string"},"catalogRevision":{"type":"string"},
            "status":{"type":"string","enum":["success","partial","error"],"description":"Routine pagination is successful. Partial means available evidence with file failures or unverified contents. Error means rejected or wholly unevaluable."},
            "issues":{"type":"array","items":{"type":"object","additionalProperties":false,
              "required":["code","scope","message","recovery"],"properties":{
                "code":{"type":"string"},"scope":{"type":"string","enum":["argument","request","file","continuity"]},
                "message":{"type":"string"},"recovery":{"type":"string","enum":["correct_arguments","report_gap","rediscover","retry_later","report_failure"]},
                "argumentPath":{"type":"string","description":"JSON Pointer to the invalid argument."},"fileId":{"type":"string"},
                "reason":{"type":"string","enum":["session_limit_exceeded","memory_budget_exceeded","query_too_large"]}}}},
            "continuation":{"type":["object","null"],"additionalProperties":false,"required":["tool","cursor"],"properties":{
              "tool":{"type":"string","enum":["continue_log_search","continue_log_count","poll_log_monitor"]},"cursor":{"type":"string"}}}
          }
        }
        """)!.AsObject();
        schema["properties"]!["data"] = data;
        schema["$defs"] = root["$defs"]!.DeepClone();
        return JsonSerializer.SerializeToElement(schema, McpV2Tools.JsonOptions);
    }

    private static void NormalizeSchema(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (obj["properties"] is JsonObject properties)
            {
                foreach (var property in properties.ToArray())
                {
                    if (Removed.Contains(property.Key) || property.Key == "provenance") { properties.Remove(property.Key); continue; }
                    var name = Rename(property.Key);
                    if (name != property.Key)
                    {
                        properties.Remove(property.Key); properties[name] = property.Value;
                        property.Value!["description"] = "True when the captured extent was fully evaluated with verifiable file contents.";
                    }
                    if (name == "handoffCursor")
                        property.Value!["description"] = "Opt-in boundary for start_log_monitor_from_snapshot, available after the scan finishes with verified file contents.";
                }
                if (properties.ContainsKey("provenanceRefs")) obj.Remove("oneOf");
                if (properties.ContainsKey("provenanceTable")) obj.Remove("allOf");
                if (properties.ContainsKey("bucketCounts"))
                {
                    properties.Remove("buckets"); obj.Remove("oneOf");
                    obj["if"] = new JsonObject { ["properties"] = new JsonObject { ["bucketSize"] = new JsonObject { ["enum"] = new JsonArray("minute", "hour", "day") } }, ["required"] = new JsonArray("bucketSize") };
                }
            }
            if (obj["required"] is JsonArray required)
            {
                var names = required.Select(n => n!.GetValue<string>()).Where(n => !Removed.Contains(n) && n is not ("provenance" or "buckets")).Select(Rename).Distinct().ToArray();
                obj["required"] = new JsonArray(names.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
            }
            if (obj["description"] is JsonValue description && description.TryGetValue<string>(out var text))
            {
                if (text.Contains("growth", StringComparison.OrdinalIgnoreCase) || text.Contains("file_grew", StringComparison.Ordinal) || text.Contains("countsOnly", StringComparison.Ordinal))
                    obj.Remove("description");
                else
                    obj["description"] = text.Replace("isComplete", "scanComplete", StringComparison.Ordinal).Replace("isQueryComplete", "scanComplete", StringComparison.Ordinal);
            }
            foreach (var property in obj.ToArray()) if (property.Value != null) NormalizeSchema(property.Value);
        }
        else if (node is JsonArray array)
            foreach (var item in array) if (item != null) NormalizeSchema(item);
    }
}
