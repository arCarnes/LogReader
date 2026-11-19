namespace LogReader.Core.Tests;

using System.Text.Json;
using System.Text.Json.Nodes;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Mcp;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

public sealed partial class McpLogToolsTests
{
    // Exercise compact backend projections separately from the public v2 protocol tests.
    private static readonly JsonSerializerOptions SerializerOptions = McpLogTools.CreateSerializerOptions(false);
    private static readonly JsonSerializerOptions StatisticsSerializerOptions = McpLogTools.CreateSerializerOptions(true);
    private static readonly AIJsonSchemaCreateOptions SchemaOptions = new() { TransformSchemaNode = McpResponseJsonPolicy.TransformSchema };
    private static McpServerPrimitiveCollection<McpServerTool> CreateBackendProjectionTools(ILogQueryBackend backend)
    {
        var tools = new McpLogTools(backend);
        McpServerPrimitiveCollection<McpServerTool> collection =
        [
            CreateQueryTool<ConfiguredLogTreeResult>(
                tools, nameof(McpLogTools.ListLogTreeToolAsync), "list_log_tree",
                "Discover configured folder/dashboard/logFile IDs. Prefer shallow discovery: start with maxDepth=0 when the relevant branch is unknown, then explore likely branches using rootGroupId and a small maxDepth. Increase depth when useful. Select targets that cover the user's requested scope, and resolve ambiguous names using tree paths. Use IDs as selectors; files may appear in multiple dashboards. Follow nextStartIndex with expectedCatalogRevision from the first page; compare revisions, and query boundary groups when depthTruncated. Physical paths are omitted.",
                openWorld: false),
            CreateQueryTool<LogSearchResult>(
                tools, nameof(McpLogTools.SearchLogsAsync),
                "search_logs",
                "Find evidence with optional surrounding context (default zero); stop when it answers the question. Use count_logs for totals. samples is preferred; matchesOnly and countsOnly remain compatible legacy choices. Follow nextCursor; append new hits, replace cumulative totals, and avoid replay duplicates. Text modes stop at the query hit cap: query_hit_limit is terminal and incomplete. " + McpResponseJsonPolicy.SnapshotSearchDescription),
            CreateQueryTool<LogCountResult>(
                tools, nameof(McpLogTools.CountLogsAsync),
                "count_logs",
                "Count matching lines and occurrences over configured targets, with optional minute/hour/day buckets. Follow nextCursor; results replace cumulative totals. Check isComplete; growth-qualified counts remain observed counts, not a recount request. No log text. " + McpResponseJsonPolicy.SnapshotSearchDescription),
            CreateQueryTool<LogReadLinesResult>(
                tools, nameof(McpLogTools.ReadLogLinesAsync),
                "read_log_lines",
                "Read a bounded one-based line range from a configured file ID. Advance with actualEndLine + 1 when present. Text is normalized and character-bounded; inspect file.error. A successful bounded read needs no refresh solely because the file later grows."),
            CreateQueryTool<LogReadTailResult>(
                tools, nameof(McpLogTools.ReadLogTailAsync),
                "read_log_tail",
                "Read a file's end or poll append events with a process-scoped cursor. Optional literal/regex filters return matches and skipped counts; repeat the filter. Idle polls omit file and nextCursor: reuse the submitted cursor. Restart invalidates cursors; rotation/truncation is explicit. Ordinary growth is expected, not a generation change. Use the existing cursor for an already requested observation; do not start a historical search/count solely because of growth."),
            CreateQueryTool<LogMonitorResult>(
                tools, nameof(McpLogTools.MonitorLogsAsync), "monitor_logs",
                "Explicitly observe configured folder/dashboard/file targets from captured file ends. No history scan. One selected rolling date; at most 100 physical files. Poll with only the returned cursor and bounded controls. Empty matches do not establish idle: inspect isScopeChecked, isCaughtUp, backlogFileCount and per-file errors. Apply events by file/generation/byteOffset; updated replaces, removed deletes. Growth is expected. Replacement, truncation, encoding changes or detected incompatible edits stop the affected file. release=true ends observation. Process-local expiry and replay rules apply; never start monitoring merely to repair snapshot uncertainty."),
            CreateQueryTool<McpLogServerStatus>(
                tools, nameof(McpLogTools.ServerStatusToolAsync), "server_status",
                "Report catalog/backend readiness, effective caps/defaults and indexed-cache usage. Individual configured files may still be unreadable. Continuation limits are capacities, not current usage.",
                openWorld: false)
        ];
        return collection;
    }

    private static McpServerTool CreateQueryTool<T>(McpLogTools tools, string methodName, string name, string description, bool openWorld = true)
    {
        var options = CreateToolOptions(name, description, openWorld);
        var schema = AIJsonUtilities.CreateJsonSchema(
            typeof(LogOperationEnvelope<T>), serializerOptions: StatisticsSerializerOptions, inferenceOptions: SchemaOptions);
        options.OutputSchema = McpResponseProjector.ShareProvenanceSchema(schema, StatisticsSerializerOptions);
        var tool = McpServerTool.Create(typeof(McpLogTools).GetMethod(methodName)!, tools, options);
        // The SDK builds method input properties separately; constrain the completed schema
        // while retaining the original string parameter bindings.
        ConstrainInputSchema(tool);
        return tool;
    }

    private static void ConstrainInputSchema(McpServerTool tool)
    {
        var input = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!.AsObject();
        McpResponseJsonPolicy.ConstrainInputs(input, tool.ProtocolTool.Name);
        tool.ProtocolTool.InputSchema = JsonSerializer.SerializeToElement(input, SerializerOptions);
    }

    private static McpServerToolCreateOptions CreateToolOptions(string name, string description, bool openWorld)
        => new()
        {
            Name = name,
            Title = name.Replace('_', ' '),
            Description = description,
            ReadOnly = true,
            Destructive = false,
            Idempotent = name != "monitor_logs",
            OpenWorld = openWorld,
            UseStructuredContent = true,
            SerializerOptions = SerializerOptions,
            SchemaCreateOptions = SchemaOptions
        };

}
