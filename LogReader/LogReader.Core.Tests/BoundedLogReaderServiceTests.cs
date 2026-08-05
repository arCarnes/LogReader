namespace LogReader.Core.Tests;

using System.Diagnostics;
using LogReader.Core;
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
