namespace LogReader.Infrastructure.Services;

using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public sealed class LogTimestampNavigationService : ILogTimestampNavigationService
{
    public async Task<TimestampNavigationResult> FindNearestLineAsync(
        string filePath,
        ParsedTimestamp targetTimestamp,
        FileEncoding encoding,
        CancellationToken ct = default)
    {
        var streamEncoding = EncodingHelper.GetEncoding(encoding);
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 256 * 1024,
            FileOptions.SequentialScan | FileOptions.Asynchronous);
        using var reader = new StreamReader(stream, streamEncoding, detectEncodingFromByteOrderMarks: true, bufferSize: 256 * 1024);

        long lineNumber = 0;
        long bestLineNumber = 0;
        long bestDeltaTicks = long.MaxValue;
        var hasParseableTimestamp = false;

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line == null)
                break;

            lineNumber++;
            if (!TimestampParser.TryParseFromLogLine(line, out var lineTimestamp))
                continue;

            hasParseableTimestamp = true;
            var deltaTicks = ComputeTimestampDistanceTicks(targetTimestamp, lineTimestamp);
            if (deltaTicks >= bestDeltaTicks)
                continue;

            bestDeltaTicks = deltaTicks;
            bestLineNumber = lineNumber;
            if (deltaTicks == 0)
                break;
        }

        ct.ThrowIfCancellationRequested();

        if (!hasParseableTimestamp)
        {
            return new TimestampNavigationResult(
                LineNumber: 0,
                HasMatch: false,
                WasExactMatch: false,
                StatusMessage: "No parseable timestamps found in the current file.");
        }

        return new TimestampNavigationResult(
            LineNumber: bestLineNumber,
            HasMatch: true,
            WasExactMatch: bestDeltaTicks == 0,
            StatusMessage: bestDeltaTicks == 0
                ? $"Navigated to exact timestamp match at line {bestLineNumber:N0}."
                : $"No exact timestamp match. Navigated to nearest timestamp at line {bestLineNumber:N0}.");
    }

    private static long ComputeTimestampDistanceTicks(ParsedTimestamp target, ParsedTimestamp candidate)
    {
        if (!target.IsTimeOnly && !candidate.IsTimeOnly)
            return AbsTicks((candidate.Value - target.Value).Ticks);

        return ComputeTimeOfDayDistanceTicks(target.TimeOfDay, candidate.TimeOfDay);
    }

    private static long ComputeTimeOfDayDistanceTicks(TimeSpan left, TimeSpan right)
    {
        var directDifference = AbsTicks((left - right).Ticks);
        var wrappedDifference = TimeSpan.TicksPerDay - directDifference;
        return Math.Min(directDifference, wrappedDifference);
    }

    private static long AbsTicks(long ticks)
        => ticks == long.MinValue ? long.MaxValue : Math.Abs(ticks);
}
