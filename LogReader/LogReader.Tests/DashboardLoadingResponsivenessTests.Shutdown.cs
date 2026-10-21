namespace LogReader.Tests;

using System.ComponentModel;
using System.IO;
using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.Core.Models;

public sealed partial class DashboardLoadingResponsivenessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionShutdownCoordinator_DrainsExpectedPreviewFailures(bool accessDenied)
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync();
            fixture.Reader.HoldPreview(ignoreCancellation: true);
            fixture.Reader.PreviewFailure = CreatePreviewFailure(accessDenied);
            var load = fixture.ViewModel.HandleDashboardGroupInvokedAsync(fixture.ViewModel.Groups[0]);
            await fixture.Reader.PreviewStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.Reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var coordinator = new LogReader.App.AppShutdownCoordinator(() => fixture.ViewModel, () => fixture.Tail, () => { });
            var drain = coordinator.DrainAsync();
            Assert.False(drain.IsCompleted);
            fixture.Reader.ReleasePreview();
            await drain.WaitAsync(TimeSpan.FromSeconds(5));
            await load.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(fixture.ViewModel.ShutdownTask.IsCompletedSuccessfully);
            await fixture.Reader.BlockedBuildDisposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            coordinator.Complete();
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpectedPreviewFailure_RemainsVisibleWithoutFailingTheIndex(bool accessDenied)
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var fixture = await CreateAsync();
            fixture.Reader.HoldPreview();
            var error = CreatePreviewFailure(accessDenied);
            fixture.Reader.PreviewFailure = error;
            var load = fixture.ViewModel.HandleDashboardGroupInvokedAsync(fixture.ViewModel.Groups[0]);
            await fixture.Reader.PreviewStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var tab = fixture.ViewModel.SelectedTab!;
            var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed(object? sender, PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(LogTabViewModel.PreviewStatusText) && tab.PreviewStatusText.Contains(error.Message))
                    published.TrySetResult();
            }
            tab.PropertyChanged += Changed;
            try
            {
                fixture.Reader.ReleasePreview();
                await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(tab.HasPreview);
                Assert.False(tab.HasLoadError);
                fixture.Reader.Release();
                await load.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(tab.IsIndexReady);
            }
            finally { tab.PropertyChanged -= Changed; }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionDrain_SeparatesCancellationAndReadFailures(bool unexpected)
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var path = Path.Combine(_root, "drain.log");
            var reader = new GatedInteractiveReader(path);
            reader.HoldPreview(ignoreCancellation: true);
            var error = unexpected ? new InvalidOperationException("Unexpected preview failure") : (Exception)new IOException("Expected preview failure");
            reader.PreviewFailure = error;
            using var scheduler = new DashboardLoadScheduler();
            using var tail = new StubFileTailService();
            using var session = new FileSession(new FileSessionKey(path, FileEncoding.Utf8), reader, tail,
                new StubEncodingDetectionService(), loadScheduler: scheduler);
            var read = session.ReadRecentPreviewAsync(default);
            await reader.PreviewStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancellation = new CancellationTokenSource();
            var canceledRead = session.ReadRecentPreviewAsync(cancellation.Token);
            var drain = session.ShutdownTask;
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledRead);
            Assert.False(drain.IsCompleted);
            reader.ReleasePreview();
            Assert.Same(error, await Assert.ThrowsAnyAsync<Exception>(() => read));
            if (unexpected)
                Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => drain));
            else
                await drain.WaitAsync(TimeSpan.FromSeconds(5));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionDrain_PreservesIndexDisposalFailures(bool accessDenied)
    {
        await WpfTestHost.RunAsync(async () =>
        {
            using var tail = new StubFileTailService();
            using var session = new FileSession(new FileSessionKey(Path.Combine(_root, "cleanup.log"), FileEncoding.Utf8),
                new StubLogReaderService(), tail, new StubEncodingDetectionService());
            var error = CreatePreviewFailure(accessDenied);
            session.DebugLineIndexDisposeTask = Task.FromException(error);
            try
            {
                Assert.Same(error, await Assert.ThrowsAnyAsync<Exception>(() => session.ShutdownTask));
            }
            finally { session.DebugLineIndexDisposeTask = null; }
        });
    }

    private static Exception CreatePreviewFailure(bool accessDenied)
        => accessDenied ? new UnauthorizedAccessException("Synthetic preview access failure") : new IOException("Synthetic preview IO failure");
}
