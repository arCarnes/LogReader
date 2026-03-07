using LogReader.App.ViewModels;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

namespace LogReader.Tests;

public class SearchPanelViewModelTests
{
    private sealed class StubLogFileRepository : ILogFileRepository
    {
        private readonly List<LogFileEntry> _entries = new();

        public Task<List<LogFileEntry>> GetAllAsync() => Task.FromResult(_entries.ToList());
        public Task<LogFileEntry?> GetByIdAsync(string id) => Task.FromResult(_entries.FirstOrDefault(e => e.Id == id));
        public Task<LogFileEntry?> GetByPathAsync(string filePath)
            => Task.FromResult(_entries.FirstOrDefault(e => string.Equals(e.FilePath, filePath, StringComparison.OrdinalIgnoreCase)));
        public Task AddAsync(LogFileEntry entry) { _entries.Add(entry); return Task.CompletedTask; }
        public Task UpdateAsync(LogFileEntry entry) => Task.CompletedTask;
        public Task DeleteAsync(string id) { _entries.RemoveAll(e => e.Id == id); return Task.CompletedTask; }
    }

    private sealed class StubLogGroupRepository : ILogGroupRepository
    {
        private readonly List<LogGroup> _groups = new();

        public Task<List<LogGroup>> GetAllAsync() => Task.FromResult(_groups.ToList());
        public Task<LogGroup?> GetByIdAsync(string id) => Task.FromResult(_groups.FirstOrDefault(g => g.Id == id));
        public Task AddAsync(LogGroup group) { _groups.Add(group); return Task.CompletedTask; }
        public Task UpdateAsync(LogGroup group) => Task.CompletedTask;
        public Task DeleteAsync(string id) { _groups.RemoveAll(g => g.Id == id); return Task.CompletedTask; }
        public Task ReorderAsync(List<string> orderedIds) => Task.CompletedTask;
        public Task ExportGroupAsync(string groupId, string exportPath) => Task.CompletedTask;
        public Task<GroupExport?> ImportGroupAsync(string importPath) => Task.FromResult<GroupExport?>(null);
    }

    private sealed class StubSessionRepository : ISessionRepository
    {
        public SessionState State { get; set; } = new();
        public Task<SessionState> LoadAsync() => Task.FromResult(State);
        public Task SaveAsync(SessionState state) { State = state; return Task.CompletedTask; }
    }

    private sealed class StubSettingsRepository : ISettingsRepository
    {
        public AppSettings Settings { get; set; } = new();
        public Task<AppSettings> LoadAsync() => Task.FromResult(Settings);
        public Task SaveAsync(AppSettings settings) { Settings = settings; return Task.CompletedTask; }
    }

    private sealed class RecordingSearchService : ISearchService
    {
        public SearchRequest? LastRequest { get; private set; }
        public IDictionary<string, FileEncoding>? LastEncodings { get; private set; }
        public IReadOnlyList<SearchResult> NextResults { get; set; } = Array.Empty<SearchResult>();

        public Task<SearchResult> SearchFileAsync(string filePath, SearchRequest request, FileEncoding encoding, CancellationToken ct = default)
            => Task.FromResult(new SearchResult { FilePath = filePath });

        public Task<IReadOnlyList<SearchResult>> SearchFilesAsync(SearchRequest request, IDictionary<string, FileEncoding> fileEncodings, CancellationToken ct = default, int maxConcurrency = 4)
        {
            LastRequest = request;
            LastEncodings = new Dictionary<string, FileEncoding>(fileEncodings, StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(NextResults);
        }
    }

    private static MainViewModel CreateMainViewModel(ILogFileRepository fileRepo, ILogGroupRepository groupRepo, ISettingsRepository settingsRepo, ISearchService search)
    {
        return new MainViewModel(
            fileRepo,
            groupRepo,
            new StubSessionRepository(),
            settingsRepo,
            new StubLogReaderService(),
            search,
            new StubFileTailService(),
            enableLifecycleTimer: false);
    }

    [Fact]
    public async Task ExecuteSearch_CurrentFile_UsesSelectedTabPathAndEncoding()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");
        mainVm.SelectedTab!.Encoding = FileEncoding.Utf16Be;

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "error"
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.NotNull(search.LastRequest);
        Assert.Equal(new[] { @"C:\logs\b.log" }, search.LastRequest!.FilePaths);
        Assert.NotNull(search.LastEncodings);
        Assert.Equal(FileEncoding.Utf16Be, search.LastEncodings![@"C:\logs\b.log"]);
    }

    [Fact]
    public async Task ExecuteSearch_AllFiles_UsesAllOpenTabs()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");
        mainVm.Tabs[0].Encoding = FileEncoding.Ansi;
        mainVm.Tabs[1].Encoding = FileEncoding.Utf16;

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "warn",
            AllFiles = true
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.NotNull(search.LastRequest);
        Assert.Equal(2, search.LastRequest!.FilePaths.Count);
        Assert.Equal(FileEncoding.Ansi, search.LastEncodings![@"C:\logs\a.log"]);
        Assert.Equal(FileEncoding.Utf16, search.LastEncodings![@"C:\logs\b.log"]);
    }

    [Fact]
    public async Task ExecuteSearch_CurrentFile_DoesNotIncludeOtherOpenTabs()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();
        await mainVm.OpenFilePathAsync(@"C:\logs\a.log");
        await mainVm.OpenFilePathAsync(@"C:\logs\b.log");

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "fatal"
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.NotNull(search.LastRequest);
        Assert.Equal(new[] { @"C:\logs\b.log" }, search.LastRequest!.FilePaths);
    }

    [Fact]
    public async Task ExecuteSearch_NoFilesInScope_SetsStatusAndSkipsSearch()
    {
        var fileRepo = new StubLogFileRepository();
        var groupRepo = new StubLogGroupRepository();
        var search = new RecordingSearchService();
        var mainVm = CreateMainViewModel(fileRepo, groupRepo, new StubSettingsRepository(), search);
        await mainVm.InitializeAsync();

        var panel = new SearchPanelViewModel(search, mainVm)
        {
            Query = "anything"
        };

        await panel.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.Equal("No files to search", panel.StatusText);
        Assert.Null(search.LastRequest);
    }
}
