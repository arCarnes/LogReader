namespace LogReader.Testing;

using System.Reflection;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

/// <summary>
/// Shared test stubs and helpers used across multiple test suites.
/// </summary>
public class StubLogReaderService : ILogReaderService
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

public class StubFileTailService : IFileTailService
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
        if (string.IsNullOrWhiteSpace(filePath))
            return;

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

    public void RaiseLinesAppended(string filePath)
    {
        LinesAppended?.Invoke(this, new TailEventArgs
        {
            FilePath = filePath
        });
    }

    public void RaiseFileRotated(string filePath)
    {
        FileRotated?.Invoke(this, new FileRotatedEventArgs
        {
            FilePath = filePath
        });
    }

    public void Dispose() => DisposeCount++;
}

public class StubLogFileRepository : ILogFileRepository
{
    private readonly List<LogFileEntry> _entries = new();

    public Task<List<LogFileEntry>> GetAllAsync() => Task.FromResult(_entries.ToList());

    public Task<IReadOnlyDictionary<string, LogFileEntry>> GetByIdsAsync(IEnumerable<string> ids)
    {
        var idSet = ids.ToHashSet(StringComparer.Ordinal);
        return Task.FromResult<IReadOnlyDictionary<string, LogFileEntry>>(
            _entries
                .Where(entry => idSet.Contains(entry.Id))
                .ToDictionary(entry => entry.Id, StringComparer.Ordinal));
    }

    public Task<IReadOnlyDictionary<string, LogFileEntry>> GetByPathsAsync(IEnumerable<string> filePaths)
    {
        var pathSet = filePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Task.FromResult<IReadOnlyDictionary<string, LogFileEntry>>(
            _entries
                .Where(entry => pathSet.Contains(entry.FilePath))
                .ToDictionary(entry => entry.FilePath, StringComparer.OrdinalIgnoreCase));
    }

    public Task<IReadOnlyDictionary<string, LogFileEntry>> GetOrCreateByPathsAsync(IEnumerable<string> filePaths)
    {
        var result = new Dictionary<string, LogFileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var filePath in filePaths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
            result[filePath] = GetOrCreateEntry(filePath);

        return Task.FromResult<IReadOnlyDictionary<string, LogFileEntry>>(result);
    }

    public Task<LogFileEntry> GetOrCreateByPathAsync(string filePath, DateTime? lastOpenedAtUtc = null)
    {
        var existing = GetOrCreateEntry(filePath);
        if (lastOpenedAtUtc.HasValue)
            existing.LastOpenedAt = lastOpenedAtUtc.Value;

        return Task.FromResult(existing);
    }

    private LogFileEntry GetOrCreateEntry(string filePath)
    {
        var existing = _entries.FirstOrDefault(entry =>
            string.Equals(entry.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            return existing;

        var entry = new LogFileEntry
        {
            FilePath = filePath
        };
        _entries.Add(entry);
        return entry;
    }

    public Task AddAsync(LogFileEntry entry) { _entries.Add(entry); return Task.CompletedTask; }
    public Task UpdateAsync(LogFileEntry entry) => Task.CompletedTask;
    public Task DeleteAsync(string id) { _entries.RemoveAll(e => e.Id == id); return Task.CompletedTask; }
}

public class StubLogGroupRepository : ILogGroupRepository
{
    private readonly List<LogGroup> _groups = new();

    public Task<List<LogGroup>> GetAllAsync() => Task.FromResult(_groups.ToList());
    public Task<LogGroup?> GetByIdAsync(string id)
        => Task.FromResult(_groups.FirstOrDefault(g => g.Id == id));
    public Task AddAsync(LogGroup group) { _groups.Add(group); return Task.CompletedTask; }
    public Task ReplaceAllAsync(IReadOnlyList<LogGroup> groups)
    {
        _groups.Clear();
        _groups.AddRange(groups);
        return Task.CompletedTask;
    }
    public Task UpdateAsync(LogGroup group) => Task.CompletedTask;
    public Task DeleteAsync(string id) { _groups.RemoveAll(g => g.Id == id); return Task.CompletedTask; }
    public Task ReorderAsync(List<string> orderedIds) => Task.CompletedTask;
    public Task ExportViewAsync(string exportPath) => Task.CompletedTask;
    public Task<ViewExport?> ImportViewAsync(string importPath) => Task.FromResult<ViewExport?>(null);
}

public class StubSettingsRepository : ISettingsRepository
{
    public AppSettings Settings { get; set; } = new();
    public Task<AppSettings> LoadAsync() => Task.FromResult(Settings);
    public Task SaveAsync(AppSettings settings) { Settings = settings; return Task.CompletedTask; }
}

public class StubSearchService : ISearchService
{
    public Task<SearchResult> SearchFileAsync(string filePath, SearchRequest request, FileEncoding encoding, CancellationToken ct = default)
        => Task.FromResult(new SearchResult { FilePath = filePath });
    public Task<SearchResult> SearchFileRangeAsync(
        string filePath,
        SearchRequest request,
        FileEncoding encoding,
        Func<int, int, FileEncoding, CancellationToken, Task<IReadOnlyList<string>>> readLinesAsync,
        CancellationToken ct = default)
        => Task.FromResult(new SearchResult { FilePath = filePath });
    public Task<IReadOnlyList<SearchResult>> SearchFilesAsync(SearchRequest request, IDictionary<string, FileEncoding> fileEncodings, CancellationToken ct = default, int maxConcurrency = 4)
        => Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());
}

public class StubEncodingDetectionService : IEncodingDetectionService
{
    public FileEncoding AutoDetectedEncoding { get; set; } = FileEncoding.Utf8;
    public string AutoStatusText { get; set; } = "Auto -> UTF-8 (fallback)";

