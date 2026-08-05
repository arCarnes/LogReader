namespace LogReader.Core.Tests;

using LogReader.Core.Models;

public sealed class LineIndexCacheOwnershipTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        $"WeezTailIndexOwnership_{Guid.NewGuid():N}");
    private readonly string _indexRoot;

    public LineIndexCacheOwnershipTests()
    {
        _indexRoot = Path.Combine(_testRoot, "Cache", AppPaths.IndexFolderName);
        Directory.CreateDirectory(_indexRoot);
    }

    [Fact]
    public void MappedOffsets_TwoLiveOwners_DoNotShareOrDeleteFiles()
    {
        using var firstOwner = LineIndexCacheOwner.Create(_indexRoot);
        using var secondOwner = LineIndexCacheOwner.Create(_indexRoot);
        using var firstOffsets = CreateFrozenOffsets(firstOwner);
        using var secondOffsets = CreateFrozenOffsets(secondOwner);

        var firstIndexPath = Assert.Single(Directory.GetFiles(firstOwner.DirectoryPath, "idx_*.bin"));
        var secondIndexPath = Assert.Single(Directory.GetFiles(secondOwner.DirectoryPath, "idx_*.bin"));
        Assert.NotEqual(firstIndexPath, secondIndexPath);

        firstOffsets.Dispose();

        Assert.False(File.Exists(firstIndexPath));
        Assert.True(File.Exists(secondIndexPath));
    }

    [Fact]
    public void Cleanup_SkipsLiveOwnerAndDeletesItAfterDisposal()
    {
        var owner = LineIndexCacheOwner.Create(_indexRoot);
        var ownerDirectory = owner.DirectoryPath;
        Directory.SetLastWriteTimeUtc(ownerDirectory, DateTime.UtcNow - TimeSpan.FromDays(2));

        var liveResult = LineIndexCacheMaintenance.CleanupOrphanedOwners(
            _indexRoot,
            minimumAge: TimeSpan.Zero);

        Assert.True(Directory.Exists(ownerDirectory));
        Assert.Equal(1, liveResult.LockedOwnerCount);

        owner.Dispose();
        var staleResult = LineIndexCacheMaintenance.CleanupOrphanedOwners(
            _indexRoot,
            minimumAge: TimeSpan.Zero);

        Assert.False(Directory.Exists(ownerDirectory));
        Assert.Equal(1, staleResult.DeletedOwnerCount);
    }

    [Fact]
    public void Cleanup_MalformedMetadataDoesNotOverrideUnlockedLifetimeEvidence()
    {
        var owner = LineIndexCacheOwner.Create(_indexRoot);
        var ownerDirectory = owner.DirectoryPath;
        owner.Dispose();
        File.WriteAllText(
            Path.Combine(ownerDirectory, LineIndexCacheOwner.MetadataFileName),
            "not-json");
        Directory.SetLastWriteTimeUtc(ownerDirectory, DateTime.UtcNow - TimeSpan.FromDays(2));

        var result = LineIndexCacheMaintenance.CleanupOrphanedOwners(
            _indexRoot,
            minimumAge: TimeSpan.Zero);

        Assert.Equal(1, result.DeletedOwnerCount);
        Assert.False(Directory.Exists(ownerDirectory));
    }

    [Fact]
    public void Cleanup_RemovesLegacyFlatIndexesWithoutVersionDirectory()
    {
        var legacyPath = Path.Combine(_indexRoot, "idx_legacy.bin");
        var unrelatedPath = Path.Combine(_indexRoot, "keep.txt");
        File.WriteAllText(legacyPath, "legacy");
        File.WriteAllText(unrelatedPath, "keep");

        var result = LineIndexCacheMaintenance.CleanupOrphanedOwners(_indexRoot);

        Assert.Equal(1, result.DeletedLegacyFileCount);
        Assert.False(File.Exists(legacyPath));
        Assert.True(File.Exists(unrelatedPath));
    }

    [Fact]
    public void Cleanup_SkipsLegacyReparsePointEvidence()
    {
        var legacyPath = Path.Combine(_indexRoot, "idx_link.bin");
        File.WriteAllText(legacyPath, "keep");

        var result = LineIndexCacheMaintenance.CleanupOrphanedOwners(
            _indexRoot,
            attributesProvider: path => string.Equals(path, legacyPath, StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.ReparsePoint
                : File.GetAttributes(path));

        Assert.Equal(1, result.SkippedOwnerCount);
        Assert.True(File.Exists(legacyPath));
    }

    [Fact]
    public void Create_PreExistingOwnerDirectoryIsNeverAdoptedOrDeleted()
    {
        var ownerId = Guid.NewGuid();
        var ownerDirectory = Path.Combine(
            _indexRoot,
            LineIndexCacheOwner.VersionDirectoryName,
            ownerId.ToString("N"));
        Directory.CreateDirectory(ownerDirectory);
        var sentinelPath = Path.Combine(ownerDirectory, "sentinel.txt");
        File.WriteAllText(sentinelPath, "do not delete");

        Assert.Throws<IOException>(() => LineIndexCacheOwner.Create(_indexRoot, ownerId));

        Assert.True(Directory.Exists(ownerDirectory));
        Assert.Equal("do not delete", File.ReadAllText(sentinelPath));
    }

    [Fact]
    public void Cleanup_SkipsUnexpectedFiles()
    {
        var owner = LineIndexCacheOwner.Create(_indexRoot);
        var ownerDirectory = owner.DirectoryPath;
        owner.Dispose();
        File.WriteAllText(Path.Combine(ownerDirectory, "do-not-delete.txt"), "unexpected");
        Directory.SetLastWriteTimeUtc(ownerDirectory, DateTime.UtcNow - TimeSpan.FromDays(2));

        var result = LineIndexCacheMaintenance.CleanupOrphanedOwners(
            _indexRoot,
            minimumAge: TimeSpan.Zero);

        Assert.Equal(1, result.SkippedOwnerCount);
        Assert.True(Directory.Exists(ownerDirectory));
    }

    [Fact]
    public void Cleanup_DoesNotDeleteNewUnlockedOwnerBeforeMinimumAge()
    {
        var owner = LineIndexCacheOwner.Create(_indexRoot);
        var ownerDirectory = owner.DirectoryPath;
        owner.Dispose();

        var result = LineIndexCacheMaintenance.CleanupOrphanedOwners(_indexRoot);

        Assert.Equal(1, result.SkippedOwnerCount);
        Assert.True(Directory.Exists(ownerDirectory));
    }

    [Fact]
    public void Cleanup_SkipsReparsePointEvidence()
    {
        var owner = LineIndexCacheOwner.Create(_indexRoot);
        var ownerDirectory = owner.DirectoryPath;
        owner.Dispose();
        Directory.SetLastWriteTimeUtc(ownerDirectory, DateTime.UtcNow - TimeSpan.FromDays(2));
        var lockPath = Path.Combine(ownerDirectory, LineIndexCacheOwner.LockFileName);

        var result = LineIndexCacheMaintenance.CleanupOrphanedOwners(
            _indexRoot,
            minimumAge: TimeSpan.Zero,
            attributesProvider: path => string.Equals(path, lockPath, StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.ReparsePoint
                : File.GetAttributes(path));

        Assert.Equal(1, result.SkippedOwnerCount);
        Assert.True(Directory.Exists(ownerDirectory));
    }

    [Fact]
    public void Cleanup_SkipsReparseVersionRootWithoutEnumeratingChildren()
    {
        var owner = LineIndexCacheOwner.Create(_indexRoot);
        var versionRoot = Path.GetDirectoryName(owner.DirectoryPath)!;
        var ownerDirectory = owner.DirectoryPath;
        owner.Dispose();
        Directory.SetLastWriteTimeUtc(ownerDirectory, DateTime.UtcNow - TimeSpan.FromDays(2));

        var result = LineIndexCacheMaintenance.CleanupOrphanedOwners(
            _indexRoot,
            minimumAge: TimeSpan.Zero,
            attributesProvider: path => string.Equals(path, versionRoot, StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.Directory | FileAttributes.ReparsePoint
                : File.GetAttributes(path));

        Assert.Equal(1, result.SkippedOwnerCount);
        Assert.True(Directory.Exists(ownerDirectory));
    }

    [Fact]
    public async Task Cleanup_ConcurrentCollectorsRemainBoundedAndIdempotent()
    {
        var ownerDirectories = new List<string>();
        for (var i = 0; i < 8; i++)
        {
            var owner = LineIndexCacheOwner.Create(_indexRoot);
            ownerDirectories.Add(owner.DirectoryPath);
            owner.Dispose();
            Directory.SetLastWriteTimeUtc(owner.DirectoryPath, DateTime.UtcNow - TimeSpan.FromDays(2));
        }

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            LineIndexCacheMaintenance.CleanupOrphanedOwners(
                _indexRoot,
                minimumAge: TimeSpan.Zero))));

        Assert.All(ownerDirectories, path => Assert.False(Directory.Exists(path)));
    }

    [Fact]
    public void IsPathUnderRoot_RejectsSiblingAndAcceptsOwnerChild()
    {
        var versionRoot = Path.Combine(_indexRoot, LineIndexCacheOwner.VersionDirectoryName);
        var sibling = Path.Combine(_indexRoot, "v1-escape", Guid.NewGuid().ToString("N"));
        var child = Path.Combine(versionRoot, Guid.NewGuid().ToString("N"));

        Assert.False(LineIndexCacheMaintenance.IsPathUnderRoot(versionRoot, sibling));
        Assert.True(LineIndexCacheMaintenance.IsPathUnderRoot(versionRoot, child));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    private static MappedLineOffsets CreateFrozenOffsets(LineIndexCacheOwner owner)
    {
        var offsets = new MappedLineOffsets(owner);
        offsets.Add(0);
        offsets.Add(12);
        offsets.Freeze();
        return offsets;
    }
}
