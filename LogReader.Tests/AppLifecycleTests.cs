namespace LogReader.Tests;

using LogReader.App;
using LogReader.Core;
using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

public class AppLifecycleTests
{
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

        App.CleanupFailedStartup(null, vm, tailService);

        Assert.Equal(0, TestHelpers.GetPropertyChangedSubscriberCount(group));
        Assert.Equal(1, tailService.DisposeCount);
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
