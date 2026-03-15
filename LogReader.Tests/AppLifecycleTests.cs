namespace LogReader.Tests;

using System.Diagnostics;
using LogReader.App;
using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public class AppLifecycleTests
{
    private sealed class SlowSessionRepository : ISessionRepository
    {
        public bool SaveStarted { get; private set; }

        public Task<SessionState> LoadAsync() => Task.FromResult(new SessionState());

        public async Task SaveAsync(SessionState state)
        {
            SaveStarted = true;
            await Task.Delay(TimeSpan.FromSeconds(30));
        }
    }

    private sealed class CountingSessionRepository : ISessionRepository
    {
        public int SaveCount { get; private set; }

        public Task<SessionState> LoadAsync() => Task.FromResult(new SessionState());

        public Task SaveAsync(SessionState state)
        {
            SaveCount++;
            return Task.CompletedTask;
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

    private static MainViewModel CreateViewModel(ISessionRepository? sessionRepo = null, IFileTailService? tailService = null, ILogGroupRepository? groupRepo = null)
    {
        return new MainViewModel(
            new StubLogFileRepository(),
            groupRepo ?? new StubLogGroupRepository(),
            sessionRepo ?? new SlowSessionRepository(),
            new StubSettingsRepository(),
            new StubLogReaderService(),
            new StubSearchService(),
            tailService ?? new StubFileTailService(),
            enableLifecycleTimer: false);
    }

    [Fact]
    public async Task TrySaveSessionOnExitAsync_TimesOutWithoutBlockingShutdownIndefinitely()
    {
        var sessionRepo = new SlowSessionRepository();
        var vm = CreateViewModel(sessionRepo: sessionRepo);
        await vm.InitializeAsync();
        var stopwatch = Stopwatch.StartNew();

        var saved = await App.TrySaveSessionOnExitAsync(vm, TimeSpan.FromMilliseconds(100));

        stopwatch.Stop();

        Assert.False(saved);
        Assert.True(sessionRepo.SaveStarted);
        Assert.InRange(stopwatch.ElapsedMilliseconds, 0, 1000);
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

        App.CleanupFailedStartup(null, vm, tailService);

        Assert.Equal(0, TestHelpers.GetPropertyChangedSubscriberCount(group));
        Assert.Equal(1, tailService.DisposeCount);
    }

    [Fact]
    public async Task ShutdownCoordinator_PreparesIdempotently_AndCompletesOnce()
    {
        var sessionRepo = new CountingSessionRepository();
        var tailService = new TrackingTailService();
        var vm = CreateViewModel(sessionRepo: sessionRepo, tailService: tailService);
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
        coordinator.Complete(TimeSpan.FromSeconds(1));
        coordinator.Complete(TimeSpan.FromSeconds(1));

        Assert.True(vm.IsShuttingDown);
        Assert.Equal(1, sessionRepo.SaveCount);
        Assert.Equal(1, tailService.StopAllCount);
        Assert.Equal(1, tailService.DisposeCount);
        Assert.Null(capturedVm);
        Assert.Null(capturedTailService);
    }
}
