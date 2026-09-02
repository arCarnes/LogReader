namespace LogReader.Core.Tests;

using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

public sealed class CountTimeWindowResolverTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Fact]
    public void Resolve_LastHour_CapturesExplicitBoundsAndDenseMinuteBuckets()
    {
        var query = new LogCountQuery
        {
            RelativeWindow = " LAST 60m ",
            BucketSize = "minute"
        };

        var success = CountTimeWindowResolver.TryResolve(
            query,
            new DateTimeOffset(2026, 8, 29, 12, 34, 56, TimeSpan.Zero),
            Utc,
            LogQueryEffectiveLimits.Default,
            out var result,
            out var error);

        Assert.True(success);
        Assert.Null(error);
        Assert.Equal("2026-08-29T11:34:56.0000000+00:00", result!.ResolvedRange!.Start);
        Assert.Equal("2026-08-29T12:34:56.0000000+00:00", result.ResolvedRange.End);
        Assert.Equal("last 60m", result.ResolvedRange.RelativeWindow);
        Assert.Equal(61, result.AggregationPlan!.Buckets.Count);
        Assert.Equal("2026-08-29T11:34:00.0000000+00:00", result.AggregationPlan.Buckets[0].Start);
    }

    [Fact]
    public void Resolve_Today_UsesLocalMidnightAndCapturedNow()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var capturedNow = new DateTimeOffset(2026, 8, 29, 16, 45, 0, TimeSpan.Zero);

        var success = CountTimeWindowResolver.TryResolve(
            new LogCountQuery { RelativeWindow = "today" },
            capturedNow,
            zone,
            LogQueryEffectiveLimits.Default,
            out var result,
            out var error);

        Assert.True(success);
        Assert.Null(error);
        Assert.Equal("2026-08-29T00:00:00.0000000-04:00", result!.ResolvedRange!.Start);
        Assert.Equal("2026-08-29T12:45:00.0000000-04:00", result.ResolvedRange.End);
    }

    [Fact]
    public void Resolve_RelativeAndAbsoluteBounds_AreRejected()
    {
        var success = CountTimeWindowResolver.TryResolve(
            new LogCountQuery
            {
                RelativeWindow = "last 1h",
                StartTimestamp = "2026-08-29T10:00:00Z"
            },
            DateTimeOffset.UtcNow,
            Utc,
            LogQueryEffectiveLimits.Default,
            out _,
            out var error);

        Assert.False(success);
        Assert.Equal("conflicting_time_window", error!.Code);
    }

    [Fact]
    public void Resolve_MinuteBuckets_AdmitsOneThousandAndRejectsOneThousandOne()
    {
        var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

        Assert.True(CountTimeWindowResolver.TryResolve(
            new LogCountQuery { RelativeWindow = "last 999m", BucketSize = "minute" },
            now,
            Utc,
            LogQueryEffectiveLimits.Default,
            out var admitted,
            out _));
        Assert.Equal(1_000, admitted!.AggregationPlan!.Buckets.Count);

        Assert.False(CountTimeWindowResolver.TryResolve(
            new LogCountQuery { RelativeWindow = "last 1000m", BucketSize = "minute" },
            now,
            Utc,
            LogQueryEffectiveLimits.Default,
            out _,
            out var error));
        Assert.Equal("count_bucket_limit_exceeded", error!.Code);
    }

    [Fact]
    public void Resolve_TimeOnlyDayBucket_IsRejected()
    {
        var success = CountTimeWindowResolver.TryResolve(
            new LogCountQuery
            {
                StartTimestamp = "09:00",
                EndTimestamp = "10:00",
                BucketSize = "day"
            },
            DateTimeOffset.UtcNow,
            Utc,
            LogQueryEffectiveLimits.Default,
            out _,
            out var error);

        Assert.False(success);
        Assert.Equal("unsupported_time_bucket", error!.Code);
    }

    [Fact]
    public void Resolve_FallBackHour_EmitsBothAmbiguousLocalHoursWithOffsets()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var success = CountTimeWindowResolver.TryResolve(
            new LogCountQuery
            {
                StartTimestamp = "2026-11-01T00:30:00-04:00",
                EndTimestamp = "2026-11-01T02:30:00-05:00",
                BucketSize = "hour"
            },
            DateTimeOffset.UtcNow,
            zone,
            LogQueryEffectiveLimits.Default,
            out var result,
            out var error);

        Assert.True(success);
        Assert.Null(error);
        Assert.Equal(
            [
                "2026-11-01T00:00:00.0000000-04:00",
                "2026-11-01T01:00:00.0000000-04:00",
                "2026-11-01T01:00:00.0000000-05:00",
                "2026-11-01T02:00:00.0000000-05:00"
            ],
            result!.AggregationPlan!.Buckets.Select(static bucket => bucket.Start));
    }

    [Fact]
    public void Resolve_FallBackMinutes_EmitsBothCompleteRepeatedSequencesChronologically()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var success = CountTimeWindowResolver.TryResolve(
            new LogCountQuery
            {
                StartTimestamp = "2026-11-01T00:59:30-04:00",
                EndTimestamp = "2026-11-01T02:00:30-05:00",
                BucketSize = "minute"
            },
            DateTimeOffset.UtcNow,
            zone,
            LogQueryEffectiveLimits.Default,
            out var result,
            out var error);

        Assert.True(success);
        Assert.Null(error);
        var buckets = result!.AggregationPlan!.Buckets;
        Assert.Equal(122, buckets.Count);
        Assert.Equal(60, buckets.Count(bucket => bucket.Start.StartsWith("2026-11-01T01:", StringComparison.Ordinal) && bucket.Start.EndsWith("-04:00", StringComparison.Ordinal)));
        Assert.Equal(60, buckets.Count(bucket => bucket.Start.StartsWith("2026-11-01T01:", StringComparison.Ordinal) && bucket.Start.EndsWith("-05:00", StringComparison.Ordinal)));
        Assert.All(buckets, bucket => Assert.Equal(TimeSpan.FromMinutes(1).Ticks, bucket.EndExclusiveTicks - bucket.StartInclusiveTicks));
        Assert.True(buckets.Select(static bucket => bucket.StartInclusiveTicks).SequenceEqual(
            buckets.Select(static bucket => bucket.StartInclusiveTicks).Order()));
    }

    [Fact]
    public void Resolve_SpringForwardMinutes_SkipsInvalidLocalTimes()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var success = CountTimeWindowResolver.TryResolve(
            new LogCountQuery
            {
                StartTimestamp = "2026-03-08T01:58:30-05:00",
                EndTimestamp = "2026-03-08T03:01:30-04:00",
                BucketSize = "minute"
            },
            DateTimeOffset.UtcNow,
            zone,
            LogQueryEffectiveLimits.Default,
            out var result,
            out var error);

        Assert.True(success);
        Assert.Null(error);
        Assert.Equal(
            [
                "2026-03-08T01:58:00.0000000-05:00",
                "2026-03-08T01:59:00.0000000-05:00",
                "2026-03-08T03:00:00.0000000-04:00",
                "2026-03-08T03:01:00.0000000-04:00"
            ],
            result!.AggregationPlan!.Buckets.Select(static bucket => bucket.Start));
        Assert.All(result.AggregationPlan.Buckets, bucket => Assert.Equal(TimeSpan.FromMinutes(1).Ticks, bucket.EndExclusiveTicks - bucket.StartInclusiveTicks));
    }
}
