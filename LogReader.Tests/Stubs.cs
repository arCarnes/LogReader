namespace LogReader.Tests;

using LogReader.Core.Interfaces;
using LogReader.Core.Models;

/// <summary>
/// Shared test stubs used across NavigationTests and MainViewModelTests.
/// </summary>
internal class StubLogReaderService : ILogReaderService
{
    private readonly int _lineCount;
    public int BuildIndexCallCount { get; private set; }
    public int ReadLinesCallCount { get; private set; }

    public StubLogReaderService(int lineCount = 200) => _lineCount = lineCount;

    public Task<LineIndex> BuildIndexAsync(string filePath, FileEncoding encoding, CancellationToken ct = default)
    {
        BuildIndexCallCount++;
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
        => Task.FromResult(existingIndex);

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
#pragma warning restore CS0067
    public int StartCallCount { get; private set; }
    public int StopCallCount { get; private set; }
    public HashSet<string> ActiveFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> StartedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> StoppedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void StartTailing(string filePath, FileEncoding encoding)
    {
        StartCallCount++;
        ActiveFiles.Add(filePath);
        StartedFiles.Add(filePath);
    }

    public void StopTailing(string filePath)
    {
        StopCallCount++;
        ActiveFiles.Remove(filePath);
        StoppedFiles.Add(filePath);
    }

    public void StopAll()
    {
        var files = ActiveFiles.ToList();
        foreach (var file in files)
            StopTailing(file);
    }

    public void Dispose() { }
}
