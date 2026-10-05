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
