namespace LogReader.Core.Tests;

using LogReader.Mcp;

public sealed class McpProcessEntryPointTests
{
    [Fact]
    public void HasValidArguments_NoArguments_ReturnsTrue()
    {
        Assert.True(Program.HasValidArguments([]));
    }

    [Theory]
    [MemberData(nameof(InvalidArgumentSets))]
    public void HasValidArguments_AnyArgument_ReturnsFalse(string[] args)
    {
        Assert.False(Program.HasValidArguments(args));
    }

    public static TheoryData<string[]> InvalidArgumentSets => new()
    {
        new[] { "--mcp-stdio" },
        new[] { "--unknown" },
        new[] { "unexpected", "arguments" }
    };
}
