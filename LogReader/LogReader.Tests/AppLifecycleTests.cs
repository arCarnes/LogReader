namespace LogReader.Tests;

using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using LogReader.App;
using LogReader.App.Services;
using LogReader.Core;
using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

public class AppLifecycleTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "LogReaderAppLifecycleTests_" + Guid.NewGuid().ToString("N")[..8]);

    public AppLifecycleTests()
    {
        AppPaths.SetRootPathForTests(_testRoot);
    }

    public void Dispose()
    {
        AppPaths.SetRootPathForTests(null);

        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, true);
        }
    }

    private sealed class TrackingTailService : IFileTailService
    {
        public int DisposeCount { get; private set; }
        public int StopAllCount { get; private set; }

#pragma warning disable CS0067 // Event is never used
        public event EventHandler<TailEventArgs>? LinesAppended;
        public event EventHandler<FileRotatedEventArgs>? FileRotated;
        public event EventHandler<TailErrorEventArgs>? TailError;
#pragma warning restore CS0067

        public void StartTailing(string filePath, FileEncoding encoding, int pollingIntervalMs = 250) { }
        public void StopTailing(string filePath) { }
        public void StopAll() => StopAllCount++;
        public void Dispose() => DisposeCount++;
    }

    private sealed class StubStartupStorageCoordinator : IStartupStorageCoordinator
    {
        public StartupStorageResult Result { get; set; } = StartupStorageResult.Ready;

        public int CallCount { get; private set; }

        public StartupStorageResult EnsureStorageReady()
        {
            CallCount++;
            return Result;
        }
    }

    private sealed class StubBootstrapper : IAppBootstrapper
    {
        public Func<bool, Task<AppComposition>> OnCreateInitializedAsync { get; set; }
            = static _ => Task.FromException<AppComposition>(new InvalidOperationException("Bootstrapper not configured."));

        public int CallCount { get; private set; }

        public Task<AppComposition> CreateInitializedAsync(bool enableLifecycleTimer = true)
        {
            CallCount++;
            return OnCreateInitializedAsync(enableLifecycleTimer);
        }
    }

    private sealed class StubAppWindow : IAppWindow
    {
        public Window? Window => null;

        public int ShowCallCount { get; private set; }

        public int CloseCallCount { get; private set; }

        public Exception? ShowException { get; set; }

        public event CancelEventHandler? Closing;

        public void Show()
        {
            ShowCallCount++;
            if (ShowException != null)
                throw ShowException;
        }

        public void Close() => CloseCallCount++;

        public void RaiseClosing() => Closing?.Invoke(this, new CancelEventArgs());
    }

    private sealed class StubAppWindowFactory : IAppWindowFactory
    {
        public StubAppWindow Window { get; set; } = new();

        public int CreateCallCount { get; private set; }

        public MainViewModel? LastViewModel { get; private set; }

        public IAppWindow Create(MainViewModel viewModel)
        {
            CreateCallCount++;
            LastViewModel = viewModel;
            return Window;
        }
    }

    private static MainViewModel CreateViewModel(IFileTailService? tailService = null, ILogGroupRepository? groupRepo = null)
    {
        return new MainViewModel(
            new StubLogFileRepository(),
            groupRepo ?? new StubLogGroupRepository(),
            new StubSettingsRepository(),
            new StubLogReaderService(),
            new StubSearchService(),
            tailService ?? new StubFileTailService(),
            new FileEncodingDetectionService(),
            new LogTimestampNavigationService(),
            enableLifecycleTimer: false);
    }

    [Fact]
    public async Task CleanupFailedStartup_DisposesViewModelAndTailService()
    {
        var groupRepo = new StubLogGroupRepository();
        var tailService = new TrackingTailService();
        var vm = CreateViewModel(tailService: tailService, groupRepo: groupRepo);
        await vm.InitializeAsync();
        await vm.CreateGroupCommand.ExecuteAsync(null);
        var group = vm.Groups[0];

        Assert.Equal(1, TestHelpers.GetPropertyChangedSubscriberCount(group));

        App.CleanupFailedStartup((Window?)null, vm, tailService);

        Assert.Equal(0, TestHelpers.GetPropertyChangedSubscriberCount(group));
        Assert.Equal(1, tailService.DisposeCount);
    }

    [Fact]
    public async Task StartupRunner_WhenStartupSucceeds_ReturnsCompositionWithoutCreatingWindow()
    {
        var storageCoordinator = new StubStartupStorageCoordinator();
        var tailService = new TrackingTailService();
        var mainViewModel = CreateViewModel(tailService: tailService);
        await mainViewModel.InitializeAsync();
        var bootstrapper = new StubBootstrapper
        {
            OnCreateInitializedAsync = enableLifecycleTimer =>
            {
                Assert.True(enableLifecycleTimer);
                return Task.FromResult(new AppComposition(mainViewModel, tailService));
            }
        };
        var messageBoxService = new StubMessageBoxService();
        var cleanupCallCount = 0;
        var result = await new AppStartupRunner(
            storageCoordinator,
            bootstrapper,
            messageBoxService,
            () => cleanupCallCount++,
            App.BuildStartupFailureMessage).RunAsync();

        Assert.Equal(AppStartupStatus.Started, result.Status);
        Assert.Same(mainViewModel, result.MainViewModel);
        Assert.Same(tailService, result.TailService);
        Assert.Equal(1, storageCoordinator.CallCount);
        Assert.Equal(1, bootstrapper.CallCount);
        Assert.Equal(1, cleanupCallCount);
        Assert.Null(messageBoxService.LastMessage);
    }

    [Fact]
    public async Task StartupRunner_WhenStorageSetupCanceled_SkipsBootstrapAndReturnsCanceled()
    {
        var storageCoordinator = new StubStartupStorageCoordinator
        {
            Result = StartupStorageResult.Canceled
        };
        var bootstrapper = new StubBootstrapper();

        var result = await new AppStartupRunner(
            storageCoordinator,
            bootstrapper,
            new StubMessageBoxService(),
            () => throw new InvalidOperationException("Cleanup should not run."),
            App.BuildStartupFailureMessage).RunAsync();

        Assert.Equal(AppStartupStatus.Canceled, result.Status);
        Assert.Equal(1, storageCoordinator.CallCount);
        Assert.Equal(0, bootstrapper.CallCount);
        Assert.Null(result.MainViewModel);
    }

    [Fact]
    public async Task StartupRunner_WhenBootstrapFails_ShowsMessageAndReturnsFailed()
    {
        var storageCoordinator = new StubStartupStorageCoordinator();
        var bootstrapper = new StubBootstrapper
        {
            OnCreateInitializedAsync = _ => Task.FromException<AppComposition>(new InvalidOperationException("Bootstrap failed."))
        };
        var messageBoxService = new StubMessageBoxService();

        var result = await new AppStartupRunner(
            storageCoordinator,
            bootstrapper,
            messageBoxService,
            static () => { },
            App.BuildStartupFailureMessage).RunAsync();

        Assert.Equal(AppStartupStatus.Failed, result.Status);
        Assert.Equal("LogReader Startup Error", messageBoxService.LastCaption);
        Assert.Contains("Bootstrap failed.", messageBoxService.LastMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupUiCoordinator_WhenShowSucceeds_CreatesAndShowsWindowThroughInjectedFactory()
    {
        var tailService = new TrackingTailService();
        var mainViewModel = CreateViewModel(tailService: tailService);
        await mainViewModel.InitializeAsync();
        var windowFactory = new StubAppWindowFactory();
        var messageBoxService = new StubMessageBoxService();
        var closingCallCount = 0;

        var result = new AppStartupUiCoordinator(
            windowFactory,
            messageBoxService,
            App.BuildStartupFailureMessage)
            .ShowMainWindow(mainViewModel, tailService, (_, _) => closingCallCount++);

        Assert.True(result.Started);
        Assert.Same(windowFactory.Window, result.MainWindow);
        Assert.Equal(1, windowFactory.CreateCallCount);
        Assert.Equal(1, windowFactory.Window.ShowCallCount);
        Assert.Null(messageBoxService.LastMessage);

        windowFactory.Window.RaiseClosing();

        Assert.Equal(1, closingCallCount);
    }

    [Fact]
    public async Task StartupUiCoordinator_WhenShowFails_ShowsMessageAndDisposesComposition()
    {
        var groupRepo = new StubLogGroupRepository();
        var tailService = new TrackingTailService();
        var mainViewModel = CreateViewModel(tailService: tailService, groupRepo: groupRepo);
        await mainViewModel.InitializeAsync();
        await mainViewModel.CreateGroupCommand.ExecuteAsync(null);
        var subscribedGroup = mainViewModel.Groups[0];
        var windowFactory = new StubAppWindowFactory
        {
            Window = new StubAppWindow
            {
                ShowException = new InvalidOperationException("Window show failed.")
            }
        };
        var messageBoxService = new StubMessageBoxService();

        var result = new AppStartupUiCoordinator(
            windowFactory,
            messageBoxService,
            App.BuildStartupFailureMessage)
            .ShowMainWindow(mainViewModel, tailService, (_, _) => { });

        Assert.False(result.Started);
        Assert.Equal(1, windowFactory.Window.ShowCallCount);
        Assert.Equal(1, windowFactory.Window.CloseCallCount);
        Assert.Equal(1, tailService.DisposeCount);
        Assert.Equal(0, TestHelpers.GetPropertyChangedSubscriberCount(subscribedGroup));
        Assert.Equal("LogReader Startup Error", messageBoxService.LastCaption);
        Assert.Contains("Window show failed.", messageBoxService.LastMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppBootstrapper_WhenInjectedFactoryIsProvided_UsesInjectedComposition()
    {
        var tailService = new TrackingTailService();
        var mainViewModel = CreateViewModel(tailService: tailService);
        await mainViewModel.InitializeAsync();
        var bootstrapper = new AppBootstrapper(enableLifecycleTimer =>
        {
            Assert.False(enableLifecycleTimer);
            return Task.FromResult(new AppComposition(mainViewModel, tailService));
        });

        var composition = await bootstrapper.CreateInitializedAsync(enableLifecycleTimer: false);

        Assert.Same(mainViewModel, composition.MainViewModel);
        Assert.Same(tailService, composition.TailService);
    }

    [Fact]
    public async Task AppStartupFlow_WhenInjectedStartupRunnerAndUiCoordinatorAreUsed_ExecutesWithoutRealApplication()
    {
        var tailService = new TrackingTailService();
        var mainViewModel = CreateViewModel(tailService: tailService);
        await mainViewModel.InitializeAsync();
        var appWindowFactory = new StubAppWindowFactory();
        MainViewModel? capturedMainViewModel = null;
        IFileTailService? capturedTailService = null;
        Window? capturedMainWindow = null;
        var shutdownCallCount = 0;

        await App.RunStartupAsync(
            startupRunnerFactory: () => new AppStartupRunner(
                new StubStartupStorageCoordinator(),
                new StubBootstrapper
                {
                    OnCreateInitializedAsync = _ => Task.FromResult(new AppComposition(mainViewModel, tailService))
                },
                new StubMessageBoxService(),
                static () => { },
                App.BuildStartupFailureMessage),
            startupUiCoordinator: new AppStartupUiCoordinator(
                appWindowFactory,
                new StubMessageBoxService(),
                App.BuildStartupFailureMessage),
            setComposition: (vm, service) =>
            {
                capturedMainViewModel = vm;
                capturedTailService = service;
            },
            setMainWindow: window => capturedMainWindow = window,
            shutdownAction: () => shutdownCallCount++,
            closingHandler: (_, _) => { });

        Assert.Equal(1, appWindowFactory.CreateCallCount);
        Assert.Equal(1, appWindowFactory.Window.ShowCallCount);
        Assert.Same(mainViewModel, appWindowFactory.LastViewModel);
        Assert.Same(mainViewModel, capturedMainViewModel);
        Assert.Same(tailService, capturedTailService);
        Assert.Null(capturedMainWindow);
        Assert.Equal(0, shutdownCallCount);
    }

    [Fact]
    public async Task ShutdownCoordinator_PreparesIdempotently_AndCompletesOnce()
    {
        var tailService = new TrackingTailService();
        var vm = CreateViewModel(tailService: tailService);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");

        MainViewModel? capturedVm = vm;
        IFileTailService? capturedTailService = tailService;
        var coordinator = new AppShutdownCoordinator(
            () => capturedVm,
            () => capturedTailService,
            () =>
            {
                capturedVm = null;
                capturedTailService = null;
            });

        coordinator.Prepare();
        coordinator.Prepare();
        coordinator.Complete();
        coordinator.Complete();

        Assert.True(vm.IsShuttingDown);
        Assert.Equal(1, tailService.StopAllCount);
        Assert.Equal(1, tailService.DisposeCount);
        Assert.Null(capturedVm);
        Assert.Null(capturedTailService);
    }

    [Fact]
    public async Task ShutdownCoordinator_Complete_DoesNotBlockOnPendingTabCleanup()
    {
        var tailService = new TrackingTailService();
        var vm = CreateViewModel(tailService: tailService);
        await vm.InitializeAsync();
        await vm.OpenFilePathAsync(@"C:\test\a.log");

        var tab = vm.Tabs[0];
        var lineIndexLockField = typeof(LogTabViewModel).GetField("_lineIndexLock", BindingFlags.Instance | BindingFlags.NonPublic);
        var lineIndexDisposeTaskField = typeof(LogTabViewModel).GetField("_lineIndexDisposeTask", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(lineIndexLockField);
        Assert.NotNull(lineIndexDisposeTaskField);

        var lineIndexLock = (SemaphoreSlim)lineIndexLockField!.GetValue(tab)!;
        await lineIndexLock.WaitAsync();

        MainViewModel? capturedVm = vm;
        IFileTailService? capturedTailService = tailService;
        var coordinator = new AppShutdownCoordinator(
            () => capturedVm,
            () => capturedTailService,
            () =>
            {
                capturedVm = null;
                capturedTailService = null;
            });

        Task? disposeTask = null;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            coordinator.Complete();
            stopwatch.Stop();

            disposeTask = (Task?)lineIndexDisposeTaskField!.GetValue(tab);
            Assert.NotNull(disposeTask);
            Assert.False(disposeTask!.IsCompleted);
            Assert.InRange(stopwatch.ElapsedMilliseconds, 0, 500);
        }
        finally
        {
            if (lineIndexLock.CurrentCount == 0)
                lineIndexLock.Release();
        }

        await disposeTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, tailService.DisposeCount);
        Assert.Null(capturedVm);
        Assert.Null(capturedTailService);
    }

    [Fact]
    public void BuildStartupFailureMessage_ForStorageError_IncludesDataPathAndGuidance()
    {
        var ex = new IOException("The process cannot access the file because it is being used by another process.");

        var message = App.BuildStartupFailureMessage(ex);

        Assert.Contains(AppPaths.DataDirectory, message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("saved data", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not locked", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ex.Message, message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildStartupFailureMessage_ForInstallConfigurationError_IncludesConfigGuidance()
    {
        var ex = new InstallConfigurationException(
            "The install configuration file is missing.",
            @"C:\Program Files\LogReader\LogReader.install.json");

        var message = App.BuildStartupFailureMessage(ex);

        Assert.Contains("install configuration", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LogReader.install.json", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Debug build", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildStartupFailureMessage_ForProtectedStorageError_IncludesProtectedGuidance()
    {
        var ex = new ProtectedStorageLocationException(@"C:\Program Files\LogReader");

        var message = App.BuildStartupFailureMessage(ex);

        Assert.Contains("protected", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"C:\Program Files\LogReader", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Program Files", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildStartupFailureMessage_ForNestedUnauthorizedAccess_IncludesStorageSpecificMessage()
    {
        var ex = new InvalidOperationException(
            "Startup failed.",
            new UnauthorizedAccessException("Access to the path was denied."));

        var message = App.BuildStartupFailureMessage(ex);

        Assert.Contains(AppPaths.DataDirectory, message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("permission", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Access to the path was denied.", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildStartupFailureMessage_ForNonStorageError_UsesGenericMessage()
    {
        var ex = new InvalidOperationException("Boom");

        var message = App.BuildStartupFailureMessage(ex);

        Assert.DoesNotContain(AppPaths.DataDirectory, message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Boom", message, StringComparison.OrdinalIgnoreCase);
    }
}
