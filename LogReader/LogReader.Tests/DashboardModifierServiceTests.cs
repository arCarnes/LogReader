using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.Core.Models;

namespace LogReader.Tests;

public sealed class DashboardModifierServiceTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "LogReaderDashboardModifierServiceTests_" + Guid.NewGuid().ToString("N")[..8]);

    public DashboardModifierServiceTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, true);
    }

    [Fact]
    public void ResolveRefreshSnapshot_TracksEffectivePathsAndSyncsModifierLabels()
    {
        var basePath = Path.Combine(_tempDirectory, "service.log");
        var effectivePath = basePath + DateTime.Today.AddDays(-1).ToString("yyyyMMdd");
        File.WriteAllText(effectivePath, "shifted");

        var dashboard = new LogGroupViewModel(
            new LogGroup
            {
                Id = "dashboard-1",
                Name = "Dashboard",
                Kind = LogGroupKind.Dashboard,
                FileIds = new List<string> { "file-1" }
            },
            _ => Task.CompletedTask);

        var service = new DashboardModifierService();
        service.SetDashboardModifier(
            dashboard.Id,
            daysBack: 1,
            new[]
            {
                new ReplacementPattern
                {
                    FindPattern = ".log",
                    ReplacePattern = ".log{yyyyMMdd}"
                }
            });

        var snapshot = service.ResolveRefreshSnapshot(
            new[] { dashboard },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["file-1"] = basePath
            });

        var member = Assert.Single(snapshot.DashboardMembers[dashboard.Id]);
        Assert.Equal(effectivePath, member.EffectivePath, ignoreCase: true);
        Assert.True(snapshot.ModifiedPaths.Contains(effectivePath));

        Assert.True(service.TryGetDashboardEffectivePaths(dashboard.Id, out var effectivePaths));
        Assert.Contains(effectivePaths, path => string.Equals(path, effectivePath, StringComparison.OrdinalIgnoreCase));

        service.SyncModifierLabels(new[] { dashboard });
        Assert.Equal("T-1", dashboard.ModifierLabel);
    }
}
