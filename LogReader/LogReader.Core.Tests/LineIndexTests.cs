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
        _testDir = Path.Combine(Path.GetTempPath(), "WeezTailTests_" + Guid.NewGuid().ToString("N")[..8]);
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
        Assert.Equal(File.GetLastWriteTimeUtc(path), index.LastWriteTimeUtc);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(NotSupportedException))]
    public async Task BuildIndex_MetadataUnavailable_BuildsReadableIndexWithUnknownTimestamp(Type exceptionType)
    {
        var path = await CreateTestFile("metadata-unavailable.log", "Line 1\nLine 2\n");
        var reader = new ChunkedLogReaderService(_ => throw CreateException(exceptionType));

        using var index = await reader.BuildIndexAsync(path, FileEncoding.Utf8);
        var lines = await reader.ReadLinesAsync(path, index, 0, 2, FileEncoding.Utf8);

        Assert.Equal(2, index.LineCount);
        Assert.Equal(default, index.LastWriteTimeUtc);
        Assert.Equal(new[] { "Line 1", "Line 2" }, lines);
    }

    [Fact]
    public async Task BuildIndex_FinalMetadataUnavailable_BuildsReadableIndexWithUnknownTimestamp()
    {
        var path = await CreateTestFile("final-metadata-unavailable.log", "Line 1\nLine 2\n");
        var metadataCalls = 0;
        var reader = new ChunkedLogReaderService(stream =>
        {
            if (++metadataCalls == 2)
                throw new IOException("Metadata unavailable.");

            return ChunkedLogReaderService.GetLastWriteTimeUtc(stream);
        });

        using var index = await reader.BuildIndexAsync(path, FileEncoding.Utf8);
        var lines = await reader.ReadLinesAsync(path, index, 0, 2, FileEncoding.Utf8);

        Assert.Equal(2, metadataCalls);
        Assert.Equal(2, index.LineCount);
        Assert.Equal(default, index.LastWriteTimeUtc);
        Assert.Equal(new[] { "Line 1", "Line 2" }, lines);
    }

    [Theory]
    [InlineData(typeof(OperationCanceledException))]
    [InlineData(typeof(ObjectDisposedException))]
    public async Task BuildIndex_NonMetadataAvailabilityFailure_Propagates(Type exceptionType)
    {
        var path = await CreateTestFile("metadata-propagates.log", "Line 1\n");
        var reader = new ChunkedLogReaderService(_ => throw CreateException(exceptionType));

        await Assert.ThrowsAsync(exceptionType, () => reader.BuildIndexAsync(path, FileEncoding.Utf8));
    }

    [Fact]
    public async Task IndexTimestamp_ReadsScannedHandleAfterPathIsReplaced()
    {
        var path = await CreateTestFile("handle-timestamp.log", "original");
        var movedPath = Path.Combine(_testDir, "handle-timestamp.old.log");
        var originalTimestamp = new DateTime(2026, 1, 1, 1, 2, 3, DateTimeKind.Utc);
        var replacementTimestamp = originalTimestamp.AddHours(1);
        File.SetLastWriteTimeUtc(path, originalTimestamp);

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        File.Move(path, movedPath);
        await File.WriteAllTextAsync(path, "replaced");
        File.SetLastWriteTimeUtc(path, replacementTimestamp);

        var handleTimestamp = ChunkedLogReaderService.GetLastWriteTimeUtc(stream);

        Assert.Equal(File.GetLastWriteTimeUtc(movedPath), handleTimestamp);
        Assert.NotEqual(File.GetLastWriteTimeUtc(path), handleTimestamp);
    }

    [Fact]
    public void ResolveStableSnapshotTimestamp_ChangedMetadata_MarksSnapshotUnstable()
    {
        var initialTimestamp = new DateTime(2026, 1, 1, 1, 2, 3, DateTimeKind.Utc);

        Assert.Equal(
            initialTimestamp,
            ChunkedLogReaderService.ResolveStableSnapshotTimestamp(initialTimestamp, initialTimestamp));
        Assert.Equal(
            default,
            ChunkedLogReaderService.ResolveStableSnapshotTimestamp(initialTimestamp, initialTimestamp.AddTicks(1)));
        Assert.Equal(
            default,
            ChunkedLogReaderService.ResolveStableSnapshotTimestamp(default, default));
    }

    [Fact]
    public async Task UpdateIndex_RewrittenPrefixFollowedByAppend_MarksSnapshotTimestampUnstable()
    {
        var path = await CreateTestFile("rewrite-append.log", "first\nsecond\n");
        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf8);
        var replacementTimestamp = index.LastWriteTimeUtc.AddSeconds(1);
        await File.WriteAllTextAsync(path, "other\nvalue!\nappended\n");
        File.SetLastWriteTimeUtc(path, replacementTimestamp);

        var updated = await _reader.UpdateIndexAsync(path, index, FileEncoding.Utf8);

        Assert.Same(index, updated);
        Assert.Equal(default, updated.LastWriteTimeUtc);
    }

    [Fact]
    public async Task UpdateIndex_InitialMetadataUnavailable_AppendsAndClearsTimestampEvidence()
    {
        var path = await CreateTestFile("append-initial-metadata-unavailable.log", "Line 1\n");
        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf8);
        await File.AppendAllTextAsync(path, "Line 2\n");
        var reader = new ChunkedLogReaderService(_ => throw new IOException("Metadata unavailable."));

        var updated = await reader.UpdateIndexAsync(path, index, FileEncoding.Utf8);
        var lines = await reader.ReadLinesAsync(path, updated, 0, 2, FileEncoding.Utf8);

        Assert.Same(index, updated);
        Assert.Equal(2, updated.LineCount);
        Assert.Equal(default, updated.LastWriteTimeUtc);
        Assert.Equal(new[] { "Line 1", "Line 2" }, lines);
    }

    [Fact]
    public async Task UpdateIndex_FinalMetadataUnavailable_AppendsAndClearsTimestampEvidence()
    {
        var path = await CreateTestFile("append-final-metadata-unavailable.log", "Line 1\n");
        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf8);
        await File.AppendAllTextAsync(path, "Line 2\n");
        var metadataCalls = 0;
        var reader = new ChunkedLogReaderService(_ =>
        {
            if (++metadataCalls == 2)
                throw new IOException("Metadata unavailable.");

            return index.LastWriteTimeUtc;
        });

        var updated = await reader.UpdateIndexAsync(path, index, FileEncoding.Utf8);
        var lines = await reader.ReadLinesAsync(path, updated, 0, 2, FileEncoding.Utf8);

        Assert.Equal(2, metadataCalls);
        Assert.Same(index, updated);
        Assert.Equal(2, updated.LineCount);
        Assert.Equal(default, updated.LastWriteTimeUtc);
        Assert.Equal(new[] { "Line 1", "Line 2" }, lines);
    }

    [Fact]
    public async Task UpdateIndex_UnchangedFileMetadataUnavailable_ClearsTimestampEvidence()
    {
        var path = await CreateTestFile("unchanged-metadata-unavailable.log", "Line 1\n");
        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf8);
        Assert.NotEqual(default, index.LastWriteTimeUtc);
        var reader = new ChunkedLogReaderService(_ => throw new IOException("Metadata unavailable."));

        var updated = await reader.UpdateIndexAsync(path, index, FileEncoding.Utf8);

        Assert.Same(index, updated);
        Assert.Equal(1, updated.LineCount);
        Assert.Equal(default, updated.LastWriteTimeUtc);
    }

    [Fact]
    public async Task UpdateIndex_TruncatedFileMetadataUnavailable_RebuildsReadableIndex()
    {
        var path = await CreateTestFile("truncated-metadata-unavailable.log", "Old 1\nOld 2\nOld 3\n");
        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf8);
        await File.WriteAllTextAsync(path, "New 1\n");
        var reader = new ChunkedLogReaderService(_ => throw new IOException("Metadata unavailable."));

        using var updated = await reader.UpdateIndexAsync(path, index, FileEncoding.Utf8);
        var lines = await reader.ReadLinesAsync(path, updated, 0, 1, FileEncoding.Utf8);

        Assert.NotSame(index, updated);
        Assert.Equal(1, updated.LineCount);
        Assert.Equal(default, updated.LastWriteTimeUtc);
        Assert.Equal(new[] { "New 1" }, lines);
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
    public async Task ReadLines_PreservesBlankLines()
    {
        var path = await CreateTestFile("blank-lines.log", "Line 1\n\nLine 3\n");

        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf8);
        var lines = await _reader.ReadLinesAsync(path, index, 0, 3, FileEncoding.Utf8);

        Assert.Equal(3, lines.Count);
        Assert.Equal("Line 1", lines[0]);
        Assert.Equal(string.Empty, lines[1]);
        Assert.Equal("Line 3", lines[2]);
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
        var originalFileSize = index.FileSize;

        // Append more content
        await File.AppendAllTextAsync(path, "Line 3\nLine 4\n");

        // UpdateIndex mutates and returns the same object
        var updated = await _reader.UpdateIndexAsync(path, index, FileEncoding.Utf8);

        Assert.Same(index, updated);
        Assert.Equal(4, updated.LineCount);
        Assert.True(updated.FileSize > originalFileSize);

        var lines = await _reader.ReadLinesAsync(path, updated, 2, 2, FileEncoding.Utf8);
        Assert.Equal("Line 3", lines[0]);
        Assert.Equal("Line 4", lines[1]);
    }

    [Fact]
    public async Task UpdateIndex_AfterAppendThenNoChange_ReturnsSameIndex()
    {
        var path = await CreateTestFile("append-no-change.log", "Line 1\nLine 2\n");
        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf8);

        await File.AppendAllTextAsync(path, "Line 3\nLine 4\n");

        var appended = await _reader.UpdateIndexAsync(path, index, FileEncoding.Utf8);
        var unchanged = await _reader.UpdateIndexAsync(path, appended, FileEncoding.Utf8);

        Assert.Same(index, appended);
        Assert.Same(appended, unchanged);
        Assert.Equal(4, unchanged.LineCount);
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
        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf8);

        // Truncate file (simulate rotation)
        await File.WriteAllTextAsync(path, "New Line 1\n");

        using var updated = await _reader.UpdateIndexAsync(path, index, FileEncoding.Utf8);

        Assert.NotSame(index, updated);
        Assert.Equal(1, updated.LineCount);
        var lines = await _reader.ReadLinesAsync(path, updated, 0, 1, FileEncoding.Utf8);
        Assert.Equal("New Line 1", lines[0]);
    }

    [Fact]
    public async Task UpdateIndex_Truncation_DoesNotDisposeExistingIndex()
    {
        var path = await CreateTestFile("truncate-keeps-old-index.log", "Line 1\nLine 2\nLine 3\n");
        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf8);

        await File.WriteAllTextAsync(path, "New Line 1\n");

        using var updated = await _reader.UpdateIndexAsync(path, index, FileEncoding.Utf8);

        Assert.NotSame(index, updated);
        index.LineOffsets.Add(index.FileSize);
    }

    [Fact]
    public async Task UpdateIndex_SameSizeRewrite_ReturnsExistingIndex()
    {
        var path = Path.Combine(_testDir, "rewrite-same-size.log");
        var originalLines = Enumerable.Range(1, 2_000)
            .Select(i => i == 1_000 ? "line-1000-aaaaaaaa" : $"line-{i:D4}-aaaaaaaa")
            .ToArray();
        await File.WriteAllTextAsync(path, string.Join("\n", originalLines) + "\n");

        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf8);

        var rewrittenLines = originalLines.ToArray();
        rewrittenLines[999] = "line-1000-bbbbbbbb";
        await File.WriteAllTextAsync(path, string.Join("\n", rewrittenLines) + "\n");

        using var rewritten = await _reader.UpdateIndexAsync(path, index, FileEncoding.Utf8);

        Assert.Same(index, rewritten);
        Assert.Equal(originalLines.Length, rewritten.LineCount);
        var lines = await _reader.ReadLinesAsync(path, rewritten, 998, 3, FileEncoding.Utf8);
        Assert.Equal(new[]
        {
            "line-0999-aaaaaaaa",
            "line-1000-bbbbbbbb",
            "line-1001-aaaaaaaa"
        }, lines);
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

    private static Exception CreateException(Type exceptionType)
        => exceptionType == typeof(ObjectDisposedException)
            ? new ObjectDisposedException("metadata")
            : (Exception)Activator.CreateInstance(exceptionType, "Metadata unavailable.")!;
}
