using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.Core.Models;

namespace LogReader.Tests;

public sealed class DashboardModifierCacheRegressionTests
{
    [Fact]
    public void ReviewRepair_CacheReconciliationPreservesOrderSharedPathsAndOrdinalIds()
    {
        var service = new DashboardModifierService();
        service.SetDashboardModifier("dashboard", 1, Array.Empty<ReplacementPattern>());
        var revision = service.GetDashboardRevision("dashboard");
        service.ApplyDashboardMembers("dashboard", revision, new[]
        {
            new ResolvedModifierMember("file", @"C:\logs\shared.log", null),
            new ResolvedModifierMember("FILE", @"c:\LOGS\shared.log", null),
            new ResolvedModifierMember("other", @"C:\logs\other.log", null)
        });

        service.ReconcileDashboardMembers("dashboard", revision, new[] { "other", "FILE", "new" });
        Assert.Equal(new[] { @"C:\logs\other.log", @"c:\LOGS\shared.log" }, service.GetDashboardOpenTargets("dashboard"));
        Assert.True(service.TryGetDashboardEffectivePaths("dashboard", out var paths));
        Assert.Equal(2, paths.Count);
        Assert.Contains(@"C:\logs\shared.log", paths);

        service.ReconcileDashboardMembers("dashboard", revision, new[] { "file" });
        Assert.Empty(service.GetDashboardOpenTargets("dashboard"));
        Assert.True(service.TryGetDashboardEffectivePaths("dashboard", out paths));
        Assert.Empty(paths);
    }

    [Theory]
    [InlineData("reorder")]
    [InlineData("delete")]
    [InlineData("replace")]
    [InlineData("modifier")]
    public void ReviewRepair_CapturedRefreshRequiresCurrentMembershipAndModifier(string mutation)
    {
        var group = new LogGroupViewModel(new LogGroup
        {
            Id = "dashboard", Kind = LogGroupKind.Dashboard, FileIds = new List<string> { "first", "second" }
        }, _ => Task.CompletedTask);
        var groups = new List<LogGroupViewModel> { group };
        var service = new DashboardModifierService();
        service.SetDashboardModifier(group.Id, 1,
            new[] { new ReplacementPattern { FindPattern = ".log", ReplacePattern = "-{yyyyMMdd}.log" } });
        var captured = service.CaptureRefresh(groups, new Dictionary<string, string>
        {
            ["first"] = @"C:\logs\first.log", ["second"] = @"C:\logs\second.log"
        }, includeAdHoc: false);
        var snapshot = captured.Resolve();
        switch (mutation)
        {
            case "reorder": group.Model.FileIds.Reverse(); break;
            case "delete": groups.Clear(); break;
            case "replace": groups[0] = new LogGroupViewModel(group.Model, _ => Task.CompletedTask); break;
            case "modifier": service.SetDashboardModifier(group.Id, 2, Array.Empty<ReplacementPattern>()); break;
        }
        service.ApplyDashboardMembers(group.Id, service.GetDashboardRevision(group.Id),
            new[] { new ResolvedModifierMember("second", @"C:\logs\current.log", null) });
        captured.Apply(snapshot);
        Assert.Equal(new[] { @"C:\logs\current.log" }, service.GetDashboardOpenTargets(group.Id));
    }

    [Fact]
    public void ReviewRepair_CacheReconciliationCannotOverwriteNewModifier()
    {
        var service = new DashboardModifierService();
        service.SetDashboardModifier("dashboard", 1, Array.Empty<ReplacementPattern>());
        var oldRevision = service.GetDashboardRevision("dashboard");
        service.SetDashboardModifier("dashboard", 2, Array.Empty<ReplacementPattern>());
        service.ApplyDashboardMembers("dashboard", service.GetDashboardRevision("dashboard"),
            new[] { new ResolvedModifierMember("file", @"C:\logs\current.log", null) });
        service.ReconcileDashboardMembers("dashboard", oldRevision, Array.Empty<string>());
        Assert.Equal(new[] { @"C:\logs\current.log" }, service.GetDashboardOpenTargets("dashboard"));
    }

    [Fact]
    public void ReviewRepair_CapturedRefreshCannotRestoreRemovedMembers()
    {
        var group = new LogGroupViewModel(new LogGroup
        {
            Id = "dashboard", Kind = LogGroupKind.Dashboard, FileIds = new List<string> { "first", "second" }
        }, _ => Task.CompletedTask);
        var service = new DashboardModifierService();
        service.SetDashboardModifier(group.Id, 1,
            new[] { new ReplacementPattern { FindPattern = ".log", ReplacePattern = "-{yyyyMMdd}.log" } });
        var captured = service.CaptureRefresh(new[] { group }, new Dictionary<string, string>
        {
            ["first"] = @"C:\logs\first.log", ["second"] = @"C:\logs\second.log"
        }, includeAdHoc: false);
        var snapshot = captured.Resolve();
        captured.Apply(snapshot);
        group.Model.FileIds.Remove("first");
        var survivor = snapshot.DashboardMembers[group.Id].Where(member => member.BaseKey == "second").ToArray();
        service.ApplyDashboardMembers(group.Id, service.GetDashboardRevision(group.Id), survivor);
        captured.Apply(snapshot);
        Assert.Equal(new[] { survivor[0].EffectivePath }, service.GetDashboardOpenTargets(group.Id));
        Assert.True(service.TryGetDashboardEffectivePaths(group.Id, out var paths));
        Assert.Equal(new[] { survivor[0].EffectivePath }, paths);
    }
}
