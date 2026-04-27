namespace LogReader.Core;

internal static class AdaptiveParallelismPolicy
{
    private const int DashboardLoadGlobalCap = 24;
    private const int SearchAndFilterGlobalCap = 32;
    private const int DashboardLoadUncHostLimit = 2;
    private const int SearchAndFilterUncHostLimit = 3;
    private const int UncShareLimit = 2;
    private const int LocalRootLimit = 2;
    private const int UnknownLimit = 1;

    internal static ParallelismPlan CreatePlan(
        AdaptiveParallelismOperation operation,
        IEnumerable<string> targetPaths)
    {
        ArgumentNullException.ThrowIfNull(targetPaths);

        var targets = targetPaths.ToArray();
        if (targets.Length == 0)
        {
            return new ParallelismPlan(
                operation,
                0,
                0,
                Array.Empty<ParallelismTargetPlan>(),
                Array.Empty<ParallelismGroupPlan>(),
                $"{operation}: no targets.");
        }

        var limits = GetLimits(operation);
        var topLevelBuckets = new Dictionary<string, BucketBuilder>(StringComparer.Ordinal);
        var shareBuckets = new Dictionary<string, BucketBuilder>(StringComparer.Ordinal);
        var targetPlans = new List<ParallelismTargetPlan>(targets.Length);

        foreach (var path in targets)
        {
            var classification = Classify(path);
            targetPlans.Add(new ParallelismTargetPlan(
                path,
                classification.TopLevelKey,
                classification.ShareKey));

            var topLevelBucket = GetOrCreateBucket(
                topLevelBuckets,
                classification.TopLevelKind,
                classification.TopLevelKey,
                classification.TopLevelDisplayName,
                null);
            topLevelBucket.AddTarget();

            if (classification.ShareKey is not null)
            {
                var shareBucket = GetOrCreateBucket(
                    shareBuckets,
                    ParallelismGroupKind.UncShare,
                    classification.ShareKey,
                    classification.ShareDisplayName!,
                    classification.TopLevelKey);
                shareBucket.AddTarget();
            }
        }

        var topLevelPlans = topLevelBuckets.Values
            .OrderBy(bucket => bucket.Kind)
            .ThenBy(bucket => bucket.Key, StringComparer.Ordinal)
            .Select(bucket => bucket.ToPlan(GetBucketLimit(bucket.Kind, bucket.TargetCount, limits)))
            .ToArray();

        var sharePlans = shareBuckets.Values
            .OrderBy(bucket => bucket.ParentKey, StringComparer.Ordinal)
            .ThenBy(bucket => bucket.Key, StringComparer.Ordinal)
            .Select(bucket => bucket.ToPlan(GetBucketLimit(ParallelismGroupKind.UncShare, bucket.TargetCount, limits)))
            .ToArray();

        var groups = topLevelPlans
            .Concat(sharePlans)
            .ToArray();

        var globalLimit = Math.Min(
            targets.Length,
            Math.Min(limits.GlobalCap, CalculateGlobalLimit(topLevelPlans, sharePlans)));
        if (globalLimit == 0)
        {
            globalLimit = 1;
        }

        return new ParallelismPlan(
            operation,
            targets.Length,
            globalLimit,
            targetPlans,
            groups,
            BuildReason(operation, targets.Length, globalLimit, limits, topLevelPlans, sharePlans));
    }

    private static BucketBuilder GetOrCreateBucket(
        IDictionary<string, BucketBuilder> buckets,
        ParallelismGroupKind kind,
        string key,
        string displayName,
        string? parentKey)
    {
        if (!buckets.TryGetValue(key, out var bucket))
        {
            bucket = new BucketBuilder(kind, key, displayName, parentKey);
            buckets.Add(key, bucket);
        }

        return bucket;
    }

