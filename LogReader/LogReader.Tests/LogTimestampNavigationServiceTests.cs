namespace LogReader.Tests;

using LogReader.Core;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

public class LogTimestampNavigationServiceTests
{
    [Fact]
    public async Task FindNearestLineAsync_ExactMatch_ReturnsExactResult()
    {
        var path = Path.Combine(Path.GetTempPath(), $"logreader-ts-service-{Guid.NewGuid():N}.log");
        try
        {
            await File.WriteAllTextAsync(path,
                "2026-03-09 19:49:10 INFO one\n2026-03-09 19:49:20 INFO two\n2026-03-09 19:49:30 INFO three\n");
            var service = new LogTimestampNavigationService();
            Assert.True(TimestampParser.TryParseInput("2026-03-09 19:49:20", out var target));

            var result = await service.FindNearestLineAsync(path, target, FileEncoding.Utf8);

            Assert.True(result.HasMatch);
            Assert.True(result.WasExactMatch);
            Assert.Equal(2, result.LineNumber);
            Assert.Contains("exact timestamp match", result.StatusMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task FindNearestLineAsync_NoExactMatch_ReturnsNearestLine()
    {
        var path = Path.Combine(Path.GetTempPath(), $"logreader-ts-service-{Guid.NewGuid():N}.log");
        try
        {
            await File.WriteAllTextAsync(path,
                "2026-03-09 19:49:10 INFO one\n2026-03-09 19:49:30 INFO two\n");
            var service = new LogTimestampNavigationService();
            Assert.True(TimestampParser.TryParseInput("2026-03-09 19:49:26", out var target));

            var result = await service.FindNearestLineAsync(path, target, FileEncoding.Utf8);

            Assert.True(result.HasMatch);
            Assert.False(result.WasExactMatch);
            Assert.Equal(2, result.LineNumber);
            Assert.Contains("nearest timestamp", result.StatusMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task FindNearestLineAsync_NoParseableTimestamps_ReturnsNoMatch()
    {
        var path = Path.Combine(Path.GetTempPath(), $"logreader-ts-service-{Guid.NewGuid():N}.log");
        try
        {
            await File.WriteAllTextAsync(path, "INFO one\nWARN two\nERROR three\n");
            var service = new LogTimestampNavigationService();
            Assert.True(TimestampParser.TryParseInput("2026-03-09 19:49:26", out var target));

            var result = await service.FindNearestLineAsync(path, target, FileEncoding.Utf8);

            Assert.False(result.HasMatch);
            Assert.False(result.WasExactMatch);
            Assert.Equal(0, result.LineNumber);
            Assert.Equal("No parseable timestamps found in the current file.", result.StatusMessage);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task FindNearestLineAsync_TimeOnlyComparison_UsesWrapAwareDistance()
    {
        var path = Path.Combine(Path.GetTempPath(), $"logreader-ts-service-{Guid.NewGuid():N}.log");
        try
        {
            await File.WriteAllTextAsync(path,
                "23:59:58 INFO almost midnight\n00:00:02 INFO just after midnight\n");
            var service = new LogTimestampNavigationService();
            Assert.True(TimestampParser.TryParseInput("00:00:01", out var target));

            var result = await service.FindNearestLineAsync(path, target, FileEncoding.Utf8);

            Assert.True(result.HasMatch);
            Assert.Equal(2, result.LineNumber);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
