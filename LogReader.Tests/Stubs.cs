namespace LogReader.Tests;

using System.Reflection;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

/// <summary>
/// Shared test stubs and helpers used across multiple test classes.
/// </summary>
internal class StubLogReaderService : ILogReaderService
{
    private readonly int _lineCount;
    public int BuildIndexCallCount { get; private set; }
    public int UpdateIndexCallCount { get; private set; }
    public int ReadLinesCallCount { get; private set; }
    public FileEncoding LastBuildEncoding { get; private set; } = FileEncoding.Utf8;
    public List<FileEncoding> AttemptedBuildEncodings { get; } = new();
    public HashSet<FileEncoding> BuildFailures { get; } = new();

    public StubLogReaderService(int lineCount = 200) => _lineCount = lineCount;

    public Task<LineIndex> BuildIndexAsync(string filePath, FileEncoding encoding, CancellationToken ct = default)
    {
        BuildIndexCallCount++;
        LastBuildEncoding = encoding;
        AttemptedBuildEncodings.Add(encoding);
        if (BuildFailures.Contains(encoding))
            throw new InvalidOperationException($"Simulated failure for encoding {encoding}");

        var index = new LineIndex
        {
            FilePath = filePath,
            FileSize = _lineCount * 100
        };
        for (int i = 0; i < _lineCount; i++)
            index.LineOffsets.Add(i * 100L);
        return Task.FromResult(index);
    }

    public Task<LineIndex> UpdateIndexAsync(string filePath, LineIndex existingIndex, FileEncoding encoding, CancellationToken ct = default)
    {
        UpdateIndexCallCount++;
        return Task.FromResult(existingIndex);
    }

    public Task<IReadOnlyList<string>> ReadLinesAsync(string filePath, LineIndex index, int startLine, int count, FileEncoding encoding, CancellationToken ct = default)
    {
        ReadLinesCallCount++;
        var lines = new List<string>();
        int actualCount = Math.Min(count, _lineCount - Math.Max(0, startLine));
        for (int i = 0; i < actualCount; i++)
            lines.Add($"Line {startLine + i + 1} content");
        return Task.FromResult<IReadOnlyList<string>>(lines);
    }

    public Task<string> ReadLineAsync(string filePath, LineIndex index, int lineNumber, FileEncoding encoding, CancellationToken ct = default)
        => Task.FromResult($"Line {lineNumber + 1} content");
}

internal class StubFileTailService : IFileTailService
{
#pragma warning disable CS0067 // Event is never used
    public event EventHandler<TailEventArgs>? LinesAppended;
    public event EventHandler<FileRotatedEventArgs>? FileRotated;
    public event EventHandler<TailErrorEventArgs>? TailError;
#pragma warning restore CS0067
    public int StartCallCount { get; private set; }
    public int StopCallCount { get; private set; }
    public int StopAllCount { get; private set; }
    public int DisposeCount { get; private set; }
    public HashSet<string> ActiveFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> StartedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> StoppedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> PollingByFile { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void StartTailing(string filePath, FileEncoding encoding, int pollingIntervalMs = 250)
    {
        StartCallCount++;
        ActiveFiles.Add(filePath);
        StartedFiles.Add(filePath);
        PollingByFile[filePath] = pollingIntervalMs;
    }

    public void StopTailing(string filePath)
    {
        StopCallCount++;
        ActiveFiles.Remove(filePath);
        StoppedFiles.Add(filePath);
        PollingByFile.Remove(filePath);
    }

    public void StopAll()
    {
        StopAllCount++;
        var files = ActiveFiles.ToList();
        foreach (var file in files)
            StopTailing(file);
    }

    public void RaiseTailError(string filePath, string errorMessage)
    {
        TailError?.Invoke(this, new TailErrorEventArgs
        {
            FilePath = filePath,
            ErrorMessage = errorMessage
        });
    }

    public void Dispose() => DisposeCount++;
}

internal class StubLogFileRepository : ILogFileRepository
{
    private readonly List<LogFileEntry> _entries = new();

    public Task<List<LogFileEntry>> GetAllAsync() => Task.FromResult(_entries.ToList());

    public Task<LogFileEntry?> GetByIdAsync(string id)
        => Task.FromResult(_entries.FirstOrDefault(e => e.Id == id));

    public Task<LogFileEntry?> GetByPathAsync(string filePath)
        => Task.FromResult(_entries.FirstOrDefault(e =>
            string.Equals(e.FilePath, filePath, StringComparison.OrdinalIgnoreCase)));

    public Task AddAsync(LogFileEntry entry) { _entries.Add(entry); return Task.CompletedTask; }
    public Task UpdateAsync(LogFileEntry entry) => Task.CompletedTask;
    public Task DeleteAsync(string id) { _entries.RemoveAll(e => e.Id == id); return Task.CompletedTask; }
}

internal class StubLogGroupRepository : ILogGroupRepository
{
    private readonly List<LogGroup> _groups = new();

    public Task<List<LogGroup>> GetAllAsync() => Task.FromResult(_groups.ToList());
    public Task<LogGroup?> GetByIdAsync(string id)
        => Task.FromResult(_groups.FirstOrDefault(g => g.Id == id));
    public Task AddAsync(LogGroup group) { _groups.Add(group); return Task.CompletedTask; }
    public Task UpdateAsync(LogGroup group) => Task.CompletedTask;
    public Task DeleteAsync(string id) { _groups.RemoveAll(g => g.Id == id); return Task.CompletedTask; }
    public Task ReorderAsync(List<string> orderedIds) => Task.CompletedTask;
    public Task ExportGroupAsync(string groupId, string exportPath) => Task.CompletedTask;
    public Task<GroupExport?> ImportGroupAsync(string importPath) => Task.FromResult<GroupExport?>(null);
}

internal class StubSettingsRepository : ISettingsRepository
{
    public AppSettings Settings { get; set; } = new();
    public Task<AppSettings> LoadAsync() => Task.FromResult(Settings);
    public Task SaveAsync(AppSettings settings) { Settings = settings; return Task.CompletedTask; }
}

internal class StubSearchService : ISearchService
{
    public Task<SearchResult> SearchFileAsync(string filePath, SearchRequest request, FileEncoding encoding, CancellationToken ct = default)
        => Task.FromResult(new SearchResult { FilePath = filePath });
    public Task<IReadOnlyList<SearchResult>> SearchFilesAsync(SearchRequest request, IDictionary<string, FileEncoding> fileEncodings, CancellationToken ct = default, int maxConcurrency = 4)
        => Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());
}

internal static class TestHelpers
{
    public static int GetPropertyChangedSubscriberCount(object instance)
    {
        var field = instance.GetType().BaseType?.GetField("PropertyChanged",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var handlers = (MulticastDelegate?)field!.GetValue(instance);
        return handlers?.GetInvocationList().Length ?? 0;
    }
}
