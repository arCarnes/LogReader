namespace LogReader.App.Services;

using System.Windows;
using LogReader.App.ViewModels;
using LogReader.Core;
using LogReader.Core.Interfaces;

internal enum AppStartupStatus
{
    Started,
    Canceled,
    Failed
}

internal sealed record AppStartupResult(
    AppStartupStatus Status,
    AppComposition? Composition = null)
{
    public MainViewModel? MainViewModel => Composition?.MainViewModel;

    public IFileTailService? TailService => Composition?.TailService;
}

internal sealed class AppStartupRunner
{
    private const string SingleInstanceCaption = "LogReader Already Running";
    private const string SingleInstanceMessage =
        "LogReader is already running for this Windows user. Close the existing instance before starting another one.";

    private readonly IStartupStorageCoordinator _storageCoordinator;
    private readonly IAppBootstrapper _bootstrapper;
    private readonly IMessageBoxService _messageBoxService;
    private readonly Action _cleanupIndexCacheDirectory;
    private readonly Func<Exception, string> _buildStartupFailureMessage;
    private readonly IPersistedStateRecoveryCoordinator _persistedStateRecoveryCoordinator;
    private readonly IAppInstanceCoordinator _appInstanceCoordinator;
    private readonly bool _enableLifecycleTimer;

    public AppStartupRunner(
        IStartupStorageCoordinator storageCoordinator,
        IAppBootstrapper bootstrapper,
        IMessageBoxService messageBoxService,
        Action cleanupIndexCacheDirectory,
        Func<Exception, string> buildStartupFailureMessage,
        IPersistedStateRecoveryCoordinator? persistedStateRecoveryCoordinator = null,
        IAppInstanceCoordinator? appInstanceCoordinator = null,
        bool enableLifecycleTimer = true)
    {
        _storageCoordinator = storageCoordinator;
        _bootstrapper = bootstrapper;
        _messageBoxService = messageBoxService;
        _cleanupIndexCacheDirectory = cleanupIndexCacheDirectory;
        _buildStartupFailureMessage = buildStartupFailureMessage;
        _persistedStateRecoveryCoordinator = persistedStateRecoveryCoordinator ?? new PersistedStateRecoveryCoordinator();
        _appInstanceCoordinator = appInstanceCoordinator ?? new SingleInstanceCoordinator();
        _enableLifecycleTimer = enableLifecycleTimer;
    }

    public async Task<AppStartupResult> RunAsync()
    {
        try
        {
            if (!_appInstanceCoordinator.TryAcquire())
            {
                _messageBoxService.Show(
                    SingleInstanceMessage,
                    SingleInstanceCaption,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return new AppStartupResult(AppStartupStatus.Canceled);
            }

            if (_storageCoordinator.EnsureStorageReady() == StartupStorageResult.Canceled)
                return new AppStartupResult(AppStartupStatus.Canceled);

            _cleanupIndexCacheDirectory();

            var recoveries = new List<PersistedStateRecoveryResult>();
            while (true)
            {
                try
                {
                    var result = new AppStartupResult(
                        AppStartupStatus.Started,
                        await _bootstrapper.CreateInitializedAsync(_enableLifecycleTimer));
                    if (recoveries.Count > 0)
                    {
                        _messageBoxService.Show(
                            BuildRecoveryMessage(recoveries),
                            "LogReader Recovered Saved Data",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }

                    return result;
                }
                catch (PersistedStateRecoveryException ex)
                {
                    recoveries.Add(_persistedStateRecoveryCoordinator.Recover(ex));
                }
            }
        }
        catch (Exception ex)
        {
            _messageBoxService.Show(
                _buildStartupFailureMessage(ex),
                "LogReader Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return new AppStartupResult(AppStartupStatus.Failed);
        }
    }

    internal static string BuildRecoveryMessage(
        IReadOnlyList<PersistedStateRecoveryResult> recoveries,
        string introLine = "LogReader recovered invalid saved data and restarted with clean defaults.")
    {
        ArgumentNullException.ThrowIfNull(recoveries);

        var lines = new List<string>
        {
            introLine
        };

        foreach (var recovery in recoveries)
        {
            lines.Add(string.Empty);
            lines.Add($"Recovered store: {recovery.StoreDisplayName}");
            lines.Add($"Original file: {recovery.StorePath}");
            lines.Add($"Backup file: {recovery.BackupPath}");
            lines.Add($"Recovery note: {recovery.NotePath}");
            lines.Add($"Reason: {recovery.FailureReason}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
