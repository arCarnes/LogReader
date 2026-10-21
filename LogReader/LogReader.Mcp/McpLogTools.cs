namespace LogReader.Mcp;

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

public sealed class McpLogTools
{
    private const string DateOffsetDescription =
        "Days before the captured server-local date: 0 uses the base path; 1 selects yesterday's configured rolling-pattern file. Independent of line timestamp filters; windows do not select historical files.";
    private const string StartTimestampDescription =
        "Inclusive line filter: ISO-8601, yyyy-MM-dd HH:mm[:ss[.fffffff]], or HH:mm[:ss[.fffffff]]. Missing/unrecognized log timestamps are excluded. Dated bounds exclude time-only log timestamps; dates without offsets use server-local time.";
    private const string EndTimestampDescription =
        "Inclusive upper line filter; use the same dated/time-only form as startTimestamp. Time-only bounds compare clock time across dates; mixed forms and overnight ranges are rejected.";
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions(includeStatistics: false);
    private static readonly JsonSerializerOptions StatisticsSerializerOptions = CreateSerializerOptions(includeStatistics: true);
    private static readonly AIJsonSchemaCreateOptions SchemaOptions = new()
    {
        TransformSchemaNode = McpResponseJsonPolicy.TransformSchema
    };

    private readonly ILogQueryBackend _backend;

