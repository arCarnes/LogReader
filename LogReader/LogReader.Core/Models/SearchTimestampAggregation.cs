namespace LogReader.Core.Models;

public enum SearchTimestampBucketKind
{
    Dated,
    TimeOfDay
}

public enum SearchTimestampBucketSize
{
    Minute,
    Hour,
    Day
}

public sealed record SearchTimestampBucketDefinition(
    int Index,
    string Start,
    string EndExclusive,
    long StartInclusiveTicks,
    long EndExclusiveTicks);

public sealed class SearchTimestampAggregationPlan
{
    private readonly SearchTimestampBucketDefinition[] _buckets;

    public SearchTimestampAggregationPlan(
        SearchTimestampBucketKind kind,
        SearchTimestampBucketSize size,
        IReadOnlyList<SearchTimestampBucketDefinition> buckets)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        if (buckets.Count == 0)
            throw new ArgumentException("At least one timestamp bucket is required.", nameof(buckets));

        _buckets = buckets.ToArray();
        for (var index = 0; index < _buckets.Length; index++)
        {
            var bucket = _buckets[index];
            if (bucket.Index != index ||
                bucket.StartInclusiveTicks < 0 ||
                bucket.EndExclusiveTicks <= bucket.StartInclusiveTicks ||
                index > 0 && bucket.StartInclusiveTicks < _buckets[index - 1].EndExclusiveTicks)
            {
                throw new ArgumentException("Timestamp buckets must be ordered, non-overlapping, and consecutively indexed.", nameof(buckets));
            }
        }

        Kind = kind;
        Size = size;
    }

    public SearchTimestampBucketKind Kind { get; }

    public SearchTimestampBucketSize Size { get; }

    public IReadOnlyList<SearchTimestampBucketDefinition> Buckets => _buckets;

    public bool TryGetBucketIndex(ParsedTimestamp timestamp, out int bucketIndex)
    {
        bucketIndex = -1;
        if (Kind == SearchTimestampBucketKind.Dated && timestamp.IsTimeOnly)
            return false;

        var candidateTicks = Kind == SearchTimestampBucketKind.TimeOfDay
            ? timestamp.TimeOfDay.Ticks
            : timestamp.Value.UtcTicks;
        var low = 0;
        var high = _buckets.Length - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var bucket = _buckets[middle];
            if (candidateTicks < bucket.StartInclusiveTicks)
            {
                high = middle - 1;
            }
            else if (candidateTicks >= bucket.EndExclusiveTicks)
            {
                low = middle + 1;
            }
            else
            {
                bucketIndex = bucket.Index;
                return true;
            }
        }

        return false;
    }
}

public sealed class SearchTimestampBucketCount
{
    public long MatchingLineCount { get; set; }

    public long MatchOccurrenceCount { get; set; }
}