    public FileEncoding DetectFileEncoding(string filePath, FileEncoding fallback = FileEncoding.Utf8)
        => AutoDetectedEncoding == FileEncoding.Auto ? fallback : AutoDetectedEncoding;

    public EncodingHelper.EncodingDecision ResolveEncodingDecision(string filePath, FileEncoding selectedEncoding)
    {
        if (selectedEncoding != FileEncoding.Auto)
            return EncodingHelper.ResolveManualEncodingDecision(selectedEncoding);

        return new EncodingHelper.EncodingDecision(
            FileEncoding.Auto,
            AutoDetectedEncoding,
            AutoStatusText);
    }
}

public sealed class BlockingLogReaderService : ILogReaderService
{
    private readonly string _blockedPath;
    private readonly TaskCompletionSource<bool> _blockedBuildStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public BlockingLogReaderService(string blockedPath)
    {
        _blockedPath = blockedPath;
    }

    public bool BlockedBuildCanceled { get; private set; }

    public Task WaitForBlockedBuildAsync()
        => _blockedBuildStarted.Task;

    public async Task<LineIndex> BuildIndexAsync(string filePath, FileEncoding encoding, CancellationToken ct = default)
    {
        if (string.Equals(filePath, _blockedPath, StringComparison.OrdinalIgnoreCase))
        {
            _blockedBuildStarted.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                BlockedBuildCanceled = true;
                throw;
            }
        }

        return CreateIndex(filePath);
    }

    public Task<LineIndex> UpdateIndexAsync(string filePath, LineIndex existingIndex, FileEncoding encoding, CancellationToken ct = default)
        => Task.FromResult(existingIndex);

    public Task<IReadOnlyList<string>> ReadLinesAsync(string filePath, LineIndex index, int startLine, int count, FileEncoding encoding, CancellationToken ct = default)
    {
        var lines = Enumerable.Range(startLine + 1, Math.Max(0, count))
            .Select(lineNumber => $"Line {lineNumber} content")
            .ToList();
        return Task.FromResult<IReadOnlyList<string>>(lines);
    }

    public Task<string> ReadLineAsync(string filePath, LineIndex index, int lineNumber, FileEncoding encoding, CancellationToken ct = default)
        => Task.FromResult($"Line {lineNumber + 1} content");

    private static LineIndex CreateIndex(string filePath)
    {
        var index = new LineIndex
        {
            FilePath = filePath,
            FileSize = 200
        };

        index.LineOffsets.Add(0);
        index.LineOffsets.Add(100);
        return index;
    }
}

public static class TestHelpers
{
    public static int GetPropertyChangedSubscriberCount(object instance)
    {
        var field = instance.GetType().BaseType?.GetField("PropertyChanged",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (field is null)
            throw new InvalidOperationException("Expected a PropertyChanged backing field on the base type.");

        var handlers = (MulticastDelegate?)field!.GetValue(instance);
        return handlers?.GetInvocationList().Length ?? 0;
    }
}
