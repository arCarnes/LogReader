namespace LogReader.Core;

using System.Diagnostics;

internal static class AdaptiveParallelismDiagnostics
{
    internal static void WritePlan(ParallelismPlan plan)
        => Debug.WriteLine(BuildPlanDiagnostic(plan));

    internal static string BuildOperationStatus(
        string verb,
        int targetCount,
        string targetLabel,
        ParallelismPlan plan)
    {
        var targetText = targetCount == 1 ? targetLabel : $"{targetLabel}s";
        return $"{verb} {targetCount:N0} {targetText} {BuildWorkerSummary(plan)}...";
    }

    internal static string BuildWorkerSummary(ParallelismPlan plan)
    {
        var workerText = plan.GlobalLimit == 1 ? "worker" : "workers";
        return $"with {plan.GlobalLimit:N0} {workerText} {BuildGroupSummary(plan)}";
    }

    internal static string BuildPlanDiagnostic(ParallelismPlan plan)
    {
        var uncHosts = GetGroups(plan, ParallelismGroupKind.UncHost).ToArray();
        var uncShares = GetGroups(plan, ParallelismGroupKind.UncShare).ToArray();
        var localRoots = GetGroups(plan, ParallelismGroupKind.LocalRoot).ToArray();
        var unknownGroups = GetGroups(plan, ParallelismGroupKind.Unknown).ToArray();

        return
            $"AdaptiveParallelism {plan.Operation}: targets={plan.TargetCount}, globalLimit={plan.GlobalLimit}, " +
            $"uncHosts={uncHosts.Length}, uncShares={uncShares.Length}, localRoots={localRoots.Length}, unknownGroups={unknownGroups.Length}, " +
            $"assignments={plan.Targets.Count}, hostLimits=[{BuildLimitList(uncHosts)}], shareLimits=[{BuildLimitList(uncShares)}], " +
            $"localRootLimits=[{BuildLimitList(localRoots)}], unknownLimits=[{BuildLimitList(unknownGroups)}], reason=\"{plan.Reason}\"";
    }

    private static string BuildGroupSummary(ParallelismPlan plan)
    {
        var uncHostCount = plan.Groups.Count(group => group.Kind == ParallelismGroupKind.UncHost);
        var localRootCount = plan.Groups.Count(group => group.Kind == ParallelismGroupKind.LocalRoot);
        var unknownCount = plan.Groups.Count(group => group.Kind == ParallelismGroupKind.Unknown);
        var parts = new List<string>();
        AddGroupSummaryPart(parts, uncHostCount, "host");
        AddGroupSummaryPart(parts, localRootCount, "local root");
        AddGroupSummaryPart(parts, unknownCount, "unknown group");

        return parts.Count == 0
            ? "across no targets"
            : $"across {string.Join(", ", parts)}";
    }

    private static IEnumerable<ParallelismGroupPlan> GetGroups(ParallelismPlan plan, ParallelismGroupKind kind)
        => plan.Groups
            .Where(group => group.Kind == kind)
            .OrderBy(group => group.Key, StringComparer.Ordinal);

    private static string BuildLimitList(IReadOnlyCollection<ParallelismGroupPlan> groups)
        => groups.Count == 0
            ? "none"
            : string.Join(", ", groups.Select(group => $"{group.DisplayName}:{group.Limit}/{group.TargetCount}"));

    private static void AddGroupSummaryPart(List<string> parts, int count, string label)
    {
        if (count > 0)
            parts.Add($"{count:N0} {label}{(count == 1 ? string.Empty : "s")}");
    }
}
