namespace LogReader.Tests;

using LogReader.App;

public sealed class ProcessEntryPointTests
{
    [Fact]
    public void IsMcpStdioMode_ExactArgument_ReturnsTrue()
    {
        Assert.True(Program.IsMcpStdioMode(["--mcp-stdio"]));
    }

    [Theory]
    [MemberData(nameof(NonMcpArgumentSets))]
    public void IsMcpStdioMode_OtherArguments_ReturnsFalse(string[] args)
    {
        Assert.False(Program.IsMcpStdioMode(args));
    }

    public static TheoryData<string[]> NonMcpArgumentSets => new()
    {
        Array.Empty<string>(),
        new[] { "--MCP-STDIO" },
        new[] { "--mcp-stdio", "unexpected" },
        new[] { "--unknown" }
    };
}
