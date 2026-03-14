namespace LogReader.Tests;

using System.Diagnostics;
using LogReader.App;
using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public class AppLifecycleTests
{
    private class StubLogFileRepository : ILogFileRepository
    {
        public Task<List<LogFileEntry>> GetAllAsync() => Task.FromResult(new List<LogFileEntry>());
        public Task<LogFileEntry?> GetByIdAsync(string id) => Task.FromResult<LogFileEntry?>(null);
        public Task<LogFileEntry?> GetByPathAsync(string filePath) => Task.FromResult<LogFileEntry?>(null);
        public Task AddAsync(LogFileEntry entry) => Task.CompletedTask;
        public Task UpdateAsync(LogFileEntry entry) => Task.CompletedTask;
        public Task DeleteAsync(string id) => Task.CompletedTask;
    }

    private class StubLogGroupRepository : ILogGroupRepository
    {
        private readonly List<LogGroup> _groups = new();

        public Task<List<LogGroup>> GetAllAsync() => Task.FromResult(_groups.ToList());
        public Task<LogGroup?> GetByIdAsync(string id) => Task.FromResult(_groups.FirstOrDefault(g => g.Id == id));
        public Task AddAsync(LogGroup group)
        {
            _groups.Add(group);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(LogGroup group) => Task.CompletedTask;
        public Task DeleteAsync(string id) => Task.CompletedTask;
        public Task ReorderAsync(List<string> orderedIds) => Task.CompletedTask;
        public Task ExportGroupAsync(string groupId, string exportPath) => Task.CompletedTask;
        public Task<GroupExport?> ImportGroupAsync(string importPath) => Task.FromResult<GroupExport?>(null);
    }

    private class StubSettingsRepository : ISettingsRepository
    {
        public Task<AppSettings> LoadAsync() => Task.FromResult(new AppSettings());
        public Task SaveAsync(AppSettings settings) => Task.CompletedTask;
    }

    private class StubSearchService : ISearchService
    {
        public Task<SearchResult> SearchFileAsync(string filePath, SearchRequest request, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(new SearchResult { FilePath = filePath });

        public Task<IReadOnlyList<SearchResult>> SearchFilesAsync(SearchRequest request, IDictionary<string, FileEncoding> fileEncodings, CancellationToken ct = default, int maxConcurrency = 4)
            => Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());
    }

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

    private sealed class TrackingTailService : IFileTailService
    {
        public int DisposeCount { get; private set; }

        public event EventHandler<TailEventArgs>? LinesAppended;
        public event EventHandler<FileRotatedEventArgs>? FileRotated;
        public event EventHandler<TailErrorEventArgs>? TailError;

        public void StartTailing(string filePath, FileEncoding encoding, int pollingIntervalMs = 250) { }
        public void StopTailing(string filePath) { }
        public void StopAll() { }
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

    private static int GetPropertyChangedSubscriberCount(object instance)
    {
        var field = instance.GetType().BaseType?.GetField("PropertyChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        var handlers = (MulticastDelegate?)field!.GetValue(instance);
        return handlers?.GetInvocationList().Length ?? 0;
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

        Assert.Equal(1, GetPropertyChangedSubscriberCount(group));

        App.CleanupFailedStartup(null, vm, tailService);

        Assert.Equal(0, GetPropertyChangedSubscriberCount(group));
        Assert.Equal(1, tailService.DisposeCount);
    }
}
