namespace LogReader.Tests;

using System.IO;
using System.Text;
using LogReader.Core.Models;
using LogReader.Core.Tests.UncPerformance;
using LogReader.Tests.UncPerformance;

public sealed class UncPerformanceDesktopTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [Trait("Category", "InteractiveDesktop")]
    public async Task DashboardWindow_LoadsPublishesSearchesFiltersScrollsAndDrains(int count)
    {
        InteractiveDesktop.RequireAvailable();
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "WeezTail-ui-performance-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var drained = false;
        try
        {
            var files = new List<FixtureFile>();
            for (var file = 0; file < count; file++)
            {
                var path = Path.Combine(root, $"numbered-{file:D3}.log");
                await File.WriteAllTextAsync(path, string.Concat(Enumerable.Range(1, 250).Select(i =>
                    $"line {i:D3}{(i == 125 ? " needle" : string.Empty)}\n")), new UTF8Encoding(false));
                files.Add(await FixtureStore.CaptureAsync($"local-{file:D3}", path, FileEncoding.Utf8, true, default));
            }
            var samples = await UiWorkflowMeasurements.RunScopeAsync($"local-{count}", files.ToArray(), Path.Combine(root, "State"), default);
            drained = true;
            Assert.Equal(6, samples.Count);
            Assert.Equal(250 * count, samples.Single(s => s.Operation == "dashboard-load").Units);
            Assert.Equal(100 * count, samples.Single(s => s.Operation == "indexed-read-100").Units);
            Assert.Equal(count, samples.Single(s => s.Operation == "ui-search").Units);
            Assert.Equal(count, samples.Single(s => s.Operation == "ui-filter").Units);
        }
        finally { if (drained) Directory.Delete(root, recursive: true); }
    }
}
