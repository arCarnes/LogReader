namespace LogReader.Core.Tests;

using System.Diagnostics;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;
using Xunit.Abstractions;

public sealed class BoundedLogReaderServiceTests : IAsyncLifetime
{
    private readonly ChunkedLogReaderService _reader = new();
    private readonly ITestOutputHelper _output;
    private string _testDirectory = null!;
    private IDisposable? _appPathsScope;

    public BoundedLogReaderServiceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public Task InitializeAsync()
    {
        _testDirectory = Path.Combine(
            Path.GetTempPath(),
            $"WeezTailBoundedReader_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _appPathsScope = AppPaths.BeginTestScope(rootPath: _testDirectory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _appPathsScope?.Dispose();
        if (Directory.Exists(_testDirectory))
            Directory.Delete(_testDirectory, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task BuildBoundedIndex_RejectsExcessOffsetsAndCleansMapping()
    {
        var path = await CreateFileAsync("too-many-lines.log", "one\ntwo\nthree\nfour\n");

        var error = await Assert.ThrowsAsync<LineIndexCapacityExceededException>(
            () => _reader.BuildBoundedIndexAsync(
                path,
                FileEncoding.Utf8,
                maximumLineCount: 3));

        Assert.Equal(3, error.MaximumLineCount);
        Assert.False(Directory.Exists(AppPaths.IndexDirectory) &&
                     Directory.EnumerateFiles(
                         AppPaths.IndexDirectory,
                         "idx_*.bin",
                         SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task BuildBoundedIndex_AllowsExactLineLimitWithTrailingNewline()
    {
        var path = await CreateFileAsync("exact-lines.log", "one\ntwo\nthree\n");

        using var index = await _reader.BuildBoundedIndexAsync(
            path,
            FileEncoding.Utf8,
            maximumLineCount: 3);

        Assert.Equal(3, index.LineCount);
    }

    [Fact]
    public async Task UpdateBoundedIndex_RollsBackOffsetsWhenCapacityIsExceeded()
    {
        var path = await CreateFileAsync("growing.log", "one\ntwo");
        using var index = await _reader.BuildBoundedIndexAsync(
            path,
            FileEncoding.Utf8,
            maximumLineCount: 3);
        await File.AppendAllTextAsync(path, "\nthree\nfour");

        await Assert.ThrowsAsync<LineIndexCapacityExceededException>(
            () => _reader.UpdateBoundedIndexAsync(
                path,
                index,
                FileEncoding.Utf8,
                maximumLineCount: 3));

        Assert.Equal(2, index.LineCount);
        Assert.Equal(new[] { "one", "two" }, await _reader.ReadLinesAsync(
            path,
            index,
            startLine: 0,
            count: 2,
            FileEncoding.Utf8));
    }

    [Fact]
    public async Task UpdateBoundedIndex_AllowsExactLineLimitWithTrailingNewline()
    {
        var path = await CreateFileAsync("growing-to-limit.log", "one\ntwo");
        using var index = await _reader.BuildBoundedIndexAsync(
            path,
            FileEncoding.Utf8,
            maximumLineCount: 3);
        await File.AppendAllTextAsync(path, "\nthree\n");

        var updated = await _reader.UpdateBoundedIndexAsync(
            path,
            index,
            FileEncoding.Utf8,
            maximumLineCount: 3);

        Assert.Same(index, updated);
        Assert.Equal(3, index.LineCount);
    }

    [Fact]
    public async Task ReadBoundedLines_LimitsIndividualAndAggregateText()
    {
        var path = await CreateFileAsync(
            "large-lines.log",
            $"{new string('a', 10_000)}\nsecond-line\nthird-line");
        using var index = await _reader.BuildBoundedIndexAsync(
            path,
            FileEncoding.Utf8,
            maximumLineCount: 10);

        var lines = await _reader.ReadBoundedLinesAsync(
            path,
            index,
            startLine: 0,
            count: 3,
            FileEncoding.Utf8,
            maximumCharactersPerLine: 8,
            maximumTotalCharacters: 12);

        Assert.Equal(2, lines.Count);
        Assert.Equal(new BoundedIndexedLine(0, "aaaaaaaa", IsTruncated: true), lines[0]);
        Assert.Equal(new BoundedIndexedLine(1, "seco", IsTruncated: true), lines[1]);
    }

    [Fact]
    public async Task ReadBoundedLines_LargeRequestedCountDoesNotOverflowRange()
    {
        var path = await CreateFileAsync("large-count.log", "one");
        using var index = await _reader.BuildBoundedIndexAsync(
            path,
            FileEncoding.Utf8,
            maximumLineCount: 1);

        var lines = await _reader.ReadBoundedLinesAsync(
            path,
            index,
            startLine: 0,
            count: int.MaxValue,
            FileEncoding.Utf8,
            maximumCharactersPerLine: 10,
            maximumTotalCharacters: 10);

        Assert.Equal("one", Assert.Single(lines).Text);
    }

    [Theory]
    [InlineData(FileEncoding.Utf8)]
    [InlineData(FileEncoding.Utf16)]
    public async Task ReadFullIndexedLines_SplitsAtBatchByteLimitWithoutSkippingLines(FileEncoding encoding)
    {
        var textEncoding = encoding == FileEncoding.Utf16
            ? System.Text.Encoding.Unicode
            : new System.Text.UTF8Encoding(false);
        var lineLength = encoding == FileEncoding.Utf16 ? 2_500_000 : 5_000_000;
        var path = Path.Combine(_testDirectory, $"full-batch-{encoding}.log");
        await File.WriteAllTextAsync(path,
            $"{new string('a', lineLength)}\n{new string('b', lineLength)}\nlast", textEncoding);
        using var index = await _reader.BuildBoundedIndexAsync(path, encoding, maximumLineCount: 3);
        var snapshot = IndexedLogReadSnapshot.Capture(index, encoding, [new IndexedLogReadRange(0, 3)]);

        var first = await _reader.ReadFullIndexedLinesAsync(path, snapshot, startLine: 0, count: 3);
        var second = await _reader.ReadFullIndexedLinesAsync(path, snapshot, startLine: first.Count, count: 3 - first.Count);

        Assert.Single(first);
        Assert.Equal(0, first[0].LineNumber);
        Assert.Equal(lineLength, first[0].Text.Length);
        Assert.Equal([1, 2], second.Select(static line => line.LineNumber));
        Assert.Equal('b', second[0].Text[0]);
        Assert.Equal("last", second[1].Text);
    }

    [Fact]
    public async Task ReadFullIndexedLines_AcceptsExactLineByteLimitAndRejectsLargerLine()
    {
        var limit = ChunkedLogReaderService.MaximumFilteredTailLineBytes;
        var path = Path.Combine(_testDirectory, "full-line-limit.log");
        await File.WriteAllTextAsync(path, new string('a', limit - 1) + "\nlast", new System.Text.UTF8Encoding(false));
        using var index = await _reader.BuildBoundedIndexAsync(path, FileEncoding.Utf8, maximumLineCount: 2);
        var snapshot = IndexedLogReadSnapshot.Capture(index, FileEncoding.Utf8, [new IndexedLogReadRange(0, 2)]);

        var accepted = await _reader.ReadFullIndexedLinesAsync(path, snapshot, startLine: 0, count: 2);
        Assert.Single(accepted);
        Assert.Equal(limit - 1, accepted[0].Text.Length);

        await File.WriteAllTextAsync(path, new string('a', limit + 1), new System.Text.UTF8Encoding(false));
        using var largerIndex = await _reader.BuildBoundedIndexAsync(path, FileEncoding.Utf8, maximumLineCount: 2);
        var largerSnapshot = IndexedLogReadSnapshot.Capture(
            largerIndex, FileEncoding.Utf8, [new IndexedLogReadRange(0, 1)]);
        await Assert.ThrowsAsync<FilteredLogLineTooLargeException>(
            () => _reader.ReadFullIndexedLinesAsync(path, largerSnapshot, startLine: 0, count: 1));
    }

    [Fact]
    public async Task ExistingUnboundedEntryPoint_RetainsInteractiveBehavior()
    {
        var path = await CreateFileAsync("interactive.log", "one\ntwo\nthree\nfour");

        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf8);

        Assert.Equal(4, index.LineCount);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task BuildBoundedIndex_OffsetStorageScalesAtEightBytesPerLine()
    {
        const int lineCount = 250_000;
        var contents = string.Create(lineCount * 2 - 1, lineCount, static (span, count) =>
        {
            for (var index = 0; index < count; index++)
            {
                span[index * 2] = 'x';
                if (index + 1 < count)
                    span[index * 2 + 1] = '\n';
            }
        });
        var path = await CreateFileAsync("measured-offsets.log", contents);
        var stopwatch = Stopwatch.StartNew();

        using var index = await _reader.BuildBoundedIndexAsync(
            path,
            FileEncoding.Utf8,
            maximumLineCount: lineCount);
        stopwatch.Stop();

        var mapping = Assert.Single(Directory.GetFiles(
            AppPaths.IndexDirectory,
            "idx_*.bin",
            SearchOption.AllDirectories));
        var mappedBytes = new FileInfo(mapping).Length;
        _output.WriteLine(
            "Indexed {0:N0} lines into {1:N0} mapped bytes in {2:N0} ms.",
            index.LineCount,
            mappedBytes,
            stopwatch.ElapsedMilliseconds);
        Assert.Equal(lineCount, index.LineCount);
        Assert.Equal(lineCount * sizeof(long), mappedBytes);
    }

    private async Task<string> CreateFileAsync(string name, string contents)
    {
        var path = Path.Combine(_testDirectory, name);
        await File.WriteAllTextAsync(path, contents);
        return path;
    }
}