    private static ParallelismLimits GetLimits(AdaptiveParallelismOperation operation)
        => operation switch
        {
            AdaptiveParallelismOperation.DashboardLoad => new ParallelismLimits(
                DashboardLoadGlobalCap,
                DashboardLoadUncHostLimit,
                UncShareLimit,
                LocalRootLimit,
                UnknownLimit),
            AdaptiveParallelismOperation.DiskSearch or
            AdaptiveParallelismOperation.FilterApply => new ParallelismLimits(
                SearchAndFilterGlobalCap,
                SearchAndFilterUncHostLimit,
                UncShareLimit,
                LocalRootLimit,
                UnknownLimit),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };

    private static int GetBucketLimit(
        ParallelismGroupKind kind,
        int targetCount,
        ParallelismLimits limits)
        => kind switch
        {
            ParallelismGroupKind.Unknown => Math.Min(limits.UnknownLimit, targetCount),
            ParallelismGroupKind.LocalRoot => Math.Min(limits.LocalRootLimit, targetCount),
            ParallelismGroupKind.UncHost => Math.Min(limits.UncHostLimit, targetCount),
            ParallelismGroupKind.UncShare => Math.Min(limits.UncShareLimit, targetCount),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    private static int CalculateGlobalLimit(
        IReadOnlyCollection<ParallelismGroupPlan> topLevelPlans,
        IReadOnlyCollection<ParallelismGroupPlan> sharePlans)
    {
        var shareBudgetByHost = sharePlans
            .Where(plan => plan.ParentKey is not null)
            .GroupBy(plan => plan.ParentKey!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(plan => plan.Limit),
                StringComparer.Ordinal);

        return topLevelPlans.Sum(plan =>
            plan.Kind == ParallelismGroupKind.UncHost &&
            shareBudgetByHost.TryGetValue(plan.Key, out var shareBudget)
                ? Math.Min(plan.Limit, shareBudget)
                : plan.Limit);
    }

    private static string BuildReason(
        AdaptiveParallelismOperation operation,
        int targetCount,
        int globalLimit,
        ParallelismLimits limits,
        IReadOnlyCollection<ParallelismGroupPlan> topLevelPlans,
        IReadOnlyCollection<ParallelismGroupPlan> sharePlans)
    {
        var topLevelSummary = BuildTopLevelSummary(topLevelPlans);
        var shareSummary = sharePlans.Count == 0
            ? string.Empty
            : $", {sharePlans.Count} UNC share{PluralSuffix(sharePlans.Count)}";

        return $"{operation}: {targetCount} target{PluralSuffix(targetCount)} across {topLevelSummary}{shareSummary}; global limit {globalLimit} (cap {limits.GlobalCap}).";
    }

    private static string BuildTopLevelSummary(IReadOnlyCollection<ParallelismGroupPlan> topLevelPlans)
    {
        if (topLevelPlans.Count == 0)
        {
            return "no grouped targets";
        }

        var parts = new List<string>();
        AddSummaryPart(parts, topLevelPlans.Count(plan => plan.Kind == ParallelismGroupKind.UncHost), "UNC host");
        AddSummaryPart(parts, topLevelPlans.Count(plan => plan.Kind == ParallelismGroupKind.LocalRoot), "local root");
        AddSummaryPart(parts, topLevelPlans.Count(plan => plan.Kind == ParallelismGroupKind.Unknown), "unknown bucket");

        return string.Join(", ", parts);
    }

    private static void AddSummaryPart(List<string> parts, int count, string label)
    {
        if (count > 0)
        {
            parts.Add($"{count} {label}{PluralSuffix(count)}");
        }
    }

    private static string PluralSuffix(int count) => count == 1 ? string.Empty : "s";

    private static TargetClassification Classify(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return TargetClassification.Unknown;
        }

        var normalizedPath = NormalizeForClassification(path);
        if (!Path.IsPathFullyQualified(normalizedPath))
        {
            return TargetClassification.Unknown;
        }

        string? root;
        try
        {
            root = Path.GetPathRoot(normalizedPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return TargetClassification.Unknown;
        }

        if (string.IsNullOrWhiteSpace(root))
        {
            return TargetClassification.Unknown;
        }

        if (root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var uncRoot = Path.TrimEndingDirectorySeparator(root);
            if (!TryParseUncRoot(uncRoot, out var host, out var share))
            {
                return TargetClassification.Unknown;
            }

            return TargetClassification.FromUnc(host, share);
        }

        if (IsLocalRoot(root))
        {
            return TargetClassification.FromLocalRoot(NormalizeLocalRoot(root));
        }

        return TargetClassification.Unknown;
    }

    private static string NormalizeForClassification(string path)
    {
        var normalized = path.Trim().Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

        if (normalized.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + normalized[8..];
        }

        if (normalized.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            return normalized[4..];
        }

        return normalized;
    }

    private static bool TryParseUncRoot(string uncRoot, out string host, out string share)
    {
        host = string.Empty;
        share = string.Empty;

        if (uncRoot.Length <= 2)
        {
            return false;
        }

        var segments = uncRoot[2..].Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return false;
        }

        host = segments[0];
        share = segments[1];
        return !string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(share);
    }