    public McpLogTools(ILogQueryBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public static McpServerPrimitiveCollection<McpServerTool> CreateToolCollection(ILogQueryBackend backend)
    {
        var tools = new McpLogTools(backend);
        McpServerPrimitiveCollection<McpServerTool> collection =
        [
            CreateTool(
                (Func<string?, int, int, int, CancellationToken, Task<LogOperationEnvelope<ConfiguredLogTreeResult>>>)tools.ListLogTreeAsync,
                "list_log_tree",
                "Discover configured folder/dashboard/logFile IDs. Prefer shallow discovery: start with maxDepth=0 when the relevant branch is unknown, then explore likely branches using rootGroupId and a small maxDepth. Increase depth when useful. Select targets that cover the user's requested scope, and resolve ambiguous names using tree paths. Use IDs as selectors; files may appear in multiple dashboards. Follow nextStartIndex, compare revisions, and query boundary groups when depthTruncated. Physical paths are omitted.",
                openWorld: false),
            CreateQueryTool<LogSearchResult>(
                tools, nameof(SearchLogsAsync),
                "search_logs",
                "Find examples; use count_logs for totals. samples adds requested context (default zero), matchesOnly keeps hit lines, countsOnly omits text. Follow nextCursor; append new hits, replace cumulative totals, and avoid replay duplicates. Text modes stop at the query hit cap: query_hit_limit is terminal and incomplete."),
            CreateQueryTool<LogCountResult>(
                tools, nameof(CountLogsAsync),
                "count_logs",
                "Count matching lines and occurrences over configured targets, with optional minute/hour/day buckets. Follow nextCursor; results replace cumulative totals. Check isComplete; changed-file counts are observed counts. No log text."),
            CreateQueryTool<LogReadLinesResult>(
                tools, nameof(ReadLogLinesAsync),
                "read_log_lines",
                "Read a bounded one-based line range from a configured file ID. Advance with actualEndLine + 1 when present. Text is normalized and character-bounded; inspect file.error."),
            CreateQueryTool<LogReadTailResult>(
                tools, nameof(ReadLogTailAsync),
                "read_log_tail",
                "Read a file's end or poll append events with a process-scoped cursor. Optional literal/regex filters return matches and skipped counts; repeat the filter. Idle polls omit file and nextCursor: reuse the submitted cursor. Restart invalidates cursors; rotation/truncation is explicit."),
            CreateTool(
                (Func<McpServer, CancellationToken, Task<LogOperationEnvelope<McpLogServerStatus>>>)tools.GetServerStatusAsync,
                "server_status",
                "Report catalog/backend readiness, effective caps/defaults and indexed-cache usage. Individual configured files may still be unreadable. Continuation limits are capacities, not current usage.",
                openWorld: false)
        ];
        return collection;
    }

    public Task<LogOperationEnvelope<ConfiguredLogTreeResult>> ListLogTreeAsync(
        [Description("Configured folder/dashboard root ID; omit for the whole tree.")] string? rootGroupId = null,
        [Description("Root-relative descendant depth, 0–20; default 20. Use 0 for roots only and 1 for roots plus immediate children; increase depth when useful. For depthTruncated, query boundary groups by rootGroupId to reach deeper descendants.")] int maxDepth = ConfiguredLogLimits.DefaultTreeMaxDepth,
        [Description("Page nodes, 1–500; default 500.")] int maxNodes = ConfiguredLogLimits.DefaultTreeMaxNodes,
        [Description("Zero-based nextStartIndex; repeat rootGroupId/maxDepth. Compare returned catalogRevision across pages; on change discard prior discovery and restart at 0. The server does not enforce an expected revision.")] int startIndex = 0,
        CancellationToken cancellationToken = default)
        => _backend.ListLogTreeAsync(
            new ConfiguredLogTreeRequest(rootGroupId, maxDepth, maxNodes, startIndex),
            cancellationToken);

    public async Task<CallToolResult> SearchLogsAsync(
        [Description("Typed configured targets: {kind: folder|dashboard|logFile, id}.")] IReadOnlyList<ConfiguredLogTarget> targets,
        [Description("Nonempty literal text or regex pattern; server_status reports the effective character limit.")] string query,
        [Description(".NET regex with a 250 ms match timeout.")] bool useRegex = false,
        [Description("Ordinal case-sensitive matching; default false.")] bool caseSensitive = false,
        [Description("samples: hit excerpts with context; matchesOnly: hit lines; countsOnly: counts without text.")] string resultMode = "samples",
        [Description(McpResponseJsonPolicy.SearchCursorDescription)] string? cursor = null,
        [Description(DateOffsetDescription)] int dateOffsetDays = 0,
        [Description(StartTimestampDescription)] string? startTimestamp = null,
        [Description(EndTimestampDescription)] string? endTimestamp = null,
        [Description("File page limit; may lower the server maximum.")] int? maxFiles = null,
        [Description("Per-file hit limit for this response; may lower the server maximum.")] int? maxHitsPerFile = null,
        [Description("Total hit limit for this response; may lower the server maximum.")] int? maxTotalHits = null,
        [Description("Context lines before each hit; default 0. server_status reports the maximum per side.")] int includeContextBefore = 0,
        [Description("Context lines after each hit; default 0. server_status reports the maximum per side.")] int includeContextAfter = 0,
        [Description("Positive request timeout in ms; may lower the effective deadline from server_status.")] int? timeoutMilliseconds = null,
        [Description("Include current-page performance counters; default false.")] bool includeStatistics = false,
        [Description("Query-wide text hit cap; may lower the effective maximum (normally 10,000; server_status). Repeat unchanged with cursors. countsOnly is unaffected.")] int? maxQueryHits = null,
        [Description("shared (default): table and file references; inline: per-file arrays.")] string provenanceMode = "shared",
        CancellationToken cancellationToken = default)
    {
        if (provenanceMode is not ("shared" or "inline"))
            return McpResponseProjector.InvalidMode("provenanceMode", "shared, inline");
        var response = await _backend.SearchLogsAsync(
            new LogSearchQuery
            {
                Targets = targets,
                Query = query,
                UseRegex = useRegex,
                CaseSensitive = caseSensitive,
                ResultMode = resultMode,
                Cursor = cursor,
                DateOffsetDays = dateOffsetDays,
                StartTimestamp = startTimestamp,
                EndTimestamp = endTimestamp,
                MaxFiles = maxFiles,
                MaxHitsPerFile = maxHitsPerFile,
                MaxTotalHits = maxTotalHits,
                MaxQueryHits = maxQueryHits,
                IncludeContextBefore = includeContextBefore,
                IncludeContextAfter = includeContextAfter,
                TimeoutMilliseconds = timeoutMilliseconds
            },
            cancellationToken).ConfigureAwait(false);
        return SerializeResponse(response, includeStatistics, provenanceMode: provenanceMode);
    }

    public async Task<CallToolResult> CountLogsAsync(
        [Description("Typed configured targets: {kind: folder|dashboard|logFile, id}.")] IReadOnlyList<ConfiguredLogTarget> targets,
        [Description("Nonempty literal text or regex pattern; server_status reports the effective character limit.")] string query,
        [Description(".NET regex with a 250 ms match timeout.")] bool useRegex = false,
        [Description("Ordinal case-sensitive matching; default false.")] bool caseSensitive = false,
        [Description(DateOffsetDescription)] int dateOffsetDays = 0,
        [Description(StartTimestampDescription)] string? startTimestamp = null,
        [Description(EndTimestampDescription)] string? endTimestamp = null,
        [Description("today: server-local midnight through the captured instant; last <positive integer><m|h|d>, at most 365 days; d means 24 elapsed hours. Exclusive of absolute bounds. Frozen across continuations; selects lines, not historical files.")] string? relativeWindow = null,
        [Description("none (default), minute, hour, or day; at most 1,000 buckets. Bucketing requires both time bounds or relativeWindow; none needs neither. Time-only ranges allow minute/hour, not day. bucketMode selects the format.")] string bucketSize = "none",
        [Description("Positive request timeout in ms; may lower the effective deadline from server_status.")] int? timeoutMilliseconds = null,
        [Description("Include performance counters for this call; default false.")] bool includeStatistics = false,
        [Description(McpResponseJsonPolicy.CountCursorDescription)] string? cursor = null,
        [Description("sparse (default): indexed counts and boundary grid; dense: timestamped buckets.")] string bucketMode = "sparse",
        [Description("shared (default): table and file references; inline: per-file arrays.")] string provenanceMode = "shared",
        CancellationToken cancellationToken = default)
    {
        if (bucketMode is not ("sparse" or "dense"))
            return McpResponseProjector.InvalidMode("bucketMode", "sparse, dense");
        if (provenanceMode is not ("shared" or "inline"))
            return McpResponseProjector.InvalidMode("provenanceMode", "shared, inline");
        var response = await _backend.CountLogsAsync(
            new LogCountQuery
            {
                Targets = targets,
                Query = query,
                UseRegex = useRegex,
                CaseSensitive = caseSensitive,
                DateOffsetDays = dateOffsetDays,
                StartTimestamp = startTimestamp,
                EndTimestamp = endTimestamp,
                RelativeWindow = relativeWindow,
                BucketSize = bucketSize,
                Cursor = cursor,
                TimeoutMilliseconds = timeoutMilliseconds
            },
            cancellationToken).ConfigureAwait(false);
        return SerializeResponse(response, includeStatistics, bucketMode, provenanceMode);
    }

    public async Task<CallToolResult> ReadLogLinesAsync(
        [Description("Configured logFile ID from list_log_tree.")] string fileId,
        [Description("One-based first line number.")] int startLine = 1,
        [Description("Positive line count; omitted uses the server default read count (normally 200). server_status reports the effective default and maximum.")] int? count = null,
        [Description(DateOffsetDescription)] int dateOffsetDays = 0,
        [Description("Positive request timeout in ms; may lower the effective deadline from server_status.")] int? timeoutMilliseconds = null,
        [Description("shared (default): table and file references; inline: per-file arrays.")] string provenanceMode = "shared",
        CancellationToken cancellationToken = default)
    {
        if (provenanceMode is not ("shared" or "inline"))
            return McpResponseProjector.InvalidMode("provenanceMode", "shared, inline");
        var response = await _backend.ReadLogLinesAsync(
            new LogReadLinesQuery
            {
                FileId = fileId,
                StartLine = startLine,
                Count = count,
                DateOffsetDays = dateOffsetDays,
                TimeoutMilliseconds = timeoutMilliseconds
            },
            cancellationToken).ConfigureAwait(false);
        return SerializeResponse(response, includeStatistics: false, provenanceMode: provenanceMode);
    }

    public async Task<CallToolResult> ReadLogTailAsync(
        [Description("Configured logFile ID from list_log_tree.")] string fileId,
        [Description("Omit to examine the latest maxLines physical lines. Resume to examine new lines, including nonmatches. Repeat query/useRegex/caseSensitive unchanged. On idle reuse the submitted cursor; invalid/prior-process cursor requires a fresh call.")] string? cursor = null,
        [Description("Positive physical lines to examine; initial/reset reads inspect only the latest lines. Omitted uses the server default read count (normally 200); server_status reports the effective default and maximum.")] int? maxLines = null,
        [Description(DateOffsetDescription)] int dateOffsetDays = 0,
        [Description("Positive request timeout in ms; may lower the effective deadline from server_status.")] int? timeoutMilliseconds = null,
        [Description("Nonempty literal/regex filter; repeat unchanged with a cursor.")] string? query = null,
        [Description(".NET regex with a 250 ms match timeout; true requires query and must be repeated unchanged with a cursor.")] bool useRegex = false,
        [Description("Ordinal case-sensitive matching; default false. true requires query and must be repeated unchanged with a cursor.")] bool caseSensitive = false,
        [Description("shared (default): table and file references; inline: per-file arrays.")] string provenanceMode = "shared",
        CancellationToken cancellationToken = default)
    {
        if (provenanceMode is not ("shared" or "inline"))
            return McpResponseProjector.InvalidMode("provenanceMode", "shared, inline");
        var response = await _backend.ReadLogTailAsync(
            new LogReadTailQuery
            {
                FileId = fileId,
                Cursor = cursor,
                Query = query,
                UseRegex = useRegex,
                CaseSensitive = caseSensitive,
                MaxLines = maxLines,
                DateOffsetDays = dateOffsetDays,
                TimeoutMilliseconds = timeoutMilliseconds
            },
            cancellationToken).ConfigureAwait(false);
        return SerializeResponse(response, includeStatistics: false, provenanceMode: provenanceMode);
    }

    public async Task<LogOperationEnvelope<McpLogServerStatus>> GetServerStatusAsync(
        McpServer? server,
        CancellationToken cancellationToken = default)
    {
        var response = await _backend.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return new LogOperationEnvelope<McpLogServerStatus>(
            response.SchemaVersion,
            response.RequestId,
            response.CatalogRevision,
            response.IsPartial,
            response.IsTruncated,
            response.TruncationReasons,
            response.Errors,
            response.Result is null
                ? null
                : new McpLogServerStatus(
                    "stdio",
                    "tools_only",
                    server?.NegotiatedProtocolVersion ?? "not_negotiated",
                    response.Result));
    }

    private static McpServerTool CreateTool(
        Delegate implementation,
        string name,
        string description,
        bool openWorld)
    {
        var tool = McpServerTool.Create(
            implementation,
            CreateToolOptions(name, description, openWorld));
        ConstrainInputSchema(tool);
        return tool;
    }

    private static McpServerTool CreateQueryTool<T>(McpLogTools tools, string methodName, string name, string description)
    {
        var options = CreateToolOptions(name, description, openWorld: true);
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
            Idempotent = true,
            OpenWorld = openWorld,
            UseStructuredContent = true,
            SerializerOptions = SerializerOptions,
            SchemaCreateOptions = SchemaOptions
        };

    private static CallToolResult SerializeResponse<T>(LogOperationEnvelope<T> response, bool includeStatistics,
        string? bucketMode = null, string provenanceMode = "shared")
        => McpResponseProjector.Serialize(response,
            includeStatistics ? StatisticsSerializerOptions : SerializerOptions, bucketMode, provenanceMode);

    private static JsonSerializerOptions CreateSerializerOptions(bool includeStatistics)
    {
        var options = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions);
        options.Converters.Insert(0, new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.TypeInfoResolver = (options.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver())
            .WithAddedModifier(typeInfo => McpResponseJsonPolicy.Apply(typeInfo, includeStatistics));
        options.MakeReadOnly();
        return options;
    }
}

public sealed record McpLogServerStatus(
    string Transport,
    string PrimitivePolicy,
    string ProtocolVersion,
    LogQueryStatus QueryBackend);
