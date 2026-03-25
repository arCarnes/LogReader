namespace LogReader.App.Services;

using System.IO;
using System.Windows;
using LogReader.Core;

internal sealed class RuntimePersistedStateRecoveryExecutor
{
    private const string WarningCaption = "LogReader Recovered Saved Data";
    private const string RuntimeRecoveryIntroLine =
        "LogReader recovered invalid saved data and retried your action with clean defaults.";

    private readonly IPersistedStateRecoveryCoordinator _persistedStateRecoveryCoordinator;
    private readonly IMessageBoxService _messageBoxService;
    private readonly Func<PersistedStateRecoveryResult, Task> _refreshRecoveredStateAsync;
    private readonly AsyncLocal<ExecutionState?> _currentExecution = new();

    public RuntimePersistedStateRecoveryExecutor(
        IPersistedStateRecoveryCoordinator persistedStateRecoveryCoordinator,
        IMessageBoxService messageBoxService,
        Func<PersistedStateRecoveryResult, Task> refreshRecoveredStateAsync)
    {
        _persistedStateRecoveryCoordinator = persistedStateRecoveryCoordinator;
        _messageBoxService = messageBoxService;
        _refreshRecoveredStateAsync = refreshRecoveredStateAsync;
    }

    public Task ExecuteAsync(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return ExecuteAsync(async () =>
        {
            await operation();
            return true;
        });
    }

    public async Task<T> ExecuteAsync<T>(Func<Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (_currentExecution.Value != null)
            return await operation();

        var state = new ExecutionState();
        _currentExecution.Value = state;

        try
        {
            while (true)
            {
                PersistedStateRecoveryResult? recoveryToRefresh = null;
                try
                {
                    if (state.PendingRefreshes.Count > 0)
                    {
                        recoveryToRefresh = state.PendingRefreshes[0];
                        state.PendingRefreshes.RemoveAt(0);
                        await _refreshRecoveredStateAsync(recoveryToRefresh);
                        continue;
                    }

                    var result = await operation();
                    if (state.Recoveries.Count > 0)
                    {
                        _messageBoxService.Show(
                            AppStartupRunner.BuildRecoveryMessage(state.Recoveries, RuntimeRecoveryIntroLine),
                            WarningCaption,
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }

                    return result;
                }
                catch (PersistedStateRecoveryException ex)
                {
                    if (recoveryToRefresh != null)
                        state.PendingRefreshes.Insert(0, recoveryToRefresh);

                    RecoverOrThrow(ex, state);
                }
            }
        }
        finally
        {
            _currentExecution.Value = null;
        }
    }

    private void RecoverOrThrow(PersistedStateRecoveryException exception, ExecutionState state)
    {
        if (!state.RecoveredStorePaths.Add(exception.StorePath))
        {
            throw new RuntimePersistedStateRecoveryFailedException(
                exception,
                state.Recoveries.LastOrDefault(recovery =>
                    string.Equals(recovery.StorePath, exception.StorePath, StringComparison.OrdinalIgnoreCase)));
        }

        var recovery = _persistedStateRecoveryCoordinator.Recover(exception);
        state.Recoveries.Add(recovery);
        state.PendingRefreshes.Add(recovery);
    }

    private sealed class ExecutionState
    {
        public HashSet<string> RecoveredStorePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<PersistedStateRecoveryResult> Recoveries { get; } = new();

        public List<PersistedStateRecoveryResult> PendingRefreshes { get; } = new();
    }
}

internal sealed class RuntimePersistedStateRecoveryFailedException : IOException
{
    public RuntimePersistedStateRecoveryFailedException(
        PersistedStateRecoveryException exception,
        PersistedStateRecoveryResult? priorRecovery)
        : base(BuildMessage(exception, priorRecovery), exception)
    {
        StoreDisplayName = exception.StoreDisplayName;
        StorePath = exception.StorePath;
        BackupPath = priorRecovery?.BackupPath;
        FailureReason = exception.FailureReason;
    }

    public string StoreDisplayName { get; }

    public string StorePath { get; }

    public string? BackupPath { get; }

    public string FailureReason { get; }

    private static string BuildMessage(
        PersistedStateRecoveryException exception,
        PersistedStateRecoveryResult? priorRecovery)
    {
        var lines = new List<string>
        {
            $"LogReader could not recover the saved {exception.StoreDisplayName} data while retrying your action.",
            $"Original file: {exception.StorePath}",
            $"Reason: {exception.FailureReason}"
        };

        if (!string.IsNullOrWhiteSpace(priorRecovery?.BackupPath))
            lines.Add($"Recovered backup: {priorRecovery.BackupPath}");

        lines.Add("Please review the recovered backup or restart LogReader before retrying.");
        return string.Join(Environment.NewLine, lines);
    }
}
