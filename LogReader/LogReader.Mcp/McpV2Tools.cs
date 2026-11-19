namespace LogReader.Mcp;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Json.Schema;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

internal static class McpV2Tools
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new IntegralInt32Converter() }
    };
    private const string Snapshot = "Each file's extent is captured when first opened. Bytes beyond it are excluded. Growth during the scan can make captured contents unverifiable. Finish the existing continuation and report the evidence with its caveats. Never restart or start observation merely because a file grows.";

    internal static McpServerPrimitiveCollection<McpServerTool> Create(ILogQueryBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        return
        [
            Create<ConfiguredLogTreeResult, McpTreeRequest>("list_log_tree",
                "Discover configured folder/dashboard/logFile IDs. Start shallow (maxDepth defaults to 0), then explore relevant branches. Names may repeat; select by ID. Follow nextStartIndex with the first catalogRevision as expectedCatalogRevision; query boundary groups when depthTruncated.",
                (r, _, ct) => backend.ListLogTreeAsync(new(r.RootGroupId, r.MaxDepth, r.MaxNodes, r.StartIndex, r.ExpectedCatalogRevision), ct)),
            Create<LogSearchResult, McpSnapshotRequest>("search_logs",
                "Find evidence. Required targets: [{kind: folder|dashboard|logFile, id: discovered ID}]. Supply query text or time criteria. Returns samples with context, not authoritative totals; use count_logs for totals. " + Snapshot,
                (r, _, ct) => backend.SearchLogsAsync(r.Search(), ct), r => r.IncludeStatistics),
            Create<LogSearchResult, McpContinueRequest>("continue_log_search",
                "Continue the existing search using only its returned cursor and execution controls. Append new hits, replace cumulative totals, and avoid duplicates on replay. Lost continuity is reported; never silently restart.",
                (r, _, ct) => backend.ContinueLogSearchAsync(r.Cursor, r.TimeoutMilliseconds, ct), r => r.IncludeStatistics),
            Create<LogCountResult, McpSnapshotRequest>("count_logs",
                "Count matching lines and occurrences. Required targets: [{kind: folder|dashboard|logFile, id: discovered ID}]. Supply query text or time criteria. Optional minute/hour/day buckets require relativeWindow or both absolute bounds; day buckets require dated bounds. " + Snapshot,
                (r, _, ct) => backend.CountLogsAsync(r.Count(), ct), r => r.IncludeStatistics),
            Create<LogCountResult, McpContinueRequest>("continue_log_count",
                "Continue the existing count using only its returned cursor and execution controls. Replace cumulative totals, buckets and files. Lost continuity is reported; never silently restart.",
                (r, _, ct) => backend.ContinueLogCountAsync(r.Cursor, r.TimeoutMilliseconds, ct), r => r.IncludeStatistics),
            Create<LogReadLinesResult, McpReadRequest>("read_log_lines",
                "Read physical lines from a required configured fileId. anchor=start (default) skips offset lines from line 1; anchor=end skips offset newest lines and returns the preceding count lines. Returns chronological lines, including untimestamped text. One bounded snapshot; never follows updates.",
                (r, _, ct) => backend.ReadLogLinesAsync(new() { FileId = r.FileId, Anchor = r.Anchor, Offset = r.Offset,
                    Count = r.Count, DateOffsetDays = r.DateOffsetDays, TimeoutMilliseconds = r.TimeoutMilliseconds }, ct)),
            Create<LogMonitorResult, McpSnapshotRequest>("start_log_monitor",
                "Only for explicitly requested observation over time. Required configured targets; captures current ends without scanning history. One rolling date, at most 100 physical files. Poll with poll_log_monitor. Growth is expected; never use observation to repair an ordinary search.",
                (r, _, ct) => backend.MonitorLogsAsync(r.Monitor(), ct)),
            Create<LogMonitorResult, McpHandoffRequest>("start_log_monitor_from_snapshot",
                "Only for explicitly requested observation after a snapshot. Requires the handoffCursor from enableHandoff=true and fully evaluated verified boundaries. Copies the frozen predicate/window; never fills gaps by rescanning.",
                (r, _, ct) => backend.MonitorLogsAsync(new() { HandoffCursor = r.HandoffCursor,
                    ObserveForMilliseconds = r.ObserveForMilliseconds, TimeoutMilliseconds = r.TimeoutMilliseconds }, ct)),
            Create<LogMonitorResult, McpPollRequest>("poll_log_monitor",
                "Poll a required observation cursor; does not wait or scan history. Empty events do not establish idle: inspect isScopeChecked, isCaughtUp and backlogFileCount. Apply appended/updated/removed events by file/generation/byteOffset, once per committed batch. Expired or stopped observation does not authorize restart.",
                (r, _, ct) => backend.MonitorLogsAsync(new() { Cursor = r.Cursor, MaxFiles = r.MaxFiles,
                    MaxLines = r.MaxLines, TimeoutMilliseconds = r.TimeoutMilliseconds }, ct)),
            Create<LogMonitorResult, McpStopRequest>("stop_log_monitor",
                "Release an explicitly requested observation using its current cursor. Does not start another observation.",
                (r, _, ct) => backend.MonitorLogsAsync(new() { Cursor = r.Cursor, Release = true, TimeoutMilliseconds = r.TimeoutMilliseconds }, ct)),
            Create<McpLogServerStatus, EmptyRequest>("server_status",
                "Report catalog readiness, effective limits and indexed-cache usage. Limits are capacities, not usage. Readiness does not guarantee each configured file is readable.",
                (_, server, ct) => new McpLogTools(backend).GetServerStatusAsync(server, ct))
        ];
    }

    private static McpServerTool Create<TData, TRequest>(string name, string description,
        Func<TRequest, McpServer?, CancellationToken, Task<LogOperationEnvelope<TData>>> invoke,
        Func<TRequest, bool>? statistics = null)
        => new ContractTool(name, description, McpV2Response.Schema<TData>(), async (args, server, ct) =>
        {
            TRequest request;
            try { request = args.Deserialize<TRequest>(JsonOptions)!; }
            catch (JsonException)
            {
                return McpV2Response.Failure(new McpIssue("invalid_argument_type", "argument",
                    "Use the documented JSON types and representable integer values.", "correct_arguments"));
            }
            var response = await invoke(request, server, ct).ConfigureAwait(false);
            return McpV2Response.Project(response, statistics?.Invoke(request) == true);
        });

    private sealed record EmptyRequest;

    // JSON Schema integers include integral decimals/exponents. Accept those without rounding
    // so a schema-valid 1.0 or 1e0 does not fail DTO binding after validation.
    private sealed class IntegralInt32Converter : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TryGetInt32(out var integer)) return integer;
            if (reader.TryGetDecimal(out var number) && number == decimal.Truncate(number) && number >= int.MinValue && number <= int.MaxValue)
                return (int)number;
            throw new JsonException("A representable integer is required.");
        }

        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }

    private sealed class ContractTool : McpServerTool
    {
        private readonly JsonElement _input;
        private readonly JsonSchema _validator;
        private readonly Func<JsonElement, McpServer?, CancellationToken, Task<CallToolResult>> _invoke;

        internal ContractTool(string name, string description, JsonElement output,
            Func<JsonElement, McpServer?, CancellationToken, Task<CallToolResult>> invoke)
        {
            _input = JsonSerializer.SerializeToElement(McpInputSchemas.For(name), JsonOptions);
            // All references in authored input schemas are local; no fetch callback is configured.
            _validator = McpV2Response.CompileSchema(_input);
            _invoke = invoke;
            ProtocolTool = new Tool
            {
                Name = name, Title = name.Replace('_', ' '), Description = description, InputSchema = _input,
                OutputSchema = output, Annotations = new ToolAnnotations
                {
                    ReadOnlyHint = true, DestructiveHint = false,
                    IdempotentHint = name is not ("start_log_monitor" or "start_log_monitor_from_snapshot" or "poll_log_monitor" or "stop_log_monitor"),
                    OpenWorldHint = name is not ("list_log_tree" or "server_status")
                }
            };
        }

        public override Tool ProtocolTool { get; }
        public override IReadOnlyList<object> Metadata => [];

        public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var args = JsonSerializer.SerializeToElement(request.Params?.Arguments ?? new Dictionary<string, JsonElement>(), JsonOptions);
                var evaluation = _validator.Evaluate(args, new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical });
                if (!evaluation.IsValid)
                    return McpV2Response.Failure(DescribeErrors(evaluation, args).Distinct().Take(8).ToArray());
                return await _invoke(args, request.Server, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                return McpV2Response.Failure(new McpIssue("internal_error", "request",
                    "The operation could not complete. Report the failure; do not restart an investigation automatically.", "report_failure"));
            }
        }

        private IEnumerable<McpIssue> DescribeErrors(EvaluationResults result, JsonElement args)
        {
            if (result.Errors is { Count: > 0 })
            {
                var path = result.InstanceLocation.ToString();
                var schema = _input;
                var value = args;
                var unknown = false;
                foreach (var part in path.Split('/').Skip(1).Select(s => s.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)))
                {
                    if (value.ValueKind == JsonValueKind.Array)
                    {
                        value = value[int.Parse(part, System.Globalization.CultureInfo.InvariantCulture)];
                        schema = schema.GetProperty("items");
                    }
                    else
                    {
                        if (!schema.GetProperty("properties").TryGetProperty(part, out var propertySchema))
                        {
                            unknown = true;
                            break;
                        }
                        value = value.GetProperty(part);
                        schema = propertySchema;
                    }
                }
                if (unknown)
                {
                    var boundedPath = new string(path.Take(2048).Select(c => char.IsControl(c) ? '?' : c).ToArray());
                    yield return new("unexpected_argument", "argument", "Remove this field; only the published argument names are accepted.", "correct_arguments", boundedPath);
                    yield break;
                }
                foreach (var keyword in result.Errors.Keys.Order(StringComparer.Ordinal))
                {
                    if (keyword == "required")
                    {
                        foreach (var missing in schema.GetProperty("required").EnumerateArray().Select(s => s.GetString()!).Where(s => !value.TryGetProperty(s, out _)))
                            yield return new("required_argument", "argument", missing == "targets"
                                ? "Supply nonempty targets from list_log_tree, for example [{\"kind\":\"logFile\",\"id\":\"<discovered-id>\"}]."
                                : $"Supply the required {missing} field.", "correct_arguments", path + "/" + Escape(missing));
                    }
                    else if (keyword == "additionalProperties")
                    {
                        foreach (var extra in value.EnumerateObject().Where(p => !schema.GetProperty("properties").TryGetProperty(p.Name, out _)).OrderBy(p => p.Name, StringComparer.Ordinal))
                            yield return new("unexpected_argument", "argument", "Remove this field; only the published argument names are accepted.",
                                "correct_arguments", path + "/" + Escape(extra.Name));
                    }
                    else
                    {
                        var expected = keyword switch
                        {
                            "enum" => "Use one of: " + string.Join(", ", schema.GetProperty("enum").EnumerateArray().Select(s => s.GetString())) + ".",
                            "type" => "Use JSON type " + schema.GetProperty("type").GetString() + ".",
                            "minItems" => "Supply at least one configured target.",
                            "minLength" => "Supply a nonempty string.",
                            "maxLength" => "Use at most " + schema.GetProperty("maxLength").GetInt32() + " characters.",
                            "minimum" => "Use a value of at least " + schema.GetProperty("minimum").GetInt32() + ".",
                            "maximum" => "Use a value no greater than " + schema.GetProperty("maximum").GetInt32() + ".",
                            _ => "Use the documented argument shape."
                        };
                        yield return new("invalid_argument", "argument", expected, "correct_arguments", path);
                    }
                }
            }
            foreach (var child in result.Details ?? [])
                if (!child.IsValid)
                    foreach (var issue in DescribeErrors(child, args)) yield return issue;
        }

        private static string Escape(string name)
        {
            var bounded = new string(name.Take(256).Select(c => char.IsControl(c) ? '?' : c).ToArray());
            return bounded.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
        }
    }
}
