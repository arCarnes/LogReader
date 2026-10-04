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
                "Discover configured folder/dashboard/logFile IDs and tree paths. Bounded, paginated; physical paths are omitted.",
                openWorld: false),
            CreateQueryTool<LogSearchResult>(
                tools, nameof(SearchLogsAsync),
                "search_logs",
                "Find example hits and merged excerpts; samples adds context, matchesOnly keeps hit lines, countsOnly omits text. Follow nextCursor. Text modes stop at 10,000 query hits: query_hit_limit is terminal and incomplete. Clean zero-hit files are summarized by pageOmittedZeroHitFileCount."),
            CreateQueryTool<LogCountResult>(
                tools, nameof(CountLogsAsync),
                "count_logs",
                "Count matching lines and occurrences over configured targets, with optional minute/hour/day buckets. Follow nextCursor; results replace cumulative totals. Check isComplete; changed-file counts are observed counts. No log text."),
            CreateQueryTool<LogReadLinesResult>(
                tools, nameof(ReadLogLinesAsync),
                "read_log_lines",
                "Read a bounded one-based line range from a configured file ID. Text is normalized and character-bounded."),
            CreateQueryTool<LogReadTailResult>(
                tools, nameof(ReadLogTailAsync),
                "read_log_tail",
                "Read a file's end or poll append events with a process-scoped cursor. Optional literal/regex filters return matches and skipped counts; repeat the filter. Idle polls omit file and nextCursor: reuse the submitted cursor. Restart invalidates cursors; rotation/truncation is explicit."),
            CreateTool(
                (Func<McpServer, CancellationToken, Task<LogOperationEnvelope<McpLogServerStatus>>>)tools.GetServerStatusAsync,
                "server_status",
                "Report readiness, effective limits, and bounded process-cache usage.",
                openWorld: false)
        ];
        return collection;
    }

    public Task<LogOperationEnvelope<ConfiguredLogTreeResult>> ListLogTreeAsync(
        [Description("Configured folder/dashboard root ID; omit for the whole tree.")] string? rootGroupId = null,
        [Description("Maximum descendant depth; may lower the server limit.")] int maxDepth = ConfiguredLogLimits.DefaultTreeMaxDepth,
        [Description("Maximum page nodes; may lower the server limit.")] int maxNodes = ConfiguredLogLimits.DefaultTreeMaxNodes,
        [Description("Zero-based nextStartIndex; requires the same catalog revision.")] int startIndex = 0,
        CancellationToken cancellationToken = default)
        => _backend.ListLogTreeAsync(
            new ConfiguredLogTreeRequest(rootGroupId, maxDepth, maxNodes, startIndex),
            cancellationToken);

    public async Task<CallToolResult> SearchLogsAsync(
        [Description("Typed configured targets: {kind: folder|dashboard|logFile, id}.")] IReadOnlyList<ConfiguredLogTarget> targets,
        [Description("Literal text or regex pattern.")] string query,
        [Description(".NET regex with a 250 ms match timeout.")] bool useRegex = false,
        [Description("Ordinal case-sensitive matching; default false.")] bool caseSensitive = false,
        [Description("samples: hit excerpts with context; matchesOnly: hit lines; countsOnly: counts without text.")] string resultMode = "samples",
        [Description("nextCursor resumes the same query. Presentation modes, statistics and timeout may change. Expires after 15 minutes idle or restart.")] string? cursor = null,
        [Description("Non-negative date offset; zero uses the configured base path.")] int dateOffsetDays = 0,
        [Description("Inclusive lower bound: ISO-8601, yyyy-MM-dd HH:mm[:ss[.fffffff]], or HH:mm[:ss[.fffffff]].")] string? startTimestamp = null,
        [Description("Inclusive upper bound; same dated/time-only form as startTimestamp.")] string? endTimestamp = null,
        [Description("File page limit; may lower the server maximum.")] int? maxFiles = null,
        [Description("Per-file hit limit for this response; may lower the server maximum.")] int? maxHitsPerFile = null,
        [Description("Total hit limit for this response; may lower the server maximum.")] int? maxTotalHits = null,
        [Description("Bounded context lines before each hit.")] int includeContextBefore = 0,
        [Description("Bounded context lines after each hit.")] int includeContextAfter = 0,
        [Description("Request timeout in ms; may lower the server deadline.")] int? timeoutMilliseconds = null,
        [Description("Include current-page performance counters; default false.")] bool includeStatistics = false,
        [Description("Query-wide text hit cap, at most 10,000; repeat unchanged with cursors. countsOnly is unaffected.")] int? maxQueryHits = null,
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
        [Description("Literal text or regex pattern.")] string query,
        [Description(".NET regex with a 250 ms match timeout.")] bool useRegex = false,
        [Description("Ordinal case-sensitive matching; default false.")] bool caseSensitive = false,
        [Description("Non-negative date offset; zero uses the configured base path.")] int dateOffsetDays = 0,
        [Description("Inclusive lower bound: ISO-8601, yyyy-MM-dd HH:mm[:ss[.fffffff]], or HH:mm[:ss[.fffffff]].")] string? startTimestamp = null,
        [Description("Inclusive upper bound; same dated/time-only form as startTimestamp.")] string? endTimestamp = null,
        [Description("Server-local today or last <positive integer><m|h|d>, at most 365 days. Excludes absolute bounds.")] string? relativeWindow = null,
        [Description("Time buckets: none, minute, hour, or day; at most 1,000. Requires both time bounds or relativeWindow. Sparse by default; bucketMode selects the format.")] string bucketSize = "none",
        [Description("Request timeout in ms; may lower the server deadline.")] int? timeoutMilliseconds = null,
        [Description("Include performance counters for this call; default false.")] bool includeStatistics = false,
        [Description("nextCursor resumes the same query and replaces cumulative counts. Presentation modes, statistics and timeout may change. Expires after 15 minutes idle or restart.")] string? cursor = null,
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
        [Description("Line count; bounded by server read limits.")] int? count = null,
        [Description("Non-negative date offset; zero uses the configured base path.")] int dateOffsetDays = 0,
        [Description("Request timeout in ms; may lower the server deadline.")] int? timeoutMilliseconds = null,
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
        [Description("Tail cursor; omit for current end. On idle, reuse the submitted cursor.")] string? cursor = null,
        [Description("Physical lines to examine; bounded by server read limits.")] int? maxLines = null,
        [Description("Non-negative date offset; zero uses the configured base path.")] int dateOffsetDays = 0,
        [Description("Request timeout in ms; may lower the server deadline.")] int? timeoutMilliseconds = null,
        [Description("Nonempty literal/regex filter; repeat unchanged with a cursor.")] string? query = null,
        [Description(".NET regex with a 250 ms match timeout.")] bool useRegex = false,
        [Description("Ordinal case-sensitive matching; default false.")] bool caseSensitive = false,
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
        => McpServerTool.Create(
            implementation,
            CreateToolOptions(name, description, openWorld));

    private static McpServerTool CreateQueryTool<T>(McpLogTools tools, string methodName, string name, string description)
    {
        var options = CreateToolOptions(name, description, openWorld: true);
        var schema = AIJsonUtilities.CreateJsonSchema(
            typeof(LogOperationEnvelope<T>), serializerOptions: StatisticsSerializerOptions, inferenceOptions: SchemaOptions);
        options.OutputSchema = McpResponseProjector.ShareProvenanceSchema(schema, StatisticsSerializerOptions);
        var tool = McpServerTool.Create(typeof(McpLogTools).GetMethod(methodName)!, tools, options);
        // The SDK builds method input properties separately; constrain the completed schema
        // while retaining the original string parameter bindings.
        var input = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!.AsObject();
        McpResponseJsonPolicy.ConstrainStringChoices(input["properties"]!.AsObject());
        tool.ProtocolTool.InputSchema = JsonSerializer.SerializeToElement(input, SerializerOptions);
        return tool;
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
