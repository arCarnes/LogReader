namespace LogReader.App.Services;

using System.Windows;
using LogReader.App.ViewModels;
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
    private readonly IStartupStorageCoordinator _storageCoordinator;
    private readonly IAppBootstrapper _bootstrapper;
    private readonly IMessageBoxService _messageBoxService;
    private readonly Action _cleanupIndexCacheDirectory;
    private readonly Func<Exception, string> _buildStartupFailureMessage;
    private readonly bool _enableLifecycleTimer;

    public AppStartupRunner(
        IStartupStorageCoordinator storageCoordinator,
        IAppBootstrapper bootstrapper,
        IMessageBoxService messageBoxService,
        Action cleanupIndexCacheDirectory,
        Func<Exception, string> buildStartupFailureMessage,
        bool enableLifecycleTimer = true)
    {
        _storageCoordinator = storageCoordinator;
        _bootstrapper = bootstrapper;
        _messageBoxService = messageBoxService;
        _cleanupIndexCacheDirectory = cleanupIndexCacheDirectory;
        _buildStartupFailureMessage = buildStartupFailureMessage;
        _enableLifecycleTimer = enableLifecycleTimer;
    }

    public async Task<AppStartupResult> RunAsync()
    {
        try
        {
            if (_storageCoordinator.EnsureStorageReady() == StartupStorageResult.Canceled)
                return new AppStartupResult(AppStartupStatus.Canceled);

            _cleanupIndexCacheDirectory();

            return new AppStartupResult(
                AppStartupStatus.Started,
                await _bootstrapper.CreateInitializedAsync(_enableLifecycleTimer));
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
}
