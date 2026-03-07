using LogReader.Core.Models;
using LogReader.Infrastructure.Repositories;

namespace LogReader.Tests;

public class JsonSessionRepositoryTests : IAsyncLifetime
{
    private string _testDir = null!;

    public Task InitializeAsync()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "LogReaderSessionTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_testDir);
        JsonStore.SetBasePathForTests(_testDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        JsonStore.SetBasePathForTests(null);
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task LoadAsync_NoFile_ReturnsDefaults()
    {
        var repo = new JsonSessionRepository();

        var session = await repo.LoadAsync();

        Assert.Empty(session.OpenTabs);
        Assert.Null(session.ActiveTabId);
    }

    [Fact]
    public async Task SaveLoad_RoundTrip_PersistsSessionState()
    {
        var repo = new JsonSessionRepository();
        var expected = new SessionState
        {
            ActiveTabId = "tab-2",
            OpenTabs = new List<OpenTabState>
            {
                new()
                {
                    FileId = "tab-1",
                    FilePath = @"C:\logs\a.log",
                    Encoding = FileEncoding.Utf8Bom,
                    AutoScrollEnabled = true,
                    IsPinned = false
                },
                new()
                {
                    FileId = "tab-2",
                    FilePath = @"C:\logs\b.log",
                    Encoding = FileEncoding.Utf16Be,
                    AutoScrollEnabled = false,
                    IsPinned = true
                }
            }
        };

        await repo.SaveAsync(expected);
        var loaded = await repo.LoadAsync();

        Assert.Equal(expected.ActiveTabId, loaded.ActiveTabId);
        Assert.Equal(2, loaded.OpenTabs.Count);
        Assert.Equal(FileEncoding.Utf8Bom, loaded.OpenTabs[0].Encoding);
        Assert.Equal(FileEncoding.Utf16Be, loaded.OpenTabs[1].Encoding);
        Assert.True(loaded.OpenTabs[1].IsPinned);
        Assert.False(loaded.OpenTabs[1].AutoScrollEnabled);
    }
}
