namespace LogReader.Infrastructure.Services;

using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using LogReader.Core;
using LogReader.Core.Models;

internal sealed record CountTimeResolution(
    string? StartTimestamp,
    string? EndTimestamp,
    LogCountResolvedTimeRange? ResolvedRange,
    SearchTimestampAggregationPlan? AggregationPlan,
    string BucketSize);

internal static partial class CountTimeWindowResolver
{
    [GeneratedRegex(@"^last\s+([1-9]\d*)\s*([mhd])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RelativeWindowRegex();

    public static bool TryResolve(
        LogCountQuery query,
        DateTimeOffset capturedNow,
        TimeZoneInfo localTimeZone,
        LogQueryEffectiveLimits limits,
        out CountTimeResolution? resolution,
        out ConfiguredLogRequestError? error)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(localTimeZone);
        resolution = null;
        error = null;

        var bucketText = string.IsNullOrWhiteSpace(query.BucketSize)
            ? "none"
            : query.BucketSize.Trim().ToLowerInvariant();
        if (bucketText is not ("none" or "minute" or "hour" or "day"))
        {
            error = Error("invalid_bucket_size", "bucketSize must be one of: none, minute, hour, or day.");
            return false;
        }

        var relativeWindow = string.IsNullOrWhiteSpace(query.RelativeWindow)
            ? null
            : query.RelativeWindow.Trim().ToLowerInvariant();
        if (relativeWindow != null &&
            (!string.IsNullOrWhiteSpace(query.StartTimestamp) || !string.IsNullOrWhiteSpace(query.EndTimestamp)))
        {
            error = Error("conflicting_time_window", "relativeWindow cannot be combined with startTimestamp or endTimestamp.");
            return false;
        }

        string? startTimestamp;
        string? endTimestamp;
        TimestampRange range;
        LogCountResolvedTimeRange? resolvedRange;
        if (relativeWindow != null)
        {
            if (!TryResolveRelativeWindow(
                    relativeWindow,
                    capturedNow,
                    localTimeZone,
                    limits.MaximumRelativeWindowDays,
                    out var start,
                    out var end,
                    out error))
            {
                return false;
            }

            startTimestamp = start.ToString("O", CultureInfo.InvariantCulture);
            endTimestamp = end.ToString("O", CultureInfo.InvariantCulture);
            if (!TimestampParser.TryBuildRange(startTimestamp, endTimestamp, out range, out _))
                throw new InvalidOperationException("Resolved relative window could not be represented as a timestamp range.");
            resolvedRange = new LogCountResolvedTimeRange(
                "dated",
                startTimestamp,
                endTimestamp,
                localTimeZone.Id,
                relativeWindow);
        }
        else
        {
            startTimestamp = Normalize(query.StartTimestamp);
            endTimestamp = Normalize(query.EndTimestamp);
            if (!TimestampParser.TryBuildRange(startTimestamp, endTimestamp, out range, out _))
            {
                error = Error("invalid_timestamp_range", "The requested timestamp range is invalid.");
                return false;
            }

            resolvedRange = CreateResolvedRange(range, localTimeZone);
        }

        SearchTimestampAggregationPlan? aggregationPlan = null;
        if (bucketText != "none")
        {
            if (!range.From.HasValue || !range.To.HasValue)
            {
                error = Error(
                    "count_bucket_range_required",
                    "Bucketing requires relativeWindow or both startTimestamp and endTimestamp.");
                return false;
            }

            var bucketSize = ParseBucketSize(bucketText);
            if (range.CompareUsingTimeOfDay && bucketSize == SearchTimestampBucketSize.Day)
            {
                error = Error("unsupported_time_bucket", "Day buckets are not supported for time-only timestamp ranges.");
                return false;
            }

            var buckets = range.CompareUsingTimeOfDay
                ? BuildTimeOfDayBuckets(range, bucketSize, limits.MaximumCountBuckets)
                : BuildDatedBuckets(range, bucketSize, localTimeZone, limits.MaximumCountBuckets);
            if (buckets == null)
            {
                error = Error(
                    "count_bucket_limit_exceeded",
                    $"The resolved window exceeds the limit of {limits.MaximumCountBuckets} {bucketText} buckets. Use a larger bucket size or a narrower window.");
                return false;
            }

            var resolvedBuckets = buckets.Value;
            aggregationPlan = new SearchTimestampAggregationPlan(
                range.CompareUsingTimeOfDay ? SearchTimestampBucketKind.TimeOfDay : SearchTimestampBucketKind.Dated,
                bucketSize,
                resolvedBuckets);
            var bucketCharacterCount = resolvedBuckets.Sum(static bucket =>
                bucket.Start.Length + bucket.EndExclusive.Length + "timeOfDay".Length);
            var resolvedCharacterCount = resolvedRange == null
                ? 0
                : resolvedRange.Kind.Length +
                  resolvedRange.Start.Length +
                  resolvedRange.End.Length +
                  resolvedRange.TimeZoneId.Length +
                  (resolvedRange.RelativeWindow?.Length ?? 0);
            if (bucketCharacterCount + resolvedCharacterCount > limits.MaximumResponseCharacters * 3 / 4)
            {
                error = Error(
                    "count_bucket_limit_exceeded",
                    "The resolved buckets exceed the bounded count response capacity. Use a larger bucket size or a narrower window.");
                return false;
            }
        }

        resolution = new CountTimeResolution(
            startTimestamp,
            endTimestamp,
            resolvedRange,
            aggregationPlan,
            bucketText);
        return true;
    }

    private static bool TryResolveRelativeWindow(
        string relativeWindow,
        DateTimeOffset capturedNow,
        TimeZoneInfo localTimeZone,
        int maximumDays,
        out DateTimeOffset start,
        out DateTimeOffset end,
        out ConfiguredLogRequestError? error)
    {
        error = null;
        end = TimeZoneInfo.ConvertTime(capturedNow, localTimeZone);
        if (StringComparer.Ordinal.Equals(relativeWindow, "today"))
        {
            start = ResolveLocalDayStart(end.Date, localTimeZone);
            return true;
        }

        var match = RelativeWindowRegex().Match(relativeWindow);
        if (!match.Success || !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
        {
            start = default;
            error = Error("invalid_relative_window", "relativeWindow must be 'today' or 'last <positive integer><m|h|d>'.");
            return false;
        }

        double totalMinutes = match.Groups[2].Value.ToLowerInvariant() switch
        {
            "m" => amount,
            "h" => amount * 60d,
            _ => amount * 24d * 60d
        };
        var maximumMinutes = maximumDays * 24d * 60d;
        if (double.IsInfinity(totalMinutes) || totalMinutes > maximumMinutes)
        {
            start = default;
            error = Error("relative_window_limit_exceeded", $"relativeWindow cannot exceed {maximumDays} elapsed days.");
            return false;
        }

        var startInstant = capturedNow.ToUniversalTime().Subtract(TimeSpan.FromMinutes(totalMinutes));
        start = TimeZoneInfo.ConvertTime(startInstant, localTimeZone);
        return true;
    }

    private static LogCountResolvedTimeRange? CreateResolvedRange(
        TimestampRange range,
        TimeZoneInfo localTimeZone)
    {
        if (!range.HasBounds)
            return null;

        if (range.CompareUsingTimeOfDay)
        {
            return new LogCountResolvedTimeRange(
                "timeOfDay",
                range.From?.TimeOfDay.ToString("c", CultureInfo.InvariantCulture) ?? string.Empty,
                range.To?.TimeOfDay.ToString("c", CultureInfo.InvariantCulture) ?? string.Empty,
                localTimeZone.Id,
                RelativeWindow: null);
        }

        return new LogCountResolvedTimeRange(
            "dated",
            range.From.HasValue
                ? TimeZoneInfo.ConvertTime(range.From.Value.Value, localTimeZone).ToString("O", CultureInfo.InvariantCulture)
                : string.Empty,
            range.To.HasValue
                ? TimeZoneInfo.ConvertTime(range.To.Value.Value, localTimeZone).ToString("O", CultureInfo.InvariantCulture)
                : string.Empty,
            localTimeZone.Id,
            RelativeWindow: null);
    }

    private static ImmutableArray<SearchTimestampBucketDefinition>? BuildTimeOfDayBuckets(
        TimestampRange range,
        SearchTimestampBucketSize size,
        int maximumBuckets)
    {
        var step = size == SearchTimestampBucketSize.Minute ? TimeSpan.FromMinutes(1) : TimeSpan.FromHours(1);
        var start = FloorTimeOfDay(range.From!.Value.TimeOfDay, size);
        var end = range.To!.Value.TimeOfDay;
        var buckets = ImmutableArray.CreateBuilder<SearchTimestampBucketDefinition>();
        while (start <= end)
        {
            if (buckets.Count >= maximumBuckets)
                return null;

            var endExclusive = start.Add(step);
            buckets.Add(new SearchTimestampBucketDefinition(
                buckets.Count,
                start.ToString("c", CultureInfo.InvariantCulture),
                endExclusive.ToString("c", CultureInfo.InvariantCulture),
                start.Ticks,
                endExclusive.Ticks));
            start = endExclusive;
        }

        return buckets.ToImmutable();
    }

    private static ImmutableArray<SearchTimestampBucketDefinition>? BuildDatedBuckets(
        TimestampRange range,
        SearchTimestampBucketSize size,
        TimeZoneInfo localTimeZone,
        int maximumBuckets)
    {
        var rangeStart = range.From!.Value.Value;
        var rangeEnd = range.To!.Value.Value;
        if (size != SearchTimestampBucketSize.Day)
        {
            var minimumStep = size == SearchTimestampBucketSize.Minute
                ? TimeSpan.FromMinutes(1)
                : TimeSpan.FromHours(1);
            if (rangeEnd.UtcTicks - rangeStart.UtcTicks >= minimumStep.Ticks * maximumBuckets)
                return null;
        }

        var localStart = TimeZoneInfo.ConvertTime(rangeStart, localTimeZone);
        var localEnd = TimeZoneInfo.ConvertTime(rangeEnd, localTimeZone);
        var firstDate = MinDate(localStart.Date, localEnd.Date);
        if (firstDate > DateTime.MinValue.Date)
            firstDate = firstDate.AddDays(-1);
        var lastDate = MaxDate(localStart.Date, localEnd.Date);
        if (lastDate < DateTime.MaxValue.Date)
            lastDate = lastDate.AddDays(1);
        var boundaryCandidates = new List<DateTimeOffset>();
        for (var date = firstDate;; date = date.AddDays(1))
        {
            if (size == SearchTimestampBucketSize.Day)
            {
                boundaryCandidates.Add(ResolveLocalDayStart(date, localTimeZone));
            }
            else
            {
                var unitsPerDay = size == SearchTimestampBucketSize.Minute ? 24 * 60 : 24;
                var step = size == SearchTimestampBucketSize.Minute
                    ? TimeSpan.FromMinutes(1)
                    : TimeSpan.FromHours(1);
                for (var unit = 0; unit < unitsPerDay; unit++)
                    boundaryCandidates.AddRange(BoundaryCandidates(date.Add(step * unit), localTimeZone));
            }

            if (date >= lastDate)
                break;
        }

        var boundaries = boundaryCandidates
            .OrderBy(static value => value.UtcTicks)
            .DistinctBy(static value => value.UtcTicks)
            .ToArray();
        var firstBoundaryIndex = Array.FindLastIndex(boundaries, value => value <= rangeStart);
        if (firstBoundaryIndex < 0)
            throw new InvalidOperationException("No local timestamp bucket boundary precedes the requested range.");

        var buckets = ImmutableArray.CreateBuilder<SearchTimestampBucketDefinition>();
        for (var index = firstBoundaryIndex; index + 1 < boundaries.Length; index++)
        {
            var current = boundaries[index];
            if (current > rangeEnd)
                break;
            if (buckets.Count >= maximumBuckets)
                return null;

            var next = boundaries[index + 1];
            buckets.Add(new SearchTimestampBucketDefinition(
                buckets.Count,
                current.ToString("O", CultureInfo.InvariantCulture),
                next.ToString("O", CultureInfo.InvariantCulture),
                current.UtcTicks,
                next.UtcTicks));
        }

        return buckets.ToImmutable();
    }

    private static IEnumerable<DateTimeOffset> BoundaryCandidates(DateTime wall, TimeZoneInfo localTimeZone)
    {
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        if (localTimeZone.IsInvalidTime(wall))
            return [];
        if (localTimeZone.IsAmbiguousTime(wall))
        {
            return localTimeZone.GetAmbiguousTimeOffsets(wall)
                .Select(offset => new DateTimeOffset(wall, offset))
                .OrderBy(static value => value.UtcTicks)
                .ToArray();
        }

        return [new DateTimeOffset(wall, localTimeZone.GetUtcOffset(wall))];
    }

    private static DateTimeOffset ResolveLocalDayStart(DateTime date, TimeZoneInfo localTimeZone)
    {
        var wall = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
        while (true)
        {
            var candidate = BoundaryCandidates(wall, localTimeZone)
                .OrderBy(static value => value.UtcTicks)
                .FirstOrDefault();
            if (candidate != default)
                return candidate;
            wall = wall.AddMinutes(1);
        }
    }

    private static TimeSpan FloorTimeOfDay(TimeSpan value, SearchTimestampBucketSize size)
        => size == SearchTimestampBucketSize.Minute
            ? TimeSpan.FromMinutes(Math.Floor(value.TotalMinutes))
            : TimeSpan.FromHours(Math.Floor(value.TotalHours));

    private static DateTime MinDate(DateTime left, DateTime right)
        => left <= right ? left.Date : right.Date;

    private static DateTime MaxDate(DateTime left, DateTime right)
        => left >= right ? left.Date : right.Date;

    private static SearchTimestampBucketSize ParseBucketSize(string value)
        => value switch
        {
            "minute" => SearchTimestampBucketSize.Minute,
            "hour" => SearchTimestampBucketSize.Hour,
            _ => SearchTimestampBucketSize.Day
        };

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ConfiguredLogRequestError Error(string code, string message)
        => new(code, message);
}