    private static bool IsLocalRoot(string root)
        => root.Length >= 3 && root[1] == ':' && root[2] == Path.DirectorySeparatorChar;

    private static string NormalizeLocalRoot(string root)
        => string.Concat(char.ToUpperInvariant(root[0]), root[1..]);

    private static string NormalizeUncHost(string host)
        => $@"\\{host.ToLowerInvariant()}";

    private static string NormalizeUncShare(string host, string share)
        => $@"\\{host.ToLowerInvariant()}\{share.ToLowerInvariant()}";

    private static string NormalizeUncHostDisplay(string host)
        => $@"\\{host}";

    private static string NormalizeUncShareDisplay(string host, string share)
        => $@"\\{host}\{share}";

    private sealed class BucketBuilder
    {
        public BucketBuilder(
            ParallelismGroupKind kind,
            string key,
            string displayName,
            string? parentKey)
        {
            Kind = kind;
            Key = key;
            DisplayName = displayName;
            ParentKey = parentKey;
        }

        public ParallelismGroupKind Kind { get; }
        public string Key { get; }
        public string DisplayName { get; }
        public string? ParentKey { get; }

        public int TargetCount { get; private set; }

        public void AddTarget() => TargetCount++;

        public ParallelismGroupPlan ToPlan(int limit)
            => new(Kind, Key, DisplayName, TargetCount, limit, ParentKey);
    }

    private readonly record struct ParallelismLimits(
        int GlobalCap,
        int UncHostLimit,
        int UncShareLimit,
        int LocalRootLimit,
        int UnknownLimit);

    private sealed record TargetClassification(
        ParallelismGroupKind TopLevelKind,
        string TopLevelKey,
        string TopLevelDisplayName,
        string? ShareKey,
        string? ShareDisplayName)
    {
        public static TargetClassification Unknown { get; } = new(
            ParallelismGroupKind.Unknown,
            "unknown",
            "unknown",
            null,
            null);

        public static TargetClassification FromLocalRoot(string root) => new(
            ParallelismGroupKind.LocalRoot,
            root,
            root,
            null,
            null);

        public static TargetClassification FromUnc(string host, string share) => new(
            ParallelismGroupKind.UncHost,
            NormalizeUncHost(host),
            NormalizeUncHostDisplay(host),
            NormalizeUncShare(host, share),
            NormalizeUncShareDisplay(host, share));
    }
}

internal enum AdaptiveParallelismOperation
{
    DashboardLoad,
    DiskSearch,
    FilterApply
}

internal enum ParallelismGroupKind
{
    Unknown,
    LocalRoot,
    UncHost,
    UncShare
}

internal sealed record ParallelismGroupPlan(
    ParallelismGroupKind Kind,
    string Key,
    string DisplayName,
    int TargetCount,
    int Limit,
    string? ParentKey);

internal sealed record ParallelismTargetPlan(
    string Path,
    string TopLevelGroupKey,
    string? SecondaryGroupKey);

internal sealed record ParallelismPlan(
    AdaptiveParallelismOperation Operation,
    int TargetCount,
    int GlobalLimit,
    IReadOnlyList<ParallelismTargetPlan> Targets,
    IReadOnlyList<ParallelismGroupPlan> Groups,
    string Reason);
