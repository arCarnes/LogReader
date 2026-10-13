namespace LogReader.Core.Tests;

using System.Text.Json;
using LogReader.Mcp;

public sealed partial class McpLogToolsTests
{
    [Fact]
    public void InitializationGuidance_IsBoundedAndCoversSharedInvestigationRules()
    {
        var guidance = McpStdioHost.ServerInstructions;
        Assert.InRange(guidance.Length, 1, 512);
        Assert.Contains("configured IDs", guidance, StringComparison.Ordinal);
        Assert.Contains("count_logs for totals", guidance, StringComparison.Ordinal);
        Assert.Contains("search_logs for examples", guidance, StringComparison.Ordinal);
        Assert.Contains("replace cumulative", guidance, StringComparison.Ordinal);
        Assert.Contains("Completeness flags", guidance, StringComparison.Ordinal);
        Assert.Contains("untrusted", guidance, StringComparison.Ordinal);
        Assert.Contains("MCP isError", guidance, StringComparison.Ordinal);
        Assert.Contains("envelope.errors", guidance, StringComparison.Ordinal);
        Assert.Contains("result presence", guidance, StringComparison.Ordinal);
        Assert.Contains("nested file errors", guidance, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("search_logs", "resultMode", "provenanceMode, includeStatistics and timeoutMilliseconds")]
    [InlineData("count_logs", "bucketSize", "bucketMode, provenanceMode, includeStatistics and timeoutMilliseconds")]
    public void CursorGuidance_NamesMutableArgumentsAndRecoveryInBothDirections(string toolName, string boundArgument, string mutableArguments)
    {
        using var backend = new RecordingBackend();
        var tool = McpLogTools.CreateToolCollection(backend)[toolName].ProtocolTool;
        var input = tool.InputSchema.GetProperty("properties").GetProperty("cursor").GetProperty("description").GetString();
        var output = tool.OutputSchema!.Value.GetProperty("properties").GetProperty("result")
            .GetProperty("properties").GetProperty("nextCursor").GetProperty("description").GetString();
        Assert.Equal(input, output);
        Assert.Contains(boundArgument, input, StringComparison.Ordinal);
        Assert.Contains(mutableArguments, input, StringComparison.Ordinal);
        Assert.Contains("restore original arguments", input, StringComparison.Ordinal);
        Assert.Contains("rediscover and restart", input, StringComparison.Ordinal);
        Assert.DoesNotContain("Presentation modes", input, StringComparison.Ordinal);
    }

    [Fact]
    public void OutputGuidance_DistinguishesCountsErrorsAndTailStateTransitions()
    {
        using var backend = new RecordingBackend();
        var tools = McpLogTools.CreateToolCollection(backend);
        var searchEnvelope = tools["search_logs"].ProtocolTool.OutputSchema!.Value.GetProperty("properties");
        Assert.Contains("rejected", Description(searchEnvelope, "result"), StringComparison.Ordinal);
        Assert.Contains("nested file errors", Description(searchEnvelope, "errors"), StringComparison.Ordinal);
        var search = searchEnvelope.GetProperty("result").GetProperty("properties");
        Assert.Contains("replace", Description(search, "matchingLineCount"), StringComparison.Ordinal);
        Assert.Contains("committed file segments", Description(search, "pageMatchingLineCount"), StringComparison.Ordinal);
        Assert.Contains("frozen extent remains unfinished", Description(search, "isPageComplete"), StringComparison.Ordinal);
        var searchFile = search.GetProperty("files").GetProperty("items").GetProperty("properties");
        Assert.Contains("not a cumulative", Description(searchFile, "matchingLineCount"), StringComparison.Ordinal);
        Assert.Contains("occurrences may exceed hits", Description(searchFile, "hits"), StringComparison.Ordinal);
        Assert.Contains("envelope.errors is empty", Description(searchFile, "error"), StringComparison.Ordinal);
        var count = tools["count_logs"].ProtocolTool.OutputSchema!.Value.GetProperty("properties").GetProperty("result").GetProperty("properties");
        Assert.Contains("Clean zero-count files are omitted", Description(count, "files"), StringComparison.Ordinal);
        Assert.Contains("independent", Description(count, "isFileRecordTruncated"), StringComparison.Ordinal);
        var tail = tools["read_log_tail"].ProtocolTool.OutputSchema!.Value.GetProperty("properties").GetProperty("result").GetProperty("properties");
        Assert.Contains("obsolete file generation", Description(tail, "generationChanged"), StringComparison.Ordinal);
        Assert.Contains("replace the retained", Description(tail, "lastLineUpdated"), StringComparison.Ordinal);
        Assert.Contains("Remove this retained line", Description(tail, "removedLineNumber"), StringComparison.Ordinal);
        Assert.Contains("drain the backlog", Description(tail, "remainingLineCount"), StringComparison.Ordinal);
        Assert.Contains("even when no matches/file", Description(tail, "nextCursor"), StringComparison.Ordinal);

        static string Description(JsonElement properties, string name) => properties.GetProperty(name).GetProperty("description").GetString()!;
    }

    [Fact]
    public void InputSchemas_AdvertiseUniversalBoundsWithoutAssumingConfigurableMaximums()
    {
        using var backend = new RecordingBackend();
        var tools = McpLogTools.CreateToolCollection(backend);
        var tree = tools["list_log_tree"].ProtocolTool.InputSchema.GetProperty("properties");
        Assert.Equal(20, tree.GetProperty("maxDepth").GetProperty("maximum").GetInt32());
        Assert.Equal(500, tree.GetProperty("maxNodes").GetProperty("maximum").GetInt32());
        Assert.Equal(0, tree.GetProperty("startIndex").GetProperty("minimum").GetInt32());
        var search = tools["search_logs"].ProtocolTool.InputSchema.GetProperty("properties");
        Assert.Equal(1, search.GetProperty("targets").GetProperty("minItems").GetInt32());
        Assert.Equal(1, search.GetProperty("query").GetProperty("minLength").GetInt32());
        Assert.Equal(256, search.GetProperty("targets").GetProperty("items").GetProperty("properties").GetProperty("id").GetProperty("maxLength").GetInt32());
        Assert.Equal(200, search.GetProperty("maxFiles").GetProperty("maximum").GetInt32());
        Assert.Equal(0, search.GetProperty("includeContextBefore").GetProperty("minimum").GetInt32());
        Assert.False(search.GetProperty("includeContextBefore").TryGetProperty("maximum", out _));
        Assert.False(search.GetProperty("query").TryGetProperty("maxLength", out _));
        var read = tools["read_log_lines"].ProtocolTool.InputSchema.GetProperty("properties");
        Assert.Equal(1, read.GetProperty("startLine").GetProperty("minimum").GetInt32());
        Assert.Equal(1, read.GetProperty("count").GetProperty("minimum").GetInt32());
        Assert.False(read.GetProperty("count").TryGetProperty("maximum", out _));
        Assert.Contains("server_status", read.GetProperty("count").GetProperty("description").GetString(), StringComparison.Ordinal);
        var tail = tools["read_log_tail"].ProtocolTool.InputSchema;
        var prerequisite = tail.GetProperty("allOf")[0];
        Assert.Equal(2, prerequisite.GetProperty("if").GetProperty("anyOf").GetArrayLength());
        Assert.Contains("query", prerequisite.GetProperty("then").GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("string", prerequisite.GetProperty("then").GetProperty("properties").GetProperty("query").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("search_logs")]
    [InlineData("count_logs")]
    public void TimestampGuidance_SeparatesFileSelectionAndLineFiltering(string toolName)
    {
        using var backend = new RecordingBackend();
        var input = McpLogTools.CreateToolCollection(backend)[toolName].ProtocolTool.InputSchema.GetProperty("properties");
        Assert.Contains("yesterday", input.GetProperty("dateOffsetDays").GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("Independent", input.GetProperty("dateOffsetDays").GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("Missing/unrecognized", input.GetProperty("startTimestamp").GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("overnight ranges are rejected", input.GetProperty("endTimestamp").GetProperty("description").GetString(), StringComparison.Ordinal);
        if (toolName == "count_logs")
        {
            Assert.Contains("Frozen across continuations", input.GetProperty("relativeWindow").GetProperty("description").GetString(), StringComparison.Ordinal);
            Assert.Contains("none needs neither", input.GetProperty("bucketSize").GetProperty("description").GetString(), StringComparison.Ordinal);
            Assert.Contains("not day", input.GetProperty("bucketSize").GetProperty("description").GetString(), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("search_logs", "resultMode", "samples|matchesOnly|countsOnly")]
    [InlineData("count_logs", "bucketSize", "none|minute|hour|day")]
    [InlineData("count_logs", "bucketMode", "sparse|dense")]
    [InlineData("search_logs", "provenanceMode", "shared|inline")]
    [InlineData("count_logs", "provenanceMode", "shared|inline")]
    [InlineData("read_log_lines", "provenanceMode", "shared|inline")]
    [InlineData("read_log_tail", "provenanceMode", "shared|inline")]
    public void PresentationSchemas_ConstrainStringInputsWithoutChangingBinding(string toolName, string propertyName, string values)
    {
        using var backend = new RecordingBackend();
        var tool = McpLogTools.CreateToolCollection(backend)[toolName].ProtocolTool;
        var property = tool.InputSchema.GetProperty("properties").GetProperty(propertyName);
        Assert.Equal("string", property.GetProperty("type").GetString());
        Assert.Equal(values.Split('|'), property.GetProperty("enum").EnumerateArray().Select(item => item.GetString()));
    }

    [Theory]
    [InlineData("search_logs")]
    [InlineData("count_logs")]
    [InlineData("read_log_lines")]
    [InlineData("read_log_tail")]
    public void PresentationSchemas_ShareOneTypedProvenanceDefinitionAndVersionWireEnvelope(string toolName)
    {
        using var backend = new RecordingBackend();
        var schema = McpLogTools.CreateToolCollection(backend)[toolName].ProtocolTool.OutputSchema!.Value;
        Assert.Equal(4, schema.GetProperty("properties").GetProperty("schemaVersion").GetProperty("const").GetInt32());
        Assert.DoesNotContain(schema.GetProperty("required").EnumerateArray(), item => item.GetString() == "result");
        var definition = schema.GetProperty("$defs").GetProperty("provenance");
        Assert.Equal("object", definition.GetProperty("type").GetString());
        Assert.Equal(5, definition.GetProperty("properties").EnumerateObject().Count());
        var references = new List<string>();
        Visit(schema);
        Assert.Contains("#/$defs/provenance", references);
        Assert.True(references.Count(reference => reference == "#/$defs/provenance") >= 2);
        AssertCompactSchema(schema);
        void Visit(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                if (node.TryGetProperty("$ref", out var reference))
                    references.Add(reference.GetString()!);
                foreach (var property in node.EnumerateObject())
                    Visit(property.Value);
            }
            else if (node.ValueKind == JsonValueKind.Array)
                foreach (var item in node.EnumerateArray()) Visit(item);
        }
    }

    [Fact]
    public void CountSchema_DescribesBothFormatsAndStrictTupleShapes()
    {
        using var backend = new RecordingBackend();
        var schema = McpLogTools.CreateToolCollection(backend)["count_logs"].ProtocolTool.OutputSchema!.Value;
        var result = schema.GetProperty("properties").GetProperty("result");
        Assert.Contains("bucketMode", result.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        var properties = result.GetProperty("properties");
        Assert.Equal(new[] { "sparse", "dense" }, properties.GetProperty("bucketMode").GetProperty("enum").EnumerateArray().Select(item => item.GetString()));
        var tuple = properties.GetProperty("bucketCounts").GetProperty("items");
        Assert.Equal(3, tuple.GetProperty("minItems").GetInt32());
        Assert.Equal(3, tuple.GetProperty("maxItems").GetInt32());
        Assert.False(tuple.GetProperty("items").GetBoolean());
        Assert.All(tuple.GetProperty("prefixItems").EnumerateArray(), item => Assert.Equal("integer", item.GetProperty("type").GetString()));
        var anchors = properties.GetProperty("bucketGrid").GetProperty("properties").GetProperty("anchors").GetProperty("items");
        Assert.Equal(2, anchors.GetProperty("minItems").GetInt32());
        Assert.Equal("string", anchors.GetProperty("prefixItems")[1].GetProperty("type").GetString());
        Assert.Contains("buckets", properties.EnumerateObject().Select(item => item.Name));
        Assert.Equal(2, result.GetProperty("oneOf").GetArrayLength());
        Assert.Contains("bucketGrid", result.GetProperty("then").GetProperty("required").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public void OutputSchemas_DoNotRequireNullablePropertiesOmittedBySerialization()
    {
        using var backend = new RecordingBackend();
        foreach (var tool in McpLogTools.CreateToolCollection(backend))
            Visit(tool.ProtocolTool.OutputSchema!.Value);
        static void Visit(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                if (node.TryGetProperty("properties", out var properties) && node.TryGetProperty("required", out var required))
                    foreach (var name in required.EnumerateArray())
                    {
                        if (properties.TryGetProperty(name.GetString()!, out var property) && property.TryGetProperty("type", out var type) &&
                            type.ValueKind == JsonValueKind.Array)
                            Assert.DoesNotContain(type.EnumerateArray(), value => value.GetString() == "null");
                    }
                foreach (var property in node.EnumerateObject()) Visit(property.Value);
            }
            else if (node.ValueKind == JsonValueKind.Array)
                foreach (var item in node.EnumerateArray()) Visit(item);
        }
    }
}
