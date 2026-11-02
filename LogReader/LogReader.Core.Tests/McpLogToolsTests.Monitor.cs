namespace LogReader.Core.Tests;

using LogReader.Core.Models;
using ModelContextProtocol.Protocol;

public sealed partial class McpLogToolsTests
{
    [Fact]
    public async Task MonitorProtocol_ProjectsAttributableEventsAndRetainsBoundedControls()
    {
        using var backend = new RecordingBackend
        {
            MonitorResult = new LogMonitorResult
            {
                NextCursor = "opaque", BacklogFileCount = 1,
                Files = [new("file", "Application", [new("dashboard", ConfiguredLogTargetKind.Dashboard,
                    "Dashboard", "dashboard", "Dashboard")], "generation", "backlog", 123,
                    [new("appended", 42, "needle", false)], null)]
            }
        };
        await WithClientAsync(backend, async client =>
        {
            var response = await client.CallToolAsync("monitor_logs", new Dictionary<string, object?>
            {
                ["targets"] = new[] { new { kind = "dashboard", id = "dashboard" } },
                ["query"] = "needle", ["excludeQuery"] = "drop", ["maxFiles"] = 3, ["maxLines"] = 7
            });
            Assert.NotEqual(true, response.IsError);
            var envelope = response.StructuredContent!.Value;
            Assert.Equal(envelope.GetRawText(), Assert.IsType<TextContentBlock>(Assert.Single(response.Content)).Text);
            var result = envelope.GetProperty("result");
            Assert.False(result.GetProperty("isIdle").GetBoolean());
            Assert.Equal("opaque", result.GetProperty("nextCursor").GetString());
            var file = result.GetProperty("files")[0];
            Assert.Equal(0, file.GetProperty("provenanceRefs")[0].GetInt32());
            Assert.Single(result.GetProperty("provenanceTable").EnumerateArray());
            Assert.Equal(42, file.GetProperty("events")[0].GetProperty("byteOffset").GetInt64());
            Assert.Equal(3, backend.LastMonitorRequest!.MaxFiles);
            Assert.Equal(7, backend.LastMonitorRequest.MaxLines);
            Assert.Equal("drop", backend.LastMonitorRequest.ExcludeQuery);
        });
    }
}
