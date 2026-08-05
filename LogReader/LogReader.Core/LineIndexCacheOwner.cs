namespace LogReader.Core;

using System.Text;
using System.Text.Json;

internal sealed class LineIndexCacheOwner : IDisposable
{
    internal const string VersionDirectoryName = "v1";
    internal const string LockFileName = "owner.lock";
    internal const string MetadataFileName = "owner.json";

    private FileStream? _lockStream;

    private LineIndexCacheOwner(
        Guid ownerId,
        string directoryPath,
        FileStream lockStream)
    {
        OwnerId = ownerId;
        DirectoryPath = directoryPath;
        _lockStream = lockStream;
    }

    internal Guid OwnerId { get; }

    internal string DirectoryPath { get; }

    internal static LineIndexCacheOwner Create(
        string indexRoot,
        Guid? ownerId = null,
        DateTime? startedAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexRoot);

        var resolvedIndexRoot = Path.GetFullPath(indexRoot);
        var versionRoot = Path.Combine(resolvedIndexRoot, VersionDirectoryName);
        var resolvedOwnerId = ownerId ?? Guid.NewGuid();
        var ownerDirectory = Path.Combine(versionRoot, resolvedOwnerId.ToString("N"));

        Directory.CreateDirectory(versionRoot);
        if (Directory.Exists(ownerDirectory))
            throw new IOException($"The line-index cache owner directory already exists: '{ownerDirectory}'.");

        Directory.CreateDirectory(ownerDirectory);

        FileStream? lockStream = null;
        var lockPath = Path.Combine(ownerDirectory, LockFileName);
        try
        {
            lockStream = new FileStream(
                lockPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read | FileShare.Delete,
                bufferSize: 1,
                FileOptions.None);

            var owner = new LineIndexCacheOwner(resolvedOwnerId, ownerDirectory, lockStream);
            owner.TryWriteMetadata(startedAtUtc ?? DateTime.UtcNow);
            return owner;
        }
        catch
        {
            lockStream?.Dispose();
            if (lockStream is not null)
            {
                TryDeleteFile(lockPath);
                TryDeleteEmptyDirectory(ownerDirectory);
            }

            throw;
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _lockStream, null)?.Dispose();
    }

    private void TryWriteMetadata(DateTime startedAtUtc)
    {
        try
        {
            var metadata = new OwnerMetadata(
                SchemaVersion: 1,
                OwnerId: OwnerId.ToString("N"),
                ProcessId: Environment.ProcessId,
                StartedAtUtc: startedAtUtc.ToUniversalTime());
            var json = JsonSerializer.Serialize(metadata);
            File.WriteAllText(
                Path.Combine(DirectoryPath, MetadataFileName),
                json,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException)
        {
            // Metadata is diagnostic only. The lifetime lock remains authoritative.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: false);
        }
        catch
        {
        }
    }

    private sealed record OwnerMetadata(
        int SchemaVersion,
        string OwnerId,
        int ProcessId,
        DateTime StartedAtUtc);
}

internal static class LineIndexCacheOwnerRegistry
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, LineIndexCacheOwner> Owners =
        new(StringComparer.OrdinalIgnoreCase);

    static LineIndexCacheOwnerRegistry()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DisposeAll();
    }

    internal static LineIndexCacheOwner GetCurrentOwner()
    {
        var indexRoot = Path.GetFullPath(AppPaths.IndexDirectory);
        lock (Sync)
        {
            if (Owners.TryGetValue(indexRoot, out var owner))
                return owner;

            owner = LineIndexCacheOwner.Create(indexRoot);
            Owners.Add(indexRoot, owner);
            return owner;
        }
    }

    internal static void Release(string indexRoot)
    {
        var resolvedRoot = Path.GetFullPath(indexRoot);
        LineIndexCacheOwner? owner = null;
        lock (Sync)
        {
            if (Owners.Remove(resolvedRoot, out var existing))
                owner = existing;
        }

        owner?.Dispose();
    }

    private static void DisposeAll()
    {
        List<LineIndexCacheOwner> owners;
        lock (Sync)
        {
            owners = Owners.Values.ToList();
            Owners.Clear();
        }

        foreach (var owner in owners)
            owner.Dispose();
    }
}
