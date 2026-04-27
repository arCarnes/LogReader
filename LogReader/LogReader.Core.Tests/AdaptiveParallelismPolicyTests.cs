namespace LogReader.Core.Tests;

using LogReader.Core;

public class AdaptiveParallelismPolicyTests
{
    [Fact]
    public void CreatePlan_SingleTarget_ReturnsSerialPlan()
    {
        var plan = AdaptiveParallelismPolicy.CreatePlan(
            AdaptiveParallelismOperation.DashboardLoad,
            new[] { @"C:\logs\app.log" });

        Assert.Equal(1, plan.TargetCount);
        Assert.Equal(1, plan.GlobalLimit);

        var target = Assert.Single(plan.Targets);
        Assert.Equal(@"C:\logs\app.log", target.Path);
        Assert.Equal(@"C:\", target.TopLevelGroupKey);
        Assert.Null(target.SecondaryGroupKey);

        var rootGroup = Assert.Single(plan.Groups);
        Assert.Equal(ParallelismGroupKind.LocalRoot, rootGroup.Kind);
        Assert.Equal(@"C:\", rootGroup.Key);
        Assert.Equal(1, rootGroup.TargetCount);
        Assert.Equal(1, rootGroup.Limit);
    }

    [Fact]
    public void CreatePlan_OneUncHost_UsesHostAndShareBuckets()
    {
        var plan = AdaptiveParallelismPolicy.CreatePlan(
            AdaptiveParallelismOperation.DashboardLoad,
            new[]
            {
                UncPath("server", "share-a", "a.log"),
                UncPath("server", "share-a", "b.log"),
                UncPath("server", "share-b", "c.log"),
                UncPath("server", "share-b", "d.log"),
                UncPath("server", "share-b", "e.log")
            });

        Assert.Equal(5, plan.TargetCount);
        Assert.Equal(2, plan.GlobalLimit);
        Assert.All(plan.Targets, target => Assert.Equal(@"\\server", target.TopLevelGroupKey));
        Assert.Equal(
            new[] { @"\\server\share-a", @"\\server\share-a", @"\\server\share-b", @"\\server\share-b", @"\\server\share-b" },
            plan.Targets.Select(target => target.SecondaryGroupKey).ToArray());

        var hostGroup = Assert.Single(plan.Groups.Where(group => group.Kind == ParallelismGroupKind.UncHost));
        var shareGroups = plan.Groups.Where(group => group.Kind == ParallelismGroupKind.UncShare).ToArray();

        Assert.Equal(@"\\server", hostGroup.Key);
        Assert.Equal(5, hostGroup.TargetCount);
        Assert.Equal(2, hostGroup.Limit);
        Assert.Equal(2, shareGroups.Length);

        Assert.Contains(shareGroups, group =>
            group.Key == @"\\server\share-a" &&
            group.ParentKey == @"\\server" &&
            group.TargetCount == 2 &&
            group.Limit == 2);

        Assert.Contains(shareGroups, group =>
            group.Key == @"\\server\share-b" &&
            group.ParentKey == @"\\server" &&
            group.TargetCount == 3 &&
            group.Limit == 2);
    }

    [Fact]
    public void CreatePlan_MultipleUncHosts_CanExceedHistoricalEight()
    {
        var targets = Enumerable.Range(1, 5)
            .SelectMany(hostIndex => new[]
            {
                UncPath($"server{hostIndex}", "share", $"a{hostIndex}.log"),
                UncPath($"server{hostIndex}", "share", $"b{hostIndex}.log")
            })
            .ToArray();

        var plan = AdaptiveParallelismPolicy.CreatePlan(
            AdaptiveParallelismOperation.DiskSearch,
            targets);

        Assert.Equal(10, plan.TargetCount);
        Assert.Equal(10, plan.GlobalLimit);
        Assert.Equal(5, plan.Groups.Count(group => group.Kind == ParallelismGroupKind.UncHost));
        Assert.All(
            plan.Groups.Where(group => group.Kind == ParallelismGroupKind.UncHost),
            group => Assert.Equal(2, group.Limit));
    }

    [Fact]
    public void CreatePlan_SingleUncShare_DoesNotExceedShareBudget()
    {
        var plan = AdaptiveParallelismPolicy.CreatePlan(
            AdaptiveParallelismOperation.DiskSearch,
            new[]
            {
                UncPath("server", "share", "one.log"),
                UncPath("server", "share", "two.log"),
                UncPath("server", "share", "three.log")
            });

        Assert.Equal(3, plan.TargetCount);
        Assert.Equal(2, plan.GlobalLimit);

        var hostGroup = Assert.Single(plan.Groups.Where(group => group.Kind == ParallelismGroupKind.UncHost));
        var shareGroup = Assert.Single(plan.Groups.Where(group => group.Kind == ParallelismGroupKind.UncShare));

        Assert.Equal(3, hostGroup.Limit);
        Assert.Equal(2, shareGroup.Limit);
    }

    [Fact]
    public void CreatePlan_LocalRoots_UseSeparateRootBudgets()
    {
        var plan = AdaptiveParallelismPolicy.CreatePlan(
            AdaptiveParallelismOperation.DashboardLoad,
            new[]
            {
                @"C:\logs\one.log",
                @"C:\logs\two.log",
                @"D:\logs\three.log",
                @"D:\logs\four.log"
            });

        Assert.Equal(4, plan.TargetCount);
        Assert.Equal(4, plan.GlobalLimit);

        var roots = plan.Groups.Where(group => group.Kind == ParallelismGroupKind.LocalRoot).ToArray();
        Assert.Equal(2, roots.Length);
        Assert.Contains(roots, group => group.Key == @"C:\" && group.TargetCount == 2 && group.Limit == 2);
        Assert.Contains(roots, group => group.Key == @"D:\" && group.TargetCount == 2 && group.Limit == 2);
    }

    [Fact]
    public void CreatePlan_RelativeAndMalformedPaths_FallBackToUnknown()
    {
        var plan = AdaptiveParallelismPolicy.CreatePlan(
            AdaptiveParallelismOperation.FilterApply,
            new[]
            {
                @"relative\path.log",
                string.Empty,
                "not-a-path"
            });

        var unknownGroup = Assert.Single(plan.Groups.Where(group => group.Kind == ParallelismGroupKind.Unknown));
        Assert.Equal(3, unknownGroup.TargetCount);
        Assert.Equal(1, unknownGroup.Limit);
        Assert.Equal(1, plan.GlobalLimit);
    }

    [Fact]
    public void CreatePlan_DashboardAndSearchProduceDifferentLimitsForSameTargets()
    {
        var targets = new[]
        {
            UncPath("server", "share", "one.log"),
            UncPath("server", "share", "two.log"),
            UncPath("server", "share-alt", "three.log"),
            UncPath("server", "share-alt", "four.log")
        };

        var dashboardPlan = AdaptiveParallelismPolicy.CreatePlan(
            AdaptiveParallelismOperation.DashboardLoad,
            targets);

        var searchPlan = AdaptiveParallelismPolicy.CreatePlan(
            AdaptiveParallelismOperation.DiskSearch,
            targets);

        Assert.Equal(2, dashboardPlan.GlobalLimit);
        Assert.Equal(3, searchPlan.GlobalLimit);
        Assert.NotEqual(dashboardPlan.GlobalLimit, searchPlan.GlobalLimit);
    }

    [Fact]
    public void BuildOperationStatus_UsesConciseWorkerAndGroupSummary()
    {
        var targets = Enumerable.Range(1, 5)
            .SelectMany(hostIndex => new[]
            {
                UncPath($"server{hostIndex}", "share", $"a{hostIndex}.log"),
                UncPath($"server{hostIndex}", "share", $"b{hostIndex}.log")
            })
            .ToArray();
        var plan = AdaptiveParallelismPolicy.CreatePlan(
            AdaptiveParallelismOperation.DiskSearch,
            targets);

        var status = AdaptiveParallelismDiagnostics.BuildOperationStatus(
            "Searching",
            targets.Length,
            "file",
            plan);

        Assert.Equal("Searching 10 files with 10 workers across 5 hosts...", status);
    }

    [Fact]
    public void BuildPlanDiagnostic_IncludesCountsLimitsAndReasonWithoutTargets()
    {
        var targets = new[]
        {
            UncPath("server", "share-a", "one.log"),
            UncPath("server", "share-b", "two.log"),
            @"C:\logs\three.log",
            "relative.log"
        };
        var plan = AdaptiveParallelismPolicy.CreatePlan(
            AdaptiveParallelismOperation.FilterApply,
            targets);

        var diagnostic = AdaptiveParallelismDiagnostics.BuildPlanDiagnostic(plan);

        Assert.Contains("AdaptiveParallelism FilterApply", diagnostic, StringComparison.Ordinal);
        Assert.Contains("targets=4", diagnostic, StringComparison.Ordinal);
        Assert.Contains("uncHosts=1", diagnostic, StringComparison.Ordinal);
        Assert.Contains("uncShares=2", diagnostic, StringComparison.Ordinal);
        Assert.Contains("localRoots=1", diagnostic, StringComparison.Ordinal);
        Assert.Contains("unknownGroups=1", diagnostic, StringComparison.Ordinal);
        Assert.Contains("assignments=4", diagnostic, StringComparison.Ordinal);
        Assert.Contains(@"\\server:2/2", diagnostic, StringComparison.Ordinal);
        Assert.Contains(@"\\server\share-a:1/1", diagnostic, StringComparison.Ordinal);
        Assert.Contains(@"C:\:1/1", diagnostic, StringComparison.Ordinal);
        Assert.Contains("reason=\"FilterApply:", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("relative.log", diagnostic, StringComparison.Ordinal);
    }

    private static string UncPath(string host, string share, string fileName)
        => $@"\\{host}\{share}\{fileName}";
}
