namespace LogReader.Tests;

using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using System.Reflection;

public sealed class WorkspaceCompositionTests
{
    [Fact]
    public async Task Shutdown_DuringSynchronousActivationDisposesReturnedRegistration()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var files = new StubLogFileRepository();
            var groups = new StubLogGroupRepository();
            using var tail = new StubFileTailService();
            var reader = new StubLogReaderService();
            var encoding = new StubEncodingDetectionService();
            var factory = new RecordingFactory(new WorkspaceServicesFactory(files, groups, reader, tail, encoding, TestUiDispatcher.Current));
            var scheduler = new ImmediateScheduler(() => Assert.IsType<MainViewModel>(factory.TabHost).BeginShutdown());
            using var vm = TestMainViewModelFactory.Create(files, groups, new StubSettingsRepository(), reader,
                new StubSearchService(), tail, encoding, enableLifecycleTimer: true, workspaceFactory: factory, tabLifecycleScheduler: scheduler);
            try
            {
                Assert.True(vm.IsShuttingDown);
                vm.Dispose();
                await vm.ShutdownTask.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, scheduler.DisposeCount);
                Assert.Equal(1, tail.StopAllCount);
            }
            finally
            {
                vm.Dispose();
                await vm.ShutdownTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
        });
    }

    [Fact]
    public async Task Disposal_ContinuesCleanupAndRetainsRegistrationFailure()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var expected = new InvalidOperationException("registration cleanup failure");
            var scheduler = new ImmediateScheduler(() => { }, expected);
            using var tail = new StubFileTailService();
            using var vm = TestMainViewModelFactory.Create(new StubLogFileRepository(), new StubLogGroupRepository(), new StubSettingsRepository(),
                new StubLogReaderService(), new StubSearchService(), tail, new StubEncodingDetectionService(),
                enableLifecycleTimer: true, tabLifecycleScheduler: scheduler);
            try
            {
                await vm.OpenFilePathAsync(@"C:\logs\cleanup-failure.log");
                vm.Dispose();
                Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => vm.ShutdownTask.WaitAsync(TimeSpan.FromSeconds(5))));
                Assert.All(vm.Tabs, tab => Assert.True(tab.IsShutdownOrDisposed));
                Assert.True(vm.SearchPanel.ShutdownTask.IsCompleted);
                Assert.Equal(1, tail.StopAllCount);
                Assert.Equal(1, scheduler.DisposeCount);
                Assert.Null(vm.WorkspaceServices.Activation.QueueMemberRefresh);
                await Assert.ThrowsAsync<ObjectDisposedException>(() => Field<FileSessionRegistry>(vm.WorkspaceServices.Tabs, "_fileSessionRegistry").LoadScheduler
                    .AcquireAsync(@"C:\logs\closed.log", () => ["A"], false, default));
                vm.Dispose();
                Assert.Equal(1, scheduler.DisposeCount);
            }
            finally
            {
                vm.Dispose();
                await Assert.ThrowsAsync<InvalidOperationException>(() => vm.ShutdownTask.WaitAsync(TimeSpan.FromSeconds(5)));
            }
        });
    }

    [Fact]
    public async Task InitializationFault_RemainsInOwnedShutdownCompletion()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var expected = new InvalidOperationException("settings initialization failure");
            using var tail = new StubFileTailService();
            using var vm = TestMainViewModelFactory.Create(new StubLogFileRepository(), new StubLogGroupRepository(), new FailingSettings(expected),
                new StubLogReaderService(), new StubSearchService(), tail, new StubEncodingDetectionService());
            try
            {
                Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(vm.InitializeAsync));
                vm.Dispose();
                Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => vm.ShutdownTask.WaitAsync(TimeSpan.FromSeconds(5))));
                await vm.InitializeAsync();
                Assert.Empty(vm.Groups);
                Assert.Equal(1, tail.StopAllCount);
            }
            finally
            {
                vm.Dispose();
                await Assert.ThrowsAsync<InvalidOperationException>(() => vm.ShutdownTask.WaitAsync(TimeSpan.FromSeconds(5)));
            }
        });
    }

    [Fact]
    public async Task SynchronousScheduler_SeesCompleteShellAndRegistrationIsDisposedOnce()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var files = new StubLogFileRepository();
            var groups = new StubLogGroupRepository();
            using var tail = new StubFileTailService();
            var reader = new StubLogReaderService();
            var encoding = new StubEncodingDetectionService();
            var catalog = new LogFileCatalogService(files);
            var factory = new RecordingFactory(new WorkspaceServicesFactory(files, groups, reader, tail, encoding,
                TestUiDispatcher.Current, catalog));
            var scheduler = new ImmediateScheduler(() =>
            {
                Assert.True(factory.Returned);
                var shell = Assert.IsType<MainViewModel>(factory.TabHost);
                Assert.Same(shell, factory.DashboardHost);
                Assert.NotNull(shell.SearchPanel);
                Assert.NotNull(shell.FilterPanel);
                Assert.Same(shell.Tabs, factory.TabHost!.Tabs);
                Assert.Same(shell.Groups, factory.DashboardHost!.Groups);
                Assert.Same(catalog, shell.WorkspaceServices.Catalog);
                Assert.Same(shell.WorkspaceServices.Activation, Field<DashboardActivationService>(shell.WorkspaceServices.Dashboards, "_dashboardActivationService"));
                factory.TabHost.SelectedTab = null;
                factory.DashboardHost.NotifyFilteredTabsChanged();
            });
            using var vm = TestMainViewModelFactory.Create(files, groups, new StubSettingsRepository(), reader,
                new StubSearchService(), tail, encoding, enableLifecycleTimer: true, workspaceFactory: factory, tabLifecycleScheduler: scheduler);
            try
            {
                await vm.InitializeAsync();
                await vm.OpenFilePathAsync(@"C:\logs\construction-barrier.log");
                Assert.Same(vm.Tabs.Single(), factory.TabHost!.SelectedTab);
                Assert.Same(vm.SelectedTab, factory.DashboardHost!.FindTabInScope(vm.SelectedTab!.FilePath, null));
                Assert.Same(catalog, Field<LogFileCatalogService>(vm.WorkspaceServices.Tabs, "_fileCatalogService"));
                Assert.Same(catalog, Field<LogFileCatalogService>(vm.WorkspaceServices.Dashboards, "_fileCatalogService"));
                vm.BeginShutdown();
                vm.Dispose();
                vm.BeginShutdown();
                vm.Dispose();
                await vm.ShutdownTask.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, factory.CallCount);
                Assert.Equal(1, scheduler.ScheduleCount);
                Assert.Equal(1, scheduler.DisposeCount);
                Assert.Null(factory.Graph!.Activation.QueueMemberRefresh);
                Assert.Null(factory.Graph.Activation.MembershipDispatcher);
                Assert.Equal(0, tail.DisposeCount);
            }
            finally
            {
                vm.Dispose();
                await vm.ShutdownTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConstructionFailure_CleansCreatedGraphAndPreservesSuppliedGraph(bool suppliedGraph)
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var files = new StubLogFileRepository();
            var groups = new StubLogGroupRepository();
            using var tail = new StubFileTailService();
            var reader = new StubLogReaderService();
            var encoding = new StubEncodingDetectionService();
            var dispatcher = TestUiDispatcher.Current;
            WorkspaceServices? created = null;
            Func<TabMemberRefreshRequest, Task> priorRefresh = _ => Task.CompletedTask;
            var factory = new CallbackFactory((tabHost, dashboardHost) =>
            {
                created = new WorkspaceServicesFactory(files, groups, reader, tail, encoding, dispatcher).Create(tabHost, dashboardHost);
                if (!suppliedGraph) return created;
                created.Activation.QueueMemberRefresh = priorRefresh;
                created.Activation.MembershipDispatcher = dispatcher;
                return new WorkspaceServicesFactory(files, groups, reader, tail, encoding, dispatcher,
                    created.Catalog, created.Tabs, created.Dashboards, created.Activation).Create(tabHost, dashboardHost);
            });
            var expected = new InvalidOperationException("scheduler construction failure");
            try
            {
                var actual = Assert.Throws<InvalidOperationException>(() => TestMainViewModelFactory.Create(files, groups,
                    new StubSettingsRepository(), reader, new StubSearchService(), tail, encoding,
                    enableLifecycleTimer: true, workspaceFactory: factory,
                    tabLifecycleScheduler: new ImmediateScheduler(() => throw expected)));
                Assert.Same(expected, actual);
                Assert.NotNull(created);
                var scheduler = Field<FileSessionRegistry>(created.Tabs, "_fileSessionRegistry").LoadScheduler;
                if (suppliedGraph)
                {
                    Assert.Same(priorRefresh, created.Activation.QueueMemberRefresh);
                    Assert.Same(dispatcher, created.Activation.MembershipDispatcher);
                    using var lease = await scheduler.AcquireAsync(@"C:\logs\still-owned.log", () => ["A"], false, default);
                }
                else
                {
                    await Assert.ThrowsAsync<ObjectDisposedException>(() => scheduler.AcquireAsync(
                        @"C:\logs\closed.log", () => ["A"], false, default));
                }
                Assert.Equal(0, tail.DisposeCount);
                Assert.Equal(0, tail.StopAllCount);
            }
            finally
            {
                created?.Dispose();
                if (created != null) await created.ShutdownTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
        });
    }

    [Fact]
    public async Task FactoryFailure_PreservesPrimaryErrorAndCallerOwnedSubstitution()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var files = new StubLogFileRepository();
            var groups = new StubLogGroupRepository();
            using var tail = new StubFileTailService();
            var reader = new StubLogReaderService();
            var encoding = new StubEncodingDetectionService();
            var expected = new InvalidOperationException("activation construction failure");
            WorkspaceServices? supplied = null;
            var factory = new CallbackFactory((tabHost, dashboardHost) =>
            {
                supplied = new WorkspaceServicesFactory(files, groups, reader, tail, encoding, TestUiDispatcher.Current).Create(tabHost, dashboardHost);
                return new WorkspaceServicesFactory(files, groups, reader, tail, encoding, TestUiDispatcher.Current,
                    tabWorkspace: supplied.Tabs, activationFactory: _ => throw expected).Create(tabHost, dashboardHost);
            });
            try
            {
                Assert.Same(expected, Assert.Throws<InvalidOperationException>(() => TestMainViewModelFactory.Create(files, groups,
                    new StubSettingsRepository(), reader, new StubSearchService(), tail, encoding, workspaceFactory: factory)));
                Assert.NotNull(supplied);
                using var lease = await Field<FileSessionRegistry>(supplied.Tabs, "_fileSessionRegistry").LoadScheduler
                    .AcquireAsync(@"C:\logs\caller-owned.log", () => ["A"], false, default);
                Assert.Equal(0, tail.DisposeCount);
            }
            finally
            {
                supplied?.Dispose();
                if (supplied != null) await supplied.ShutdownTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
        });
    }

    [Fact]
    public async Task IndependentShells_ShareSessionsOnlyWithinTheirOwnGraph()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            using var tailA = new StubFileTailService();
            using var tailB = new StubFileTailService();
            var dispatcherA = new CountingDispatcher();
            var dispatcherB = new CountingDispatcher();
            using var first = WithoutContext(() => TestMainViewModelFactory.Create(new StubLogFileRepository(), new StubLogGroupRepository(), new StubSettingsRepository(),
                new StubLogReaderService(), new StubSearchService(), tailA, new StubEncodingDetectionService(), uiDispatcher: dispatcherA));
            using var second = WithoutContext(() => TestMainViewModelFactory.Create(new StubLogFileRepository(), new StubLogGroupRepository(), new StubSettingsRepository(),
                new StubLogReaderService(), new StubSearchService(), tailB, new StubEncodingDetectionService(), uiDispatcher: dispatcherB));
            try
            {
                var a = (await WithoutContext(() => first.CreateDashboardPlaceholderAsync(@"C:\logs\same.log", "A", default)))!.Tab;
                var b = (await WithoutContext(() => first.CreateDashboardPlaceholderAsync(@"C:\logs\same.log", "B", default)))!.Tab;
                var c = (await WithoutContext(() => second.CreateDashboardPlaceholderAsync(@"C:\logs\same.log", "A", default)))!.Tab;
                Assert.Same(a.ActiveSession, b.ActiveSession);
                Assert.NotSame(a.ActiveSession, c.ActiveSession);
                Assert.NotSame(first.WorkspaceServices.Catalog, second.WorkspaceServices.Catalog);
                Assert.NotSame(first.WorkspaceServices.Activation, second.WorkspaceServices.Activation);
                Assert.NotSame(first.Tabs, second.Tabs);
                var beforeA = dispatcherA.Calls;
                var beforeB = dispatcherB.Calls;
                await Task.Run(() => a.InvokeOnUiAsync(() => Assert.True(WpfUiDispatcher.Instance.CheckAccess())));
                await Task.Run(() => a.ActiveSession.InvokeOnSessionContextAsync(() => Assert.True(WpfUiDispatcher.Instance.CheckAccess())));
                Assert.Equal(beforeA + 2, dispatcherA.Calls);
                Assert.Equal(beforeB, dispatcherB.Calls);
                await Task.Run(() => c.InvokeOnUiAsync(() => Assert.True(WpfUiDispatcher.Instance.CheckAccess())));
                await Task.Run(() => c.ActiveSession.InvokeOnSessionContextAsync(() => Assert.True(WpfUiDispatcher.Instance.CheckAccess())));
                Assert.Equal(beforeA + 2, dispatcherA.Calls);
                Assert.Equal(beforeB + 2, dispatcherB.Calls);
                first.Dispose();
                await first.ShutdownTask.WaitAsync(TimeSpan.FromSeconds(5));
                await second.CompleteDashboardPlaceholderAsync(
                    (await second.CreateDashboardPlaceholderAsync(@"C:\logs\other.log", "A", default))!, default);
                Assert.Equal(2, second.Tabs.Count);
                Assert.Equal(0, tailB.StopAllCount);
            }
            finally
            {
                first.Dispose();
                second.Dispose();
                await Task.WhenAll(first.ShutdownTask, second.ShutdownTask).WaitAsync(TimeSpan.FromSeconds(5));
            }
        });
    }

    [Fact]
    public async Task Shutdown_DrainsHeldGroupReadAndRejectsNewOpen()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var groups = new HeldGroups();
            using var tail = new StubFileTailService();
            using var vm = TestMainViewModelFactory.Create(new StubLogFileRepository(), groups, new StubSettingsRepository(),
                new StubLogReaderService(), new StubSearchService(), tail, new StubEncodingDetectionService());
            var initialize = vm.InitializeAsync();
            try
            {
                await groups.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                vm.BeginShutdown();
                Assert.False(vm.ShutdownTask.IsCompleted);
                groups.Release.TrySetResult();
                await Task.WhenAll(initialize, vm.ShutdownTask).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Empty(vm.Groups);
                await vm.OpenFilePathAsync(@"C:\logs\after-shutdown.log");
                Assert.Empty(vm.Tabs);
            }
            finally
            {
                groups.Release.TrySetResult();
                vm.Dispose();
                await Task.WhenAll(initialize, vm.ShutdownTask).WaitAsync(TimeSpan.FromSeconds(5));
            }
        });
    }

    [Fact]
    public async Task Shutdown_DrainsHeldMembershipProbeWithoutLateRows()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var files = new StubLogFileRepository();
            var entry = new LogFileEntry { FilePath = @"C:\logs\held-probe.log" };
            await files.AddAsync(entry);
            var groups = new StubLogGroupRepository();
            await groups.AddAsync(new LogGroup { Id = "dashboard", Name = "Dashboard", FileIds = [entry.Id] });
            using var tail = new StubFileTailService();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reader = new StubLogReaderService();
            var encoding = new StubEncodingDetectionService();
            var factory = new WorkspaceServicesFactory(files, groups, reader, tail, encoding, TestUiDispatcher.Current,
                activationFactory: host => new DashboardActivationService(host, files, groups, async paths =>
                {
                    started.TrySetResult();
                    await release.Task;
                    return paths.ToDictionary(pair => pair.Key, _ => DashboardFileProbeResult.Found);
                }));
            using var vm = TestMainViewModelFactory.Create(files, groups, new StubSettingsRepository(), reader,
                new StubSearchService(), tail, encoding, workspaceFactory: factory);
            var initialize = vm.InitializeAsync();
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Empty(vm.Groups.Single().MemberFiles);
                vm.Dispose();
                Assert.False(vm.ShutdownTask.IsCompleted);
                release.TrySetResult();
                await Task.WhenAll(initialize, vm.ShutdownTask).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Empty(vm.Groups.Single().MemberFiles);
            }
            finally
            {
                release.TrySetResult();
                vm.Dispose();
                await Task.WhenAll(initialize, vm.ShutdownTask).WaitAsync(TimeSpan.FromSeconds(5));
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shutdown_RefreshSchedulerDrainsCancellationIgnoringWorker(bool fail)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new InvalidOperationException("refresh failed while draining");
        var scheduler = new TabMemberRefreshScheduler(async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
            if (fail) throw expected;
        });
        var running = scheduler.Queue(new TabMemberRefreshRequest(true, new Dictionary<string, string>()));
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            scheduler.Shutdown();
            var shutdown = scheduler.ShutdownTask;
            Assert.False(shutdown.IsCompleted);
            Assert.True(scheduler.Queue(new TabMemberRefreshRequest(true, new Dictionary<string, string>())).IsCanceled);
            release.TrySetResult();
            if (fail)
            {
                Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => shutdown.WaitAsync(TimeSpan.FromSeconds(5))));
                Assert.Same(shutdown, scheduler.ShutdownTask);
                Assert.True(running.IsFaulted);
            }
            else
            {
                await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(running.IsCanceled);
            }
        }
        finally
        {
            release.TrySetResult();
            scheduler.Shutdown();
            if (fail) await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.ShutdownTask.WaitAsync(TimeSpan.FromSeconds(5)));
            else await scheduler.ShutdownTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static T Field<T>(object instance, string name)
        => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

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

    private sealed class CountingDispatcher : IUiDispatcher
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public bool CheckAccess() => WpfUiDispatcher.Instance.CheckAccess();
        public Task InvokeAsync(Action action)
        {
            Interlocked.Increment(ref _calls);
            return WpfUiDispatcher.Instance.InvokeAsync(action);
        }
        public Task InvokeAsync(Func<Task> action)
        {
            Interlocked.Increment(ref _calls);
            return WpfUiDispatcher.Instance.InvokeAsync(action);
        }
    }

    private sealed class RecordingFactory(IWorkspaceServicesFactory inner) : IWorkspaceServicesFactory
    {
        public bool Returned { get; private set; }
        public int CallCount { get; private set; }
        public ITabWorkspaceHost? TabHost { get; private set; }
        public IDashboardWorkspaceHost? DashboardHost { get; private set; }
        public WorkspaceServices? Graph { get; private set; }
        public WorkspaceServices Create(ITabWorkspaceHost tabHost, IDashboardWorkspaceHost dashboardHost)
        {
            CallCount++;
            TabHost = tabHost;
            DashboardHost = dashboardHost;
            Graph = inner.Create(tabHost, dashboardHost);
            Returned = true;
            return Graph;
        }
    }

    private sealed class CallbackFactory(Func<ITabWorkspaceHost, IDashboardWorkspaceHost, WorkspaceServices> create) : IWorkspaceServicesFactory
    {
        public WorkspaceServices Create(ITabWorkspaceHost tabHost, IDashboardWorkspaceHost dashboardHost) => create(tabHost, dashboardHost);
    }

    private sealed class ImmediateScheduler(Action beforeCallback, Exception? disposeError = null) : ITabLifecycleScheduler, IDisposable
    {
        public int ScheduleCount { get; private set; }
        public int DisposeCount { get; private set; }
        public IDisposable ScheduleRecurring(TimeSpan dueTime, TimeSpan interval, Action callback)
        {
            ScheduleCount++;
            beforeCallback();
            callback();
            return this;
        }
        public void Dispose()
        {
            DisposeCount++;
            if (disposeError != null) throw disposeError;
        }
    }

    private sealed class FailingSettings(Exception error) : StubSettingsRepository, ISettingsRepository
    {
        Task<AppSettings> ISettingsRepository.LoadAsync() => Task.FromException<AppSettings>(error);
    }

    private sealed class HeldGroups : StubLogGroupRepository, ILogGroupRepository
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<List<LogGroup>> ILogGroupRepository.GetAllAsync()
        {
            Started.TrySetResult();
            await Release.Task;
            return [new LogGroup { Id = "late", Name = "Late" }];
        }
    }

    [Fact]
    public async Task Shutdown_DrainsHeldInitializationAndPreventsLateActivation()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var settings = new HeldSettings();
            var appearance = new StubLogAppearanceService();
            using var tail = new StubFileTailService();
            using var vm = TestMainViewModelFactory.Create(new StubLogFileRepository(), new StubLogGroupRepository(), settings,
                new StubLogReaderService(), new StubSearchService(), tail, new StubEncodingDetectionService(), logAppearanceService: appearance);
            var initialize = vm.InitializeAsync();
            try
            {
                await settings.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                vm.Dispose();
                Assert.False(vm.ShutdownTask.IsCompleted);
                settings.Release.TrySetResult();
                await Task.WhenAll(initialize, vm.ShutdownTask).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, appearance.ApplyCallCount);
                Assert.Empty(vm.Groups);
                await vm.InitializeAsync();
                Assert.Equal(1, settings.LoadCount);
                vm.Dispose();
                Assert.Equal(0, tail.DisposeCount);
            }
            finally
            {
                settings.Release.TrySetResult();
                vm.Dispose();
                await Task.WhenAll(initialize, vm.ShutdownTask).WaitAsync(TimeSpan.FromSeconds(5));
            }
        });
    }

    private sealed class HeldSettings : StubSettingsRepository, ISettingsRepository
    {
        private int _loadCount;
        public int LoadCount => Volatile.Read(ref _loadCount);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<AppSettings> ISettingsRepository.LoadAsync()
        {
            Interlocked.Increment(ref _loadCount);
            Started.TrySetResult();
            await Release.Task;
            return new AppSettings();
        }
    }
}
