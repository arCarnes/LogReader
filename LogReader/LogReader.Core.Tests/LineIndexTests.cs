namespace LogReader.Core.Tests;

using System.Text;
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

    [Fact]
    public async Task BuildIndex_MultiMegabyteFileWithoutNewline_PreservesCompleteLine()
    {
        var content = new string('x', 2 * 1024 * 1024) + "needle";
        var path = await CreateTestFile("large-no-newline.log", content);

        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf8);
        var line = await _reader.ReadLineAsync(path, index, 0, FileEncoding.Utf8);

        Assert.Equal(1, index.LineCount);
        Assert.Equal(content, line);
    }

    [Fact]
    public async Task BuildIndex_PreCancelledLargeFile_LeavesNoTemporaryIndex()
    {
        var path = await CreateTestFile("cancelled-large.log", new string('x', 2 * 1024 * 1024));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _reader.BuildIndexAsync(path, FileEncoding.Utf8, cts.Token));

        Assert.False(Directory.Exists(AppPaths.IndexDirectory) &&
                     Directory.EnumerateFiles(AppPaths.IndexDirectory, "idx_*.bin").Any());
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
    public async Task UpdateIndex_SameSizeReplacement_RebuildsForNewGeneration()
    {
        var path = await CreateTestFile("same-size-replacement.log", "old-a\nold-b\n");
        var retiredPath = Path.Combine(_testDir, "same-size-replacement.old.log");
        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf8);

        File.Move(path, retiredPath);
        await File.WriteAllTextAsync(path, "new-a\nnew-b\n");

        using var updated = await _reader.UpdateIndexAsync(path, index, FileEncoding.Utf8);
        var lines = await _reader.ReadLinesAsync(path, updated, 0, 2, FileEncoding.Utf8);

        Assert.NotSame(index, updated);
        Assert.True(updated.ReplacesPriorGeneration);
        Assert.Equal(new[] { "new-a", "new-b" }, lines);
    }

    [Fact]
    public async Task UpdateIndex_LargerReplacement_RebuildsInsteadOfExtendingOldOffsets()
    {
        var path = await CreateTestFile("larger-replacement.log", "old\n");
        var retiredPath = Path.Combine(_testDir, "larger-replacement.old.log");
        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf8);

        File.Move(path, retiredPath);
        await File.WriteAllTextAsync(path, "replacement first\nreplacement second\n");

        using var updated = await _reader.UpdateIndexAsync(path, index, FileEncoding.Utf8);
        var lines = await _reader.ReadLinesAsync(path, updated, 0, 2, FileEncoding.Utf8);

        Assert.NotSame(index, updated);
        Assert.True(updated.ReplacesPriorGeneration);
        Assert.Equal(new[] { "replacement first", "replacement second" }, lines);
    }

    [Fact]
    public async Task UpdateIndex_KnownIdentityBecomesUnknown_DoesNotRebuildOrMutateOnGrowth()
    {
        var path = await CreateTestFile("known-then-unknown.log", "first\n");
        var knownToken = FileGenerationToken.Create(1, 100);
        var tokenCalls = 0;
        var reader = new ChunkedLogReaderService(
            ChunkedLogReaderService.GetLastWriteTimeUtc,
            _ => Interlocked.Increment(ref tokenCalls) <= 2
                ? knownToken
                : FileGenerationToken.Unknown);
        using var index = await reader.BuildIndexAsync(path, FileEncoding.Utf8);
        var originalSize = index.FileSize;
        var originalLineCount = index.LineCount;
        await File.AppendAllTextAsync(path, "second\n");

        var error = await Assert.ThrowsAsync<IOException>(
            () => reader.UpdateIndexAsync(path, index, FileEncoding.Utf8));

        Assert.Contains("temporarily unavailable", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(knownToken, index.GenerationToken);
        Assert.Equal(originalSize, index.FileSize);
        Assert.Equal(originalLineCount, index.LineCount);
    }

    [Fact]
    public async Task UpdateIndex_UnknownIdentityBecomesKnown_DoesNotPromoteOrRebuild()
    {
        var path = await CreateTestFile("unknown-then-known.log", "first\n");
        var knownToken = FileGenerationToken.Create(1, 101);
        var tokenCalls = 0;
        var reader = new ChunkedLogReaderService(
            ChunkedLogReaderService.GetLastWriteTimeUtc,
            _ => Interlocked.Increment(ref tokenCalls) == 1
                ? FileGenerationToken.Unknown
                : knownToken);
        using var index = await reader.BuildIndexAsync(path, FileEncoding.Utf8);

        var updated = await reader.UpdateIndexAsync(path, index, FileEncoding.Utf8);

        Assert.Same(index, updated);
        Assert.False(updated.GenerationToken.IsKnown);
        Assert.False(updated.ReplacesPriorGeneration);
    }

    [Fact]
    public async Task UpdateIndex_SecondAutomaticReplacementBeforeCooldown_IsBlockedBeforeScan()
    {
        var path = await CreateTestFile("automatic-reload-cooldown.log", "first\n");
        var timestamp = 0L;
        var currentToken = FileGenerationToken.Create(1, 102);
        var reader = new ChunkedLogReaderService(
            ChunkedLogReaderService.GetLastWriteTimeUtc,
            _ => currentToken,
            () => Volatile.Read(ref timestamp));
        using var index = await reader.BuildIndexAsync(path, FileEncoding.Utf8);

        currentToken = FileGenerationToken.Create(1, 103);
        using var firstReplacement = await reader.UpdateIndexAsync(
            path,
            index,
            FileEncoding.Utf8);

        currentToken = FileGenerationToken.Create(1, 104);
        var blocked = await Assert.ThrowsAsync<AutomaticReloadBlockedException>(
            () => reader.UpdateIndexAsync(
                path,
                firstReplacement,
                FileEncoding.Utf8));

        Assert.NotNull(blocked.RetryAfter);
        Assert.True(blocked.RetryAfter > TimeSpan.Zero);
        Assert.Equal(1, firstReplacement.LineCount);
    }

    [Fact]
    public async Task UpdateIndex_ApplicationCooldown_IsSharedAcrossIndexes()
    {
        var path1 = await CreateTestFile("automatic-reload-app-1.log", "first\n");
        var path2 = await CreateTestFile("automatic-reload-app-2.log", "second\n");
        var timestamp = 0L;
        var tokens = new Dictionary<string, FileGenerationToken>(StringComparer.OrdinalIgnoreCase)
        {
            [path1] = FileGenerationToken.Create(1, 105),
            [path2] = FileGenerationToken.Create(1, 106)
        };
        var reader = new ChunkedLogReaderService(
            ChunkedLogReaderService.GetLastWriteTimeUtc,
            stream => tokens[stream.Name],
            () => Volatile.Read(ref timestamp));
        using var index1 = await reader.BuildIndexAsync(path1, FileEncoding.Utf8);
        using var index2 = await reader.BuildIndexAsync(path2, FileEncoding.Utf8);

        tokens[path1] = FileGenerationToken.Create(1, 107);
        using var firstReplacement = await reader.UpdateIndexAsync(
            path1,
            index1,
            FileEncoding.Utf8);
        tokens[path2] = FileGenerationToken.Create(1, 108);

        var blocked = await Assert.ThrowsAsync<AutomaticReloadBlockedException>(
            () => reader.UpdateIndexAsync(path2, index2, FileEncoding.Utf8));

        Assert.NotNull(blocked.RetryAfter);
        Assert.True(blocked.RetryAfter > TimeSpan.Zero);
        index2.ResetAutomaticReloadDelay();
        await Assert.ThrowsAsync<AutomaticReloadBlockedException>(
            () => reader.UpdateIndexAsync(path2, index2, FileEncoding.Utf8));
    }

    [Fact]
    public async Task UpdateIndex_CooldownExpires_AllowsLaterReplacement()
    {
        var path = await CreateTestFile("automatic-reload-cooldown-expiry.log", "first\n");
        var timestamp = 0L;
        var currentToken = FileGenerationToken.Create(1, 109);
        var reader = new ChunkedLogReaderService(
            ChunkedLogReaderService.GetLastWriteTimeUtc,
            _ => currentToken,
            () => Volatile.Read(ref timestamp));
        using var index = await reader.BuildIndexAsync(path, FileEncoding.Utf8);

        currentToken = FileGenerationToken.Create(1, 110);
        using var firstReplacement = await reader.UpdateIndexAsync(
            path,
            index,
            FileEncoding.Utf8);
        Volatile.Write(
            ref timestamp,
            (long)Math.Ceiling(
                AutomaticReloadAdmission.MinimumCooldown.TotalSeconds *
                System.Diagnostics.Stopwatch.Frequency) + 1);
        currentToken = FileGenerationToken.Create(1, 111);

        using var secondReplacement = await reader.UpdateIndexAsync(
            path,
            firstReplacement,
            FileEncoding.Utf8);

        Assert.True(secondReplacement.ReplacesPriorGeneration);
        Assert.Equal(currentToken, secondReplacement.GenerationToken);
    }

    [Fact]
    public void AutomaticReloadAdmission_UsesMinimumAndByteProportionalCooldowns()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            AutomaticReloadAdmission.CalculateCooldown(
                AutomaticReloadAdmission.MinimumChargeBytes,
                AutomaticReloadAdmission.PerFileBytesPerSecond));
        Assert.Equal(
            TimeSpan.FromSeconds(64),
            AutomaticReloadAdmission.CalculateCooldown(
                64L * 1024 * 1024,
                AutomaticReloadAdmission.PerFileBytesPerSecond));
        Assert.Equal(
            TimeSpan.FromSeconds(32),
            AutomaticReloadAdmission.CalculateCooldown(
                64L * 1024 * 1024,
                AutomaticReloadAdmission.ApplicationBytesPerSecond));
    }

    [Fact]
    public async Task ReadLines_KnownReplacement_DoesNotUseOldOffsetsAgainstNewFile()
    {
        var path = await CreateTestFile("read-replacement.log", "old first\nold second\n");
        var retiredPath = Path.Combine(_testDir, "read-replacement.old.log");
        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf8);

        File.Move(path, retiredPath);
        await File.WriteAllTextAsync(path, "new first with a different width\nnew second\n");

        await Assert.ThrowsAsync<IOException>(
            () => _reader.ReadLinesAsync(path, index, 0, 2, FileEncoding.Utf8));
    }

    [Fact]
    public async Task UpdateIndex_AppendValidationFailure_RollsBackAddedOffsets()
    {
        var path = await CreateTestFile("append-validation-failure.log", "first\n");
        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf8);
        var originalFileSize = index.FileSize;
        var originalLineCount = index.LineCount;
        await File.AppendAllTextAsync(path, "second\nthird\n");
        var generationCalls = 0;
        var reader = new ChunkedLogReaderService(
            ChunkedLogReaderService.GetLastWriteTimeUtc,
            _ => Interlocked.Increment(ref generationCalls) == 1
                ? index.GenerationToken
                : throw new OperationCanceledException("Generation validation canceled."));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => reader.UpdateIndexAsync(path, index, FileEncoding.Utf8));

        Assert.Equal(originalFileSize, index.FileSize);
        Assert.Equal(originalLineCount, index.LineCount);
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

    [Fact]
    public async Task BuildIndex_FileGrowsAfterSnapshot_StopsAtCapturedLength()
    {
        const string initialContent = "initial\n";
        var path = await CreateTestFile("build-moving-eof.log", initialContent);
        var appended = 0;
        var reader = new ChunkedLogReaderService(
            ChunkedLogReaderService.GetLastWriteTimeUtc,
            _ =>
            {
                if (Interlocked.Exchange(ref appended, 1) == 0)
                    File.AppendAllText(path, "late\n");

                return FileGenerationToken.Unknown;
            });

        using var index = await reader.BuildIndexAsync(path, FileEncoding.Utf8);

        Assert.Equal(Encoding.UTF8.GetByteCount(initialContent), index.FileSize);
        Assert.Equal(1, index.LineCount);
        Assert.Equal("initial", await reader.ReadLineAsync(path, index, 0, FileEncoding.Utf8));
    }

    [Fact]
    public async Task UpdateIndex_FileGrowsAfterSnapshot_DefersLaterBytesUntilNextUpdate()
    {
        var path = await CreateTestFile("update-moving-eof.log", "initial\n");
        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf8);
        await File.AppendAllTextAsync(path, "captured\n");
        var capturedLength = new FileInfo(path).Length;
        var appended = 0;
        var reader = new ChunkedLogReaderService(
            ChunkedLogReaderService.GetLastWriteTimeUtc,
            _ =>
            {
                if (Interlocked.Exchange(ref appended, 1) == 0)
                    File.AppendAllText(path, "deferred\n");

                return index.GenerationToken;
            });

        var firstUpdate = await reader.UpdateIndexAsync(path, index, FileEncoding.Utf8);

        Assert.Same(index, firstUpdate);
        Assert.Equal(capturedLength, firstUpdate.FileSize);
        Assert.Equal(2, firstUpdate.LineCount);

        var secondUpdate = await reader.UpdateIndexAsync(path, firstUpdate, FileEncoding.Utf8);

        Assert.Same(firstUpdate, secondUpdate);
        Assert.Equal(new[] { "initial", "captured", "deferred" },
            await reader.ReadLinesAsync(path, secondUpdate, 0, 3, FileEncoding.Utf8));
    }

    private static Exception CreateException(Type exceptionType)
        => exceptionType == typeof(ObjectDisposedException)
            ? new ObjectDisposedException("metadata")
            : (Exception)Activator.CreateInstance(exceptionType, "Metadata unavailable.")!;
}
