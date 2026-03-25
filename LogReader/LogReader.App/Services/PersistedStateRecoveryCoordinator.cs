namespace LogReader.App.Services;

using System.IO;
using LogReader.Core;

internal interface IPersistedStateRecoveryCoordinator
{
    PersistedStateRecoveryResult Recover(PersistedStateRecoveryException exception);
}

internal sealed class PersistedStateRecoveryCoordinator : IPersistedStateRecoveryCoordinator
{
    private readonly Func<DateTime> _utcNow;

    public PersistedStateRecoveryCoordinator(Func<DateTime>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public PersistedStateRecoveryResult Recover(PersistedStateRecoveryException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var directory = Path.GetDirectoryName(exception.StorePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new IOException(
                $"LogReader could not recover the saved {exception.StoreDisplayName} data because the store path is invalid:{Environment.NewLine}{exception.StorePath}",
                exception);
        }

        Directory.CreateDirectory(directory);

        var timestamp = _utcNow().ToString("yyyyMMdd-HHmmssfff");
        var storeFileName = Path.GetFileNameWithoutExtension(exception.StorePath);
        var storeExtension = Path.GetExtension(exception.StorePath);
        var backupPath = Path.Combine(directory, $"{storeFileName}.corrupt-{timestamp}{storeExtension}");
        File.Move(exception.StorePath, backupPath, overwrite: false);

        var notePath = backupPath + ".note.txt";
        File.WriteAllText(
            notePath,
            string.Join(
                Environment.NewLine,
                $"RecoveredAtUtc={_utcNow():O}",
                $"StoreDisplayName={exception.StoreDisplayName}",
                $"OriginalPath={exception.StorePath}",
                $"BackupPath={backupPath}",
                $"FailureReason={exception.FailureReason}"));

        return new PersistedStateRecoveryResult(
            exception.StoreDisplayName,
            exception.StorePath,
            backupPath,
            notePath,
            exception.FailureReason);
    }
}
