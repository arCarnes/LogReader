namespace LogReader.Infrastructure.Services;

using System.Text;
using LogReader.Core;
using LogReader.Core.Models;

internal static class SearchResultHelpers
{
    internal static SearchHit RetainResumableHit(string line, long lineNumber, int start, int length, int maximumLength)
    {
        var retained = RetainLineText(line, start, length, maximumLength);
        return new SearchHit
        {
            LineNumber = lineNumber,
            LineText = retained.Text,
            MatchStart = retained.PrefixLength + Math.Max(start, retained.WindowStart) - retained.WindowStart,
            MatchLength = Math.Max(0, Math.Min(start + length, retained.WindowEnd) - Math.Max(start, retained.WindowStart)),
            OriginalMatchStart = start,
            OriginalMatchLength = length,
            LineTextTruncated = retained.WindowStart > 0 || retained.WindowEnd < line.Length
        };
    }

    internal static void AccountResumableLine(SearchResult result, SearchRequest request,
        int occurrences, ParsedTimestamp? timestamp)
    {
        if (occurrences == 0)
            return;
        result.MatchingLineCount++;
        result.MatchOccurrenceCount += occurrences;
        AddTimestampAggregation(result, request, timestamp, occurrences);
    }

    internal static void AddTimestampAggregation(
        SearchResult result,
        SearchRequest request,
        ParsedTimestamp? lineTimestamp,
        int occurrenceCount)
    {
        var aggregation = request.TimestampAggregation;
        if (aggregation == null)
            return;

        if (!lineTimestamp.HasValue || !aggregation.TryGetBucketIndex(lineTimestamp.Value, out var bucketIndex))
        {
            result.UnbucketedMatchingLineCount++;
            result.UnbucketedMatchOccurrenceCount += occurrenceCount;
            return;
        }

        if (!result.TimestampBucketCounts.TryGetValue(bucketIndex, out var bucket))
        {
            bucket = new SearchTimestampBucketCount();
            result.TimestampBucketCounts.Add(bucketIndex, bucket);
        }

        bucket.MatchingLineCount++;
        bucket.MatchOccurrenceCount += occurrenceCount;
    }

    internal static RetainedLineText RetainLineText(string line, int matchStart, int matchLength, int? maxRetainedLength)
    {
        if (!maxRetainedLength.HasValue || maxRetainedLength.Value <= 0 || line.Length <= maxRetainedLength.Value)
            return new RetainedLineText(line, 0, line.Length, 0);

        var maxLength = maxRetainedLength.Value;
        const string marker = "...";
        if (maxLength <= marker.Length * 2 + 1)
        {
            var start = Math.Min(Math.Max(0, matchStart), Math.Max(0, line.Length - maxLength));
            var end = Math.Min(line.Length, start + maxLength);
            return new RetainedLineText(line.Substring(start, end - start), start, end, 0);
        }

        var contentLength = Math.Max(1, maxLength - (marker.Length * 2));
        var contextBefore = Math.Max(0, (contentLength - matchLength) / 2);
        var windowStart = Math.Clamp(matchStart - contextBefore, 0, Math.Max(0, line.Length - contentLength));
        var windowEnd = Math.Min(line.Length, windowStart + contentLength);
        var hasPrefix = windowStart > 0;
        var hasSuffix = windowEnd < line.Length;

        var builder = new StringBuilder(maxLength);
        if (hasPrefix)
            builder.Append(marker);

        builder.Append(line, windowStart, windowEnd - windowStart);

        if (hasSuffix)
            builder.Append(marker);

        return new RetainedLineText(
            builder.ToString(),
            windowStart,
            windowEnd,
            hasPrefix ? marker.Length : 0);
    }

    internal sealed record RetainedLineText(
        string Text,
        int WindowStart,
        int WindowEnd,
        int PrefixLength);

}
