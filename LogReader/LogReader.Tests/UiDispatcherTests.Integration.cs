namespace LogReader.Tests;

using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.Core.Models;

public partial class UiDispatcherTests
{
    [Fact]
    public async Task TabStatusWithoutCapturedContext_UsesInjectedDispatcherOnWpfOwner()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var owner = Environment.CurrentManagedThreadId;
            var dispatcher = new RecordingWpfDispatcher();
            using var tab = WithoutContext(() => new LogTabViewModel(
                "file", @"C:\logs\dispatcher-test.log", new StubLogReaderService(), new StubFileTailService(),
                new StubEncodingDetectionService(), new AppSettings(), skipInitialEncodingResolution: true,
                sessionRegistry: null, initialEncoding: FileEncoding.Utf8, scopeDashboardId: null, uiDispatcher: dispatcher));
            var changed = new TaskCompletionSource<(int Thread, bool Dispatched)>(TaskCreationOptions.RunContinuationsAsynchronously);
            tab.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(tab.StatusText) && tab.StatusText == "worker status")
                    changed.TrySetResult((Environment.CurrentManagedThreadId, dispatcher.IsExecuting));
            };
            try
            {
                await Task.Run(() => ((IFileSessionClient)tab).SetStatusText("worker status"));
                var published = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(owner, published.Thread);
                Assert.True(published.Dispatched);
            }
            finally
            {
                var session = tab.ActiveSession;
                tab.BeginShutdown();
                tab.Dispose();
                await session.ShutdownTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
        });
    }

    [Fact]
    public async Task ShellComposition_ForwardsInjectedFallbackToTabAndSession()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var dispatcher = new RecordingWpfDispatcher();
            using var vm = WithoutContext(() => new MainViewModel(
                new StubLogFileRepository(), new StubLogGroupRepository(), new StubSettingsRepository(),
                new LogReader.Testing.StubLogReaderService(), new StubSearchService(), new StubFileTailService(),
                new StubEncodingDetectionService(), enableLifecycleTimer: false,
                fileDialogService: null, messageBoxService: null, settingsDialogService: null,
                bulkOpenPathsDialogService: null, settingsViewModelFactory: null,
                persistedStateRecoveryCoordinator: null, workspaceViewModelReference: null,
                logAppearanceService: null, tabLifecycleScheduler: null, fileCatalogService: null,
                tabWorkspace: null, dashboardWorkspace: null, uiDispatcher: dispatcher));
            try
            {
                var prepared = await WithoutContext(() => vm.CreateDashboardPlaceholderAsync(
                    @"C:\logs\composition-dispatcher-test.log", "dashboard", CancellationToken.None));
                var tab = Assert.IsType<TabWorkspaceService.PreparedTabOpen>(prepared).Tab;
                await Task.Run(() => tab.InvokeOnUiAsync(() => Assert.True(dispatcher.IsExecuting)));
                await Task.Run(() => tab.ActiveSession.InvokeOnSessionContextAsync(() => Assert.True(dispatcher.IsExecuting)));
            }
            finally
            {
                vm.BeginShutdown();
                vm.Dispose();
                await vm.ShutdownTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
        });
    }

    private static T WithoutContext<T>(Func<T> action)
    {
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            return action();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private sealed class RecordingWpfDispatcher : IUiDispatcher
    {
        private readonly AsyncLocal<int> _depth = new();
        public bool IsExecuting => _depth.Value != 0;
        public bool CheckAccess() => WpfUiDispatcher.Instance.CheckAccess();

        public Task InvokeAsync(Action action)
            => WpfUiDispatcher.Instance.InvokeAsync(() =>
            {
                _depth.Value++;
                try { action(); }
                finally { _depth.Value--; }
            });

        public Task InvokeAsync(Func<Task> action)
            => WpfUiDispatcher.Instance.InvokeAsync(async () =>
            {
                _depth.Value++;
                try { await action(); }
                finally { _depth.Value--; }
            });
    }
}
