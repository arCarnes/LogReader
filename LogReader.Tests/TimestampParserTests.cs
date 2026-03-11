namespace LogReader.Tests;

using LogReader.Core;

public class TimestampParserTests
{
    [Theory]
    [InlineData("2026-03-09T19:49:12.334Z", false)]
    [InlineData("2026-03-09 19:49:12", false)]
    [InlineData("19:49:12.334", true)]
    public void TryParseInput_CommonFormats_AreSupported(string input, bool expectedTimeOnly)
    {
        var parsed = TimestampParser.TryParseInput(input, out var timestamp);

        Assert.True(parsed);
        Assert.Equal(expectedTimeOnly, timestamp.IsTimeOnly);
    }

    [Fact]
    public void TryParseFromLogLine_FindsEmbeddedTimestamp()
    {
        var line = "INFO corr=abc 2026-03-09 19:49:12 heartbeat ok";

        var parsed = TimestampParser.TryParseFromLogLine(line, out var timestamp);

        Assert.True(parsed);
        Assert.False(timestamp.IsTimeOnly);
    }

    [Fact]
    public void TryBuildRange_InvalidOrder_ReturnsError()
    {
        var parsed = TimestampParser.TryBuildRange("2026-03-09 19:50:00", "2026-03-09 19:49:00", out _, out var error);

        Assert.False(parsed);
        Assert.NotNull(error);
    }
}
