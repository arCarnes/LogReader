namespace LogReader.Infrastructure.Repositories;

internal readonly record struct PersistedStoreStamp(
    bool Exists,
    long Length,
    long CreationTimeUtcTicks,
    long LastWriteTimeUtcTicks)
{
    internal static PersistedStoreStamp Missing => new(false, 0, 0, 0);
}

internal interface IPersistedSnapshotFileSystem
{
    PersistedStoreStamp GetStamp(string path);

    Task<byte[]?> ReadAllBytesAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken);

    bool HasTemporaryArtifact(string storePath);

    bool HasRecoveryArtifact(string storePath);
}

internal sealed class PersistedSnapshotFileSystem : IPersistedSnapshotFileSystem
{
    public PersistedStoreStamp GetStamp(string path)
    {
        var info = new FileInfo(path);
        info.Refresh();
        return info.Exists
            ? new PersistedStoreStamp(
                true,
                info.Length,
                info.CreationTimeUtc.Ticks,
                info.LastWriteTimeUtc.Ticks)
            : PersistedStoreStamp.Missing;
    }

    public async Task<byte[]?> ReadAllBytesAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > maximumBytes)
                throw new PersistedStoreTooLargeException();

            var contents = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(contents, cancellationToken).ConfigureAwait(false);
            return contents;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    public bool HasTemporaryArtifact(string storePath)
        => File.Exists(storePath + ".tmp");

    public bool HasRecoveryArtifact(string storePath)
    {
        var directory = Path.GetDirectoryName(storePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return false;

        var fileName = Path.GetFileNameWithoutExtension(storePath);
        var extension = Path.GetExtension(storePath);
        return Directory.EnumerateFiles(
                directory,
                $"{fileName}.corrupt-*{extension}",
                SearchOption.TopDirectoryOnly)
            .Any();
    }
}

internal sealed class PersistedStoreTooLargeException : IOException;
