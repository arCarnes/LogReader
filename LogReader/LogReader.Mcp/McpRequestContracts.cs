namespace LogReader.Mcp;

using System.Text.Json.Nodes;
using LogReader.Core.Models;

// These are wire contracts, deliberately independent of Core's persistence and query schemas.
internal sealed record McpTarget(string Kind, string Id)
{
    public ConfiguredLogTarget ToCore() => new(Kind switch
    {
        "folder" => ConfiguredLogTargetKind.Folder,
        "dashboard" => ConfiguredLogTargetKind.Dashboard,
        "logFile" => ConfiguredLogTargetKind.LogFile,
        _ => throw new InvalidOperationException("The validated target kind was unsupported.")
    }, Id);
}

internal sealed class McpSnapshotRequest
{
    public McpTarget[] Targets { get; init; } = [];
    public string Query { get; init; } = "";
    public string? ExcludeQuery { get; init; }
    public bool UseRegex { get; init; }
    public bool CaseSensitive { get; init; }
    public int DateOffsetDays { get; init; }
    public string? StartTimestamp { get; init; }
    public string? EndTimestamp { get; init; }
    public string? RelativeWindow { get; init; }
    public string BucketSize { get; init; } = "none";
    public int? MaxFiles { get; init; }
    public int? MaxHitsPerFile { get; init; }
    public int? MaxTotalHits { get; init; }
    public int? MaxQueryHits { get; init; }
    public int IncludeContextBefore { get; init; }
    public int IncludeContextAfter { get; init; }
    public int? TimeoutMilliseconds { get; init; }
    public bool IncludeStatistics { get; init; }
    public bool EnableHandoff { get; init; }
    public int? ObserveForMilliseconds { get; init; }

    public LogSearchQuery Search() => new()
    {
        Targets = Targets.Select(t => t.ToCore()).ToArray(), Query = Query, ExcludeQuery = ExcludeQuery,
        UseRegex = UseRegex, CaseSensitive = CaseSensitive, DateOffsetDays = DateOffsetDays,
        StartTimestamp = StartTimestamp, EndTimestamp = EndTimestamp, RelativeWindow = RelativeWindow,
        MaxFiles = MaxFiles, MaxHitsPerFile = MaxHitsPerFile, MaxTotalHits = MaxTotalHits, MaxQueryHits = MaxQueryHits,
        IncludeContextBefore = IncludeContextBefore, IncludeContextAfter = IncludeContextAfter,
        TimeoutMilliseconds = TimeoutMilliseconds, EnableHandoff = EnableHandoff, ResultMode = "samples"
    };

    public LogCountQuery Count() => new()
    {
        Targets = Targets.Select(t => t.ToCore()).ToArray(), Query = Query, ExcludeQuery = ExcludeQuery,
        UseRegex = UseRegex, CaseSensitive = CaseSensitive, DateOffsetDays = DateOffsetDays,
        StartTimestamp = StartTimestamp, EndTimestamp = EndTimestamp, RelativeWindow = RelativeWindow,
        BucketSize = BucketSize, TimeoutMilliseconds = TimeoutMilliseconds, EnableHandoff = EnableHandoff
    };

    public LogMonitorQuery Monitor() => new()
    {
        Targets = Targets.Select(t => t.ToCore()).ToArray(), Query = Query.Length == 0 ? null : Query,
        ExcludeQuery = ExcludeQuery, UseRegex = UseRegex, CaseSensitive = CaseSensitive, DateOffsetDays = DateOffsetDays,
        StartTimestamp = StartTimestamp, EndTimestamp = EndTimestamp, RelativeWindow = RelativeWindow,
        TimeoutMilliseconds = TimeoutMilliseconds, ObserveForMilliseconds = ObserveForMilliseconds
    };
}

internal sealed record McpContinueRequest(string Cursor, int? TimeoutMilliseconds = null, bool IncludeStatistics = false);
internal sealed record McpReadRequest(string FileId, string Anchor = "start", int Offset = 0, int? Count = null,
    int DateOffsetDays = 0, int? TimeoutMilliseconds = null);
internal sealed record McpPollRequest(string Cursor, int MaxFiles = 10, int? MaxLines = null, int? TimeoutMilliseconds = null);
internal sealed record McpStopRequest(string Cursor, int? TimeoutMilliseconds = null);
internal sealed record McpHandoffRequest(string HandoffCursor, int? ObserveForMilliseconds = null, int? TimeoutMilliseconds = null);
internal sealed record McpTreeRequest(string? RootGroupId = null, int MaxDepth = 0, int MaxNodes = 500,
    int StartIndex = 0, string? ExpectedCatalogRevision = null);

