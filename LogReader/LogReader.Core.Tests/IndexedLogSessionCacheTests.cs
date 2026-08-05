namespace LogReader.Core.Tests;

using LogReader.Core;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

public sealed class IndexedLogSessionCacheTests : IAsyncLifetime
{
    private string _testDirectory = null!;
    private IDisposable? _appPathsScope;

    public Task InitializeAsync()
    {
        _testDirectory = Path.Combine(
            Path.GetTempPath(),
            $"WeezTailHeadlessCache_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _appPathsScope = AppPaths.BeginTestScope(rootPath: _testDirectory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _appPathsScope?.Dispose();
        if (Directory.Exists(_testDirectory))
            Directory.Delete(_testDirectory, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Acquire_ReusesNormalizedPathAndResolvedEncoding()
    {
        var path = await CreateFileAsync("reuse.log", "one\ntwo");
        using var cache = CreateCache();

        using (var first = cache.Acquire(Path.Combine(_testDirectory, ".", "reuse.log")))
        {
            var count = await first.UseCurrentIndexAsync(
                static (index, _, _) => Task.FromResult(index.LineCount));
            Assert.Equal(2, count);
        }

        using (var second = cache.Acquire(path, FileEncoding.Utf8))
        {
            var count = await second.UseCurrentIndexAsync(
                static (index, _, _) => Task.FromResult(index.LineCount));
            Assert.Equal(2, count);
        }

        var snapshot = cache.GetSnapshot();
        Assert.Equal(0, snapshot.ActiveSessions);
        Assert.Equal(1, snapshot.RetainedSessions);
        Assert.Equal(2, snapshot.MappedLineOffsets);
    }

    [Fact]
    public async Task SweepExpiredSessions_DisposesRetainedIndexMapping()
    {
        var path = await CreateFileAsync("expire.log", "one\ntwo");
        var now = DateTime.UtcNow;
        using var cache = new IndexedLogSessionCache(
            new ChunkedLogReaderService(),
            new FileEncodingDetectionService(),
            new IndexedLogSessionCacheOptions
            {
                MaximumSessions = 2,
                MaximumMappedLineOffsets = 100,
                WarmRetentionDuration = TimeSpan.FromSeconds(5)
            },
            () => now);

        using (var lease = cache.Acquire(path))
        {
            await lease.UseCurrentIndexAsync(
                static (index, _, _) => Task.FromResult(index.LineCount));
        }

        Assert.Single(GetIndexFiles());
        now = now.AddSeconds(6);

        Assert.Equal(1, cache.SweepExpiredSessions());
        Assert.Empty(GetIndexFiles());
    }

    [Fact]
    public async Task Acquire_EvictsLeastRecentlyUsedWarmSessionAtCapacity()
    {
        var firstPath = await CreateFileAsync("first.log", "first");
        var secondPath = await CreateFileAsync("second.log", "second");
        using var cache = CreateCache(maximumSessions: 1);

        using (var first = cache.Acquire(firstPath))
        {
            await first.UseCurrentIndexAsync(
                static (index, _, _) => Task.FromResult(index.LineCount));
        }

        using (var second = cache.Acquire(secondPath))
        {
            await second.UseCurrentIndexAsync(
                static (index, _, _) => Task.FromResult(index.LineCount));
        }

        var snapshot = cache.GetSnapshot();
        Assert.Equal(1, snapshot.RetainedSessions);
        Assert.Equal(1, snapshot.MappedLineOffsets);
        Assert.Single(GetIndexFiles());
    }

    [Fact]
    public async Task Acquire_RejectsWhenAllSessionSlotsAreLeased()
    {
        var firstPath = await CreateFileAsync("active.log", "first");
        var secondPath = await CreateFileAsync("blocked.log", "second");
        using var cache = CreateCache(maximumSessions: 1);
        using var active = cache.Acquire(firstPath);

        Assert.Throws<IndexedLogSessionCapacityExceededException>(
            () => cache.Acquire(secondPath));
    }

    [Fact]
    public async Task AggregateOffsetBudget_IsEnforcedDuringSecondBuild()
    {
        var firstPath = await CreateFileAsync("three-a.log", "one\ntwo\nthree");
        var secondPath = await CreateFileAsync("three-b.log", "one\ntwo\nthree");
        using var cache = CreateCache(maximumOffsets: 5);

        using (var first = cache.Acquire(firstPath))
        {
            await first.UseCurrentIndexAsync(
                static (index, _, _) => Task.FromResult(index.LineCount));
        }

        using var second = cache.Acquire(secondPath);
        var error = await Assert.ThrowsAsync<LineIndexCapacityExceededException>(
            () => second.UseCurrentIndexAsync(
                static (index, _, _) => Task.FromResult(index.LineCount)));

        Assert.Equal(2, error.MaximumLineCount);
        Assert.Equal(3, cache.GetSnapshot().MappedLineOffsets);
    }

    [Fact]
    public async Task CancelledOperation_ReleasesGateAndLeaseCanBeDisposed()
    {
        var path = await CreateFileAsync("cancel.log", "one\ntwo");
        using var cache = CreateCache(warmRetention: TimeSpan.Zero);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        using (var lease = cache.Acquire(path))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => lease.UseCurrentIndexAsync(
                    static (index, _, _) => Task.FromResult(index.LineCount),
                    cts.Token));
        }

        Assert.Equal(0, cache.GetSnapshot().ActiveSessions);
        Assert.Equal(0, cache.GetSnapshot().RetainedSessions);
    }

    [Fact]
    public async Task Dispose_DefersActiveSessionCleanupUntilLeaseRelease()
    {
        var path = await CreateFileAsync("active-dispose.log", "one\ntwo");
        var cache = CreateCache();
        var lease = cache.Acquire(path);
        await lease.UseCurrentIndexAsync(
            static (index, _, _) => Task.FromResult(index.LineCount));
        Assert.Single(GetIndexFiles());

        cache.Dispose();
        Assert.Single(GetIndexFiles());
        lease.Dispose();

        Assert.Empty(GetIndexFiles());
    }

    private IndexedLogSessionCache CreateCache(
        int maximumSessions = 4,
        int maximumOffsets = 100,
        TimeSpan? warmRetention = null)
        => new(
            new ChunkedLogReaderService(),
            new FileEncodingDetectionService(),
            new IndexedLogSessionCacheOptions
            {
                MaximumSessions = maximumSessions,
                MaximumMappedLineOffsets = maximumOffsets,
                WarmRetentionDuration = warmRetention ?? TimeSpan.FromMinutes(1)
            });

    private async Task<string> CreateFileAsync(string name, string contents)
    {
        var path = Path.Combine(_testDirectory, name);
        await File.WriteAllTextAsync(path, contents);
        return path;
    }

    private IReadOnlyList<string> GetIndexFiles()
        => Directory.Exists(AppPaths.IndexDirectory)
            ? Directory.GetFiles(
                AppPaths.IndexDirectory,
                "idx_*.bin",
                SearchOption.AllDirectories)
            : Array.Empty<string>();
}
