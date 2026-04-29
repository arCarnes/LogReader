namespace LogReader.Core.Tests;

using LogReader.Core;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

public class LineIndexTests : IAsyncLifetime
{
    private readonly ChunkedLogReaderService _reader = new();
    private string _testDir = null!;
    private IDisposable? _appPathsScope;

    public Task InitializeAsync()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "LogReaderTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_testDir);
        _appPathsScope = AppPaths.BeginTestScope(rootPath: _testDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _appPathsScope?.Dispose();
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, true);
        return Task.CompletedTask;
    }

    private async Task<string> CreateTestFile(string name, string content)
    {
        var path = Path.Combine(_testDir, name);
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    [Fact]
    public async Task BuildIndex_CountsLinesCorrectly()
    {
        var path = await CreateTestFile("test.log", "Line 1\nLine 2\nLine 3\n");

        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf8);

        Assert.Equal(3, index.LineCount);
    }

    [Fact]
    public async Task BuildIndex_NoTrailingNewline()
    {
        var path = await CreateTestFile("test.log", "Line 1\nLine 2\nLine 3");

        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf8);

        Assert.Equal(3, index.LineCount);
    }

    [Fact]
    public async Task BuildIndex_EmptyFile()
    {
        var path = await CreateTestFile("test.log", "");

        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf8);

        Assert.Equal(0, index.LineCount);
        var lines = await _reader.ReadLinesAsync(path, index, 0, 1, FileEncoding.Utf8);
        Assert.Empty(lines);
    }

    [Fact]
    public async Task BuildIndex_SingleLine()
    {
        var path = await CreateTestFile("test.log", "Hello World");

        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf8);

        Assert.Equal(1, index.LineCount);
    }

    [Fact]
    public async Task ReadLines_ReadsCorrectContent()
    {
        var path = await CreateTestFile("test.log", "Line 1\nLine 2\nLine 3\n");

        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf8);
        var lines = await _reader.ReadLinesAsync(path, index, 0, 3, FileEncoding.Utf8);

        Assert.Equal(3, lines.Count);
        Assert.Equal("Line 1", lines[0]);
        Assert.Equal("Line 2", lines[1]);
        Assert.Equal("Line 3", lines[2]);
    }

    [Fact]
    public async Task ReadLines_SubsetOfLines()
    {
        var path = await CreateTestFile("test.log", "Line 1\nLine 2\nLine 3\nLine 4\nLine 5\n");

        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf8);
        var lines = await _reader.ReadLinesAsync(path, index, 1, 2, FileEncoding.Utf8);

        Assert.Equal(2, lines.Count);
        Assert.Equal("Line 2", lines[0]);
        Assert.Equal("Line 3", lines[1]);
    }

    [Fact]
    public async Task ReadLines_WindowsLineEndings()
    {
        var path = await CreateTestFile("test.log", "Line 1\r\nLine 2\r\nLine 3\r\n");

        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf8);
        var lines = await _reader.ReadLinesAsync(path, index, 0, 3, FileEncoding.Utf8);

        Assert.Equal(3, lines.Count);
        Assert.Equal("Line 1", lines[0]);
        Assert.Equal("Line 2", lines[1]);
    }

    [Fact]
    public async Task ReadLines_CarriageReturnOnlyLineEndings()
    {
        var path = await CreateTestFile("cr-only.log", "Line 1\rLine 2\rLine 3\r");

        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf8);
        var lines = await _reader.ReadLinesAsync(path, index, 0, 3, FileEncoding.Utf8);

        Assert.Equal(3, index.LineCount);
        Assert.Equal(new[] { "Line 1", "Line 2", "Line 3" }, lines);
    }

    [Fact]
    public async Task ReadLines_MixedLineEndings_TreatsCrLfAsSingleBoundary()
    {
        var path = await CreateTestFile("mixed.log", "Line 1\r\nLine 2\rLine 3\nLine 4");

        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf8);
        var lines = await _reader.ReadLinesAsync(path, index, 0, 4, FileEncoding.Utf8);

        Assert.Equal(4, index.LineCount);
        Assert.Equal(new[] { "Line 1", "Line 2", "Line 3", "Line 4" }, lines);
    }

    [Fact]
    public async Task ReadLine_SingleLine()
    {
        var path = await CreateTestFile("test.log", "First\nSecond\nThird\n");

        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf8);
        var line = await _reader.ReadLineAsync(path, index, 1, FileEncoding.Utf8);

        Assert.Equal("Second", line);
    }

    [Fact]
    public async Task UpdateIndex_AppendsNewLines()
    {
        var path = await CreateTestFile("test.log", "Line 1\nLine 2\n");
        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf8);
        Assert.Equal(2, index.LineCount);

        // Append more content
        await File.AppendAllTextAsync(path, "Line 3\nLine 4\n");

        // UpdateIndex mutates and returns the same object
        var updated = await _reader.UpdateIndexAsync(path, index, FileEncoding.Utf8);

        Assert.Equal(4, updated.LineCount);

        var lines = await _reader.ReadLinesAsync(path, updated, 2, 2, FileEncoding.Utf8);
        Assert.Equal("Line 3", lines[0]);
        Assert.Equal("Line 4", lines[1]);
    }

    [Fact]
    public async Task UpdateIndex_EmptyFile_AppendsLineWithoutNewline()
    {
        var path = await CreateTestFile("test.log", "");
        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf8);
        Assert.Equal(0, index.LineCount);

        await File.AppendAllTextAsync(path, "First line");

        var updated = await _reader.UpdateIndexAsync(path, index, FileEncoding.Utf8);
        Assert.Equal(1, updated.LineCount);
        var line = await _reader.ReadLineAsync(path, updated, 0, FileEncoding.Utf8);
        Assert.Equal("First line", line);
    }

    [Fact]
    public async Task UpdateIndex_SplitCrLfAcrossAppendBoundary_TreatsAsSingleLineEnding()
    {
        var path = await CreateTestFile("split-crlf.log", "Line 1\r");
        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf8);

        await File.AppendAllTextAsync(path, "\nLine 2\r\n");

        var updated = await _reader.UpdateIndexAsync(path, index, FileEncoding.Utf8);

        Assert.Equal(2, updated.LineCount);
        var lines = await _reader.ReadLinesAsync(path, updated, 0, 2, FileEncoding.Utf8);
        Assert.Equal(new[] { "Line 1", "Line 2" }, lines);
    }

    [Fact]
    public async Task UpdateIndex_BareCrAcrossAppendBoundary_RemainsLineEnding()
    {
        var path = await CreateTestFile("split-cr-only.log", "Line 1\r");
        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf8);

        await File.AppendAllTextAsync(path, "Line 2\r");

        var updated = await _reader.UpdateIndexAsync(path, index, FileEncoding.Utf8);

        Assert.Equal(2, updated.LineCount);
        var lines = await _reader.ReadLinesAsync(path, updated, 0, 2, FileEncoding.Utf8);
        Assert.Equal(new[] { "Line 1", "Line 2" }, lines);
    }

    [Fact]
    public async Task UpdateIndex_DetectsTruncation()
    {
        var path = await CreateTestFile("test.log", "Line 1\nLine 2\nLine 3\n");
        var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf8);

        // Truncate file (simulate rotation)
        await File.WriteAllTextAsync(path, "New Line 1\n");

        // UpdateIndex disposes the old index and returns a new one
        using var updated = await _reader.UpdateIndexAsync(path, index, FileEncoding.Utf8);

        Assert.Equal(1, updated.LineCount);
        var lines = await _reader.ReadLinesAsync(path, updated, 0, 1, FileEncoding.Utf8);
        Assert.Equal("New Line 1", lines[0]);
    }

    [Fact]
    public async Task UpdateIndex_DetectsSameSizeRewrite()
    {
        var path = await CreateTestFile("rewrite-same-size.log", "Old 1\nOld 2\n");
        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf8);

        using var updated = await _reader.UpdateIndexAsync(path, index, FileEncoding.Utf8);
        await File.WriteAllTextAsync(path, "New 1\nNew 2\n");

        using var rewritten = await _reader.UpdateIndexAsync(path, updated, FileEncoding.Utf8);

        Assert.NotSame(updated, rewritten);
        Assert.Equal(2, rewritten.LineCount);
        var lines = await _reader.ReadLinesAsync(path, rewritten, 0, 2, FileEncoding.Utf8);
        Assert.Equal(new[] { "New 1", "New 2" }, lines);
    }

    [Fact]
    public async Task ReadLines_OutOfRange_ReturnsEmpty()
    {
        var path = await CreateTestFile("test.log", "Line 1\n");
        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf8);

        var lines = await _reader.ReadLinesAsync(path, index, 100, 10, FileEncoding.Utf8);

        Assert.Empty(lines);
    }

    [Fact]
    public async Task BuildIndex_LargeFile()
    {
        var content = string.Join("\n", Enumerable.Range(0, 10000).Select(i => $"Log line {i}: Some content here"));
        var path = await CreateTestFile("large.log", content);

        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf8);

        Assert.Equal(10000, index.LineCount);

        // Spot check some lines
        var line0 = await _reader.ReadLineAsync(path, index, 0, FileEncoding.Utf8);
        Assert.Equal("Log line 0: Some content here", line0);

        var line9999 = await _reader.ReadLineAsync(path, index, 9999, FileEncoding.Utf8);
        Assert.Equal("Log line 9999: Some content here", line9999);
    }
}