internal static class McpInputSchemas
{
    internal static JsonObject For(string name)
    {
        var p = new JsonObject();
        var required = new JsonArray();
        if (name is "search_logs" or "count_logs" or "start_log_monitor")
        {
            p["targets"] = new JsonObject
            {
                ["type"] = "array", ["minItems"] = 1,
                ["description"] = "Required configured IDs from list_log_tree. Example: [{\"kind\":\"logFile\",\"id\":\"<discovered-id>\"}]. server_status reports the target limit.",
                ["items"] = Object(new JsonObject { ["kind"] = Choice("folder", "dashboard", "logFile"), ["id"] = Text(256) }, "kind", "id")
            };
            required.Add("targets");
            p["query"] = Text(description: "Literal text or .NET regex (250 ms match timeout). For search/count, supply text or time criteria.", minimum: name == "start_log_monitor" ? 1 : 0);
            p["excludeQuery"] = Text(description: "Exclude matching lines using the same regex/case options.");
            p["useRegex"] = Flag(); p["caseSensitive"] = Flag();
            p["dateOffsetDays"] = Number(0, description: "Days before the captured server-local date. Selects a rolling file, independently of line timestamp filters.");
            p["startTimestamp"] = Text(256, "Inclusive ISO-8601 or yyyy-MM-dd HH:mm[:ss[.fffffff]] or HH:mm[:ss[.fffffff]]. Missing timestamps are excluded; dated bounds exclude time-only timestamps.");
            p["endTimestamp"] = Text(256, "Inclusive upper bound in the same dated/time-only form as startTimestamp. Mixed forms and overnight time-only ranges are rejected.");
            p["relativeWindow"] = Text(256, "today or last <positive integer><m|h|d>, at most 365 elapsed days. Exclusive of absolute bounds; frozen at creation; does not select historical files.");
            if (name != "start_log_monitor")
            {
                p["includeStatistics"] = Flag(); p["enableHandoff"] = Flag();
            }
            if (name == "count_logs")
                p["bucketSize"] = Choice("none", "minute", "hour", "day");
            if (name == "search_logs")
            {
                p["maxFiles"] = Number(1, 200);
                foreach (var key in new[] { "maxHitsPerFile", "maxTotalHits", "maxQueryHits" }) p[key] = Number();
                p["includeContextBefore"] = Number(0); p["includeContextAfter"] = Number(0);
            }
        }
        if (name is "continue_log_search" or "continue_log_count" or "poll_log_monitor" or "stop_log_monitor")
        {
            p["cursor"] = Text(1024, "Opaque returned cursor. Lost continuity requires reporting the gap; never restart automatically.");
            required.Add("cursor");
            if (name.StartsWith("continue_", StringComparison.Ordinal)) p["includeStatistics"] = Flag();
        }
        if (name == "poll_log_monitor")
        {
            p["maxFiles"] = Number(1, 100); p["maxLines"] = Number();
        }
        if (name == "start_log_monitor_from_snapshot")
        {
            p["handoffCursor"] = Text(1024, "Verified snapshot boundary returned after enableHandoff=true. No fallback scan.");
            required.Add("handoffCursor");
        }
        if (name is "start_log_monitor" or "start_log_monitor_from_snapshot")
            p["observeForMilliseconds"] = Number(1, 86_400_000);
        if (name == "read_log_lines")
        {
            p["fileId"] = Text(256, "Required configured logFile ID from list_log_tree."); required.Add("fileId");
            p["anchor"] = Choice("start", "end");
            p["offset"] = Number(0, description: "Physical lines to skip from the anchor; default 0. End offsets skip newest lines first.");
            p["count"] = Number(description: "Physical lines; omitted uses the server default. Lines are returned chronologically.");
            p["dateOffsetDays"] = Number(0);
        }
        if (name == "list_log_tree")
        {
            p["rootGroupId"] = Text(256, "Configured folder/dashboard ID; omit for root groups.");
            p["maxDepth"] = Number(0, 20, "Default 0: shallow discovery. Query boundary groups when depthTruncated.");
            p["maxNodes"] = Number(1, 500); p["startIndex"] = Number(0);
            p["expectedCatalogRevision"] = Text(256, "Repeat the first page's catalogRevision with subsequent startIndex pages.");
        }
        if (name is not ("list_log_tree" or "server_status")) p["timeoutMilliseconds"] = Number();
        return Object(p, required.Select(n => n!.GetValue<string>()).ToArray());
    }

    private static JsonObject Object(JsonObject properties, params string[] required) => new()
    {
        ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false,
        ["required"] = new JsonArray(required.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray())
    };
    private static JsonObject Text(int? maximum = null, string? description = null, int minimum = 1)
    {
        var schema = new JsonObject { ["type"] = "string", ["minLength"] = minimum };
        if (maximum != null) schema["maxLength"] = maximum.Value;
        if (description != null) schema["description"] = description;
        return schema;
    }
    private static JsonObject Number(int minimum = 1, int maximum = int.MaxValue, string? description = null)
    {
        var schema = new JsonObject { ["type"] = "integer", ["minimum"] = minimum, ["maximum"] = maximum };
        if (description != null) schema["description"] = description;
        return schema;
    }
    private static JsonObject Flag() => new() { ["type"] = "boolean" };
    private static JsonObject Choice(params string[] values) => new()
    {
        ["type"] = "string", ["enum"] = new JsonArray(values.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray())
    };
}
