namespace LogReader.Tests;

using System.IO;
using System.Text.Json;
using LogReader.App.Services;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public sealed class UiStatePersistenceTests
{
    private static UiState State(bool expanded) => new() { GroupExpansionById = new() { ["id"] = expanded } };

    [Fact]
    public async Task StartupAndUnchangedClose_DoNotWrite()
    {
        var repository = new RecordingRepository();
        var service = new UiStatePersistenceService(repository, _ => Assert.Fail("Unexpected failure"));
        await service.LoadAsync();
        service.Start(State(false));
        service.Queue(State(false));
        await service.FlushAsync(State(false), TimeSpan.FromSeconds(1));
        Assert.Empty(repository.Saves);
    }

    [Fact]
    public async Task RapidToggles_CoalesceAndCaptureImmutableSnapshot()
    {
        var repository = new RecordingRepository();
        var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new UiStatePersistenceService(repository, _ => Assert.Fail("Unexpected failure"), async (duration, ct) =>
        {
            Assert.Equal(TimeSpan.FromMilliseconds(300), duration);
            delayStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        });
        service.Start(State(false));
        service.Queue(State(true));
        await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.Queue(State(false));
        var snapshot = State(true);
        service.Queue(snapshot);
        snapshot.GroupExpansionById["id"] = false;
        await service.FlushAsync(State(true), TimeSpan.FromSeconds(5));
        Assert.True(Assert.Single(repository.Saves).GroupExpansionById["id"]);
    }

    [Fact]
    public async Task SlowSave_DoesNotBlockToggles_AndSavesOnlyLatestPendingState()
    {
        var repository = new RecordingRepository();
        var saving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.OnSave = async _ =>
        {
            if (repository.Saves.Count == 1)
            {
                saving.TrySetResult();
                await release.Task;
            }
        };
        var service = new UiStatePersistenceService(repository, _ => Assert.Fail("Unexpected failure"), (_, _) => Task.CompletedTask);
        service.Start(State(false));
        service.Queue(State(true));
        await saving.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.Queue(State(false));
        service.Queue(new UiState { GroupExpansionById = new() { ["id"] = false, ["new"] = true } });
        var flush = service.FlushAsync(new UiState { GroupExpansionById = new() { ["id"] = false, ["new"] = true } }, TimeSpan.FromSeconds(5));
        Assert.False(flush.IsCompleted);
        Assert.Single(repository.Saves);
        release.SetResult();
        await flush;
        Assert.Equal(2, repository.Saves.Count);
        Assert.False(repository.Saves[1].GroupExpansionById["id"]);
        Assert.True(repository.Saves[1].GroupExpansionById["new"]);
        Assert.Equal(1, repository.MaxConcurrentSaves);
    }

    [Fact]
    public async Task LoadAndSaveFailures_NoticeOnce_NoStartupOverwrite_RetryOnClose()
    {
        var failures = new List<Exception>();
        var repository = new RecordingRepository { LoadFailure = new JsonException("invalid ui state") };
        repository.OnSave = _ => Task.FromException(new IOException("locked"));
        var service = new UiStatePersistenceService(repository, failures.Add);
        Assert.Empty((await service.LoadAsync()).GroupExpansionById);
        service.Start(State(false));
        Assert.Empty(repository.Saves);
        await service.FlushAsync(State(false), TimeSpan.FromSeconds(5));
        Assert.Single(repository.Saves);
        Assert.Single(failures);
        repository.OnSave = null;
        await service.FlushAsync(State(false), TimeSpan.FromSeconds(5));
        Assert.Equal(2, repository.Saves.Count);
    }

    [Fact]
    public async Task FailedBackgroundSave_RetriesOnNextChangeWithoutRetryLoop()
    {
        var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new RecordingRepository { OnSave = _ => Task.FromException(new IOException("locked")) };
        var notices = 0;
        var service = new UiStatePersistenceService(repository, _ =>
        {
            Interlocked.Increment(ref notices);
            failure.TrySetResult();
        }, (_, _) => Task.CompletedTask);
        service.Start(State(false));
        service.Queue(State(true));
        await failure.Task.WaitAsync(TimeSpan.FromSeconds(5));
        repository.OnSave = null;
        service.Queue(State(false));
        await service.FlushAsync(State(false), TimeSpan.FromSeconds(5));
        Assert.Equal(2, repository.Saves.Count);
        Assert.False(repository.Saves[1].GroupExpansionById["id"]);
        Assert.Equal(1, notices);
    }

    [Fact]
    public async Task CloseTimeout_DoesNotBlockShutdownOrStartOverlappingSave()
    {
        var repository = new RecordingRepository();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.OnSave = _ => release.Task;
        var failures = new List<Exception>();
        var service = new UiStatePersistenceService(repository, failures.Add);
        service.Start(State(false));
        await service.FlushAsync(State(true), TimeSpan.FromMilliseconds(50));
        Assert.IsType<TimeoutException>(Assert.Single(failures));
        Assert.Single(repository.Saves);
        service.Queue(State(false));
        release.SetResult();
        await service.FlushAsync(State(true), TimeSpan.FromSeconds(5));
        Assert.Equal(1, repository.MaxConcurrentSaves);
    }

    internal sealed class RecordingRepository : IUiStateRepository
    {
        internal List<UiState> Saves { get; } = new();
        internal UiState Loaded { get; set; } = new();
        internal Exception? LoadFailure { get; set; }
        internal Func<UiState, Task>? OnSave { get; set; }
        internal int MaxConcurrentSaves { get; private set; }
        private int _saving;
        public Task<UiState> LoadAsync() => LoadFailure == null ? Task.FromResult(Loaded) : Task.FromException<UiState>(LoadFailure);
        public async Task SaveAsync(UiState state)
        {
            MaxConcurrentSaves = Math.Max(MaxConcurrentSaves, Interlocked.Increment(ref _saving));
            Saves.Add(state);
            try
            {
                if (OnSave != null)
                    await OnSave(state);
                Loaded = state;
            }
            finally { Interlocked.Decrement(ref _saving); }
        }
    }
}
