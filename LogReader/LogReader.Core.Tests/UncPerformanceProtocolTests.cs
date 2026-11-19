namespace LogReader.Core.Tests;

using System.Text.Json;
using LogReader.Core.Tests.UncPerformance;

public sealed class UncPerformanceProtocolTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ToolValidation_AcceptsEquivalentStructuredTextAndSuccessfulOutcomes(bool includeErrors)
    {
        using var structured = JsonDocument.Parse(includeErrors
            ? "{\"schemaVersion\":5,\"status\":\"success\",\"data\":{\"count\":1},\"issues\":[]}"
            : "{\"schemaVersion\":5,\"status\":\"partial\",\"data\":{\"count\":1},\"issues\":[{\"code\":\"file_error\"}]}");
        var response = JsonSerializer.SerializeToElement(new
        {
            content = new[] { new { type = "text", text = structured.RootElement.GetRawText() } },
            structuredContent = structured.RootElement
        });
        var validated = McpMeasurementClient.ValidateToolResponse(response);
        Assert.Equal(1, validated.GetProperty("data").GetProperty("count").GetInt32());
    }

    [Theory]
    [InlineData("text-mismatch")]
    [InlineData("tool-error")]
    [InlineData("envelope-error")]
    [InlineData("duplicate-text")]
    public void ToolValidation_RejectsErrorsAndMismatchedOrAmbiguousText(string failure)
    {
        var envelope = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 5, data = new { count = 1 }, status = failure == "envelope-error" ? "error" : "success",
            issues = failure == "envelope-error" ? new[] { new { code = "fixture_error" } } : []
        });
        var text = new { type = "text", text = failure == "text-mismatch" ? "{\"result\":{\"count\":2},\"errors\":[]}" : envelope.GetRawText() };
        var response = JsonSerializer.SerializeToElement(new
        {
            isError = failure == "tool-error", content = failure == "duplicate-text" ? new[] { text, text } : [text],
            structuredContent = envelope
        });
        Assert.Throws<InvalidOperationException>(() => McpMeasurementClient.ValidateToolResponse(response));
    }

    [Theory]
    [InlineData(@"\\other\share\report.json")]
    [InlineData("//other/share/report.json")]
    public void LocalArtifacts_RejectUncBeforeAccess(string path)
        => Assert.Throws<InvalidOperationException>(() => FixtureStore.RequireLocalPath(path));

    [Fact]
    public void LocalArtifacts_NormalizeRelativePathsWithoutCreatingThem()
    {
        var path = Path.Combine("artifacts", "measurements", "unc-performance", "new-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(Path.GetFullPath(path), FixtureStore.RequireLocalPath(path));
        Assert.False(Directory.Exists(path));
    }
}
