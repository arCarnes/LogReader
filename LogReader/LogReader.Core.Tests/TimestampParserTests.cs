namespace LogReader.Core.Tests;

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

    [Fact]
    public void TryBuildRange_DateTimeBounds_CompareFullTimestamp()
    {
        var parsed = TimestampParser.TryBuildRange("2026-03-09 19:00:00", "2026-03-09 20:00:00", out var range, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.False(range.CompareUsingTimeOfDay);
        Assert.True(TimestampParser.TryParseInput("2026-03-09 19:30:00", out var inRange));
        Assert.True(TimestampParser.TryParseInput("2026-03-10 19:30:00", out var outOfRange));
        Assert.True(range.Contains(inRange));
        Assert.False(range.Contains(outOfRange));
    }

    [Fact]
    public void TryBuildRange_TimeOnlyBounds_CompareTimeOfDay()
    {
        var parsed = TimestampParser.TryBuildRange("19:00:00", "20:00:00", out var range, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.True(range.CompareUsingTimeOfDay);
        Assert.True(TimestampParser.TryParseInput("2026-03-10 19:30:00", out var datedCandidate));
        Assert.True(range.Contains(datedCandidate));
    }

    [Theory]
    [InlineData("2026-03-09 19:00:00", "20:00:00")]
    [InlineData("19:00:00", "2026-03-09 20:00:00")]
    public void TryBuildRange_MixedDateAndTimeOnlyBounds_ReturnsError(string from, string to)
    {
        var parsed = TimestampParser.TryBuildRange(from, to, out _, out var error);

        Assert.False(parsed);
        Assert.Equal("'From' and 'To' must both include dates or both be time-only.", error);
    }
}
