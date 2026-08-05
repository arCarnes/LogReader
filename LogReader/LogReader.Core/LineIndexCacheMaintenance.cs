namespace LogReader.Core;

internal readonly record struct LineIndexCacheCleanupResult(
    int DeletedOwnerCount,
    int LockedOwnerCount,
    int SkippedOwnerCount,
    int FailedOwnerCount,
    int DeletedLegacyFileCount = 0);

internal static class LineIndexCacheMaintenance
{
    internal static readonly TimeSpan DefaultOrphanMinimumAge = TimeSpan.FromHours(24);

    internal static LineIndexCacheCleanupResult CleanupOrphanedOwners(
        string? indexRoot = null,
        DateTime? utcNow = null,
        TimeSpan? minimumAge = null,
        Func<string, FileAttributes>? attributesProvider = null)
    {
        var resolvedIndexRoot = Path.GetFullPath(indexRoot ?? AppPaths.IndexDirectory);
        var versionRoot = Path.Combine(resolvedIndexRoot, LineIndexCacheOwner.VersionDirectoryName);
        if (!Directory.Exists(resolvedIndexRoot))
            return default;

        var now = (utcNow ?? DateTime.UtcNow).ToUniversalTime();
        var effectiveMinimumAge = minimumAge ?? DefaultOrphanMinimumAge;
        var getAttributes = attributesProvider ?? File.GetAttributes;

        try
        {
            if ((getAttributes(resolvedIndexRoot) & FileAttributes.ReparsePoint) != 0)
            {
                return new LineIndexCacheCleanupResult(0, 0, 1, 0);
            }
        }
        catch (Exception ex) when (IsExpectedIoException(ex))
        {
            return new LineIndexCacheCleanupResult(0, 0, 0, 1);
        }

        var (deletedLegacyFiles, skippedLegacyFiles, failedLegacyFiles) =
            CleanupLegacyIndexFiles(resolvedIndexRoot, getAttributes);
        if (!Directory.Exists(versionRoot))
        {
            return new LineIndexCacheCleanupResult(
                0,
                0,
                skippedLegacyFiles,
                failedLegacyFiles,
                deletedLegacyFiles);
        }

        try
        {
            if ((getAttributes(versionRoot) & FileAttributes.ReparsePoint) != 0)
            {
                return new LineIndexCacheCleanupResult(
                    0,
                    0,
                    skippedLegacyFiles + 1,
                    failedLegacyFiles,
                    deletedLegacyFiles);
            }
        }
        catch (Exception ex) when (IsExpectedIoException(ex))
        {
            return new LineIndexCacheCleanupResult(
                0,
                0,
                skippedLegacyFiles,
                failedLegacyFiles + 1,
                deletedLegacyFiles);
        }

        var deleted = 0;
        var locked = 0;
        var skipped = skippedLegacyFiles;
        var failed = failedLegacyFiles;

        string[] ownerDirectories;
        try
        {
            ownerDirectories = Directory.GetDirectories(versionRoot, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (IsExpectedIoException(ex))
        {
            return new LineIndexCacheCleanupResult(
                0,
                0,
                skippedLegacyFiles,
                failedLegacyFiles + 1,
                deletedLegacyFiles);
        }

        foreach (var ownerDirectory in ownerDirectories)
        {
            try
            {
                if (!IsSafeOwnerDirectory(versionRoot, ownerDirectory, getAttributes) ||
                    now - Directory.GetLastWriteTimeUtc(ownerDirectory) < effectiveMinimumAge)
                {
                    skipped++;
                    continue;
                }

                var lockPath = Path.Combine(ownerDirectory, LineIndexCacheOwner.LockFileName);
                if (File.Exists(lockPath) && !CanAcquireOwnerLock(lockPath))
                {
                    locked++;
                    continue;
                }

                Directory.Delete(ownerDirectory, recursive: true);
                deleted++;
            }
            catch (Exception ex) when (IsExpectedIoException(ex))
            {
                failed++;
            }
        }

        return new LineIndexCacheCleanupResult(deleted, locked, skipped, failed, deletedLegacyFiles);
    }

    private static (int Deleted, int Skipped, int Failed) CleanupLegacyIndexFiles(
        string indexRoot,
        Func<string, FileAttributes> getAttributes)
    {
        string[] legacyFiles;
        try
        {
            legacyFiles = Directory.GetFiles(indexRoot, "idx_*.bin", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (IsExpectedIoException(ex))
        {
            return (0, 0, 1);
        }

        var deleted = 0;
        var skipped = 0;
        var failed = 0;
        foreach (var legacyFile in legacyFiles)
        {
            try
            {
                var parent = Path.GetDirectoryName(Path.GetFullPath(legacyFile));
                var attributes = getAttributes(legacyFile);
                if (!StringComparer.OrdinalIgnoreCase.Equals(parent, indexRoot) ||
                    (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    skipped++;
                    continue;
                }

                File.Delete(legacyFile);
                deleted++;
            }
            catch (Exception ex) when (IsExpectedIoException(ex))
            {
                failed++;
            }
        }

        return (deleted, skipped, failed);
    }

    internal static bool IsPathUnderRoot(string root, string candidate)
    {
        var resolvedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var resolvedCandidate = Path.GetFullPath(candidate);
        return resolvedCandidate.StartsWith(
            resolvedRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeOwnerDirectory(
        string versionRoot,
        string ownerDirectory,
        Func<string, FileAttributes> getAttributes)
    {
        if (!IsPathUnderRoot(versionRoot, ownerDirectory) ||
            !Guid.TryParseExact(Path.GetFileName(ownerDirectory), "N", out _))
        {
            return false;
        }

        if ((getAttributes(ownerDirectory) & FileAttributes.ReparsePoint) != 0)
            return false;

        foreach (var entry in Directory.EnumerateFileSystemEntries(
                     ownerDirectory,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            var attributes = getAttributes(entry);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                return false;

            var name = Path.GetFileName(entry);
            if (!string.Equals(name, LineIndexCacheOwner.LockFileName, StringComparison.Ordinal) &&
                !string.Equals(name, LineIndexCacheOwner.MetadataFileName, StringComparison.Ordinal) &&
                !IsIndexFileName(name))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsIndexFileName(string name)
    {
        const string prefix = "idx_";
        const string suffix = ".bin";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
            !name.EndsWith(suffix, StringComparison.Ordinal) ||
            name.Length != prefix.Length + 32 + suffix.Length)
        {
            return false;
        }

        return Guid.TryParseExact(
            name.AsSpan(prefix.Length, 32),
            "N",
            out _);
    }

    private static bool CanAcquireOwnerLock(string lockPath)
    {
        try
        {
            using var stream = new FileStream(
                lockPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            return true;
        }
        catch (Exception ex) when (IsExpectedIoException(ex))
        {
            return false;
        }
    }

    private static bool IsExpectedIoException(Exception ex)
        => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
}
