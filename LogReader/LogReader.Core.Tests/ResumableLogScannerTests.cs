namespace LogReader.Core.Tests;

using System.Text;
using System.Diagnostics;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

public sealed class ResumableLogScannerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OccurrenceEnumerationYieldsAndResumesInsideOneLine(bool useRegex)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, new string('a', 8000));
            var scanner = new ResumableLogScanner(new FixedEncoding(FileEncoding.Utf8));
            var state = new ResumableFileCheckpoint();
            var request = SearchRequest.Create(useRegex ? "(?=a)" : "a", useRegex, false, [path],
                SearchRequestSourceMode.DiskSnapshot, SearchRequestUsage.DiskSearch);
            var regex = useRegex ? RegexPatternFactory.Create(request.Query, false) : null;
            var clock = 0L;
            var step = Math.Max(1, Stopwatch.Frequency / 100_000);
            var yieldedMatcher = false;
            EvaluatedScanLine? result = null;
            for (var page = 0; !state.Done && page < 200; page++)
            {
                state = state.Clone();
                await using var reader = await scanner.OpenAsync(path, state, default);
                var budget = new ScanWorkBudget(1, 100_000, () => clock += step);
                result = await scanner.ReadNextAsync(reader, state, request, regex, budget, 16_000, default) ?? result;
                yieldedMatcher |= state.Matcher is { Occurrences: > 0 };
            }
            Assert.True(yieldedMatcher);
            Assert.True(state.Done);
            Assert.NotNull(result);
            Assert.Equal(8000, result.Occurrences);
            Assert.Equal(useRegex ? 0 : 1, result.Hit!.MatchLength);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(FileEncoding.Utf8)]
    [InlineData(FileEncoding.Utf8Bom)]
    [InlineData(FileEncoding.Ansi)]
    [InlineData(FileEncoding.Utf16)]
    [InlineData(FileEncoding.Utf16Be)]
    public async Task TinyByteSlicesPreserveEveryLineAndOccurrence(FileEncoding encoding)
    {
        var path = Path.GetTempFileName();
        try
        {
            var text = "é needle needle\r\n\rneedle\nother\rfinal needle";
            await File.WriteAllTextAsync(path, text, EncodingHelper.GetEncoding(encoding));
            var scanner = new ResumableLogScanner(new FixedEncoding(encoding));
            var state = new ResumableFileCheckpoint();
            var request = SearchRequest.Create("needle", false, false, [path], SearchRequestSourceMode.DiskSnapshot, SearchRequestUsage.DiskSearch,
                maxRetainedLineTextLength: 4096);
            var lines = new List<EvaluatedScanLine>();
            for (var page = 0; !state.Done && page < 200; page++)
            {
                state = state.Clone();
                await using var reader = await scanner.OpenAsync(path, state, default);
                var budget = new ScanWorkBudget(1000, 5);
                while (!budget.IsStopped && !state.Done)
                {
                    var line = await scanner.ReadNextAsync(reader, state, request, null, budget, 1024, default);
                    if (line != null)
                        lines.Add(line);
                }
                scanner.ValidateAfterRead(reader.Stream, state);
            }
            Assert.True(state.Done);
            Assert.Equal(new[] { "é needle needle", "", "needle", "other", "final needle" }, lines.Select(l => l.Text));
            Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, lines.Select(l => l.Number));
            Assert.Equal(new[] { 2, 0, 1, 0, 1 }, lines.Select(l => l.Occurrences));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task FrozenExtentExcludesAppendsAndReplacementOrEditsCannotResume()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "needle\nneedle");
            var scanner = new ResumableLogScanner(new FixedEncoding(FileEncoding.Utf8));
            var state = new ResumableFileCheckpoint();
            var request = SearchRequest.Create("needle", false, false, [path], SearchRequestSourceMode.DiskSnapshot, SearchRequestUsage.DiskSearch);
            await using (var reader = await scanner.OpenAsync(path, state, default))
                await scanner.ReadNextAsync(reader, state, request, null, new ScanWorkBudget(1000, 3), 1024, default);
            var extent = state.Extent;
            await File.AppendAllTextAsync(path, "\nneedle appended");
            await using (var reader = await scanner.OpenAsync(path, state, default))
            {
                while (!state.Done)
                    await scanner.ReadNextAsync(reader, state, request, null, new ScanWorkBudget(1000, 1024), 1024, default);
                Assert.Equal(extent, state.ReadOffset);
                Assert.Contains("file_changed_during_search", state.Reasons);
            }
            await File.WriteAllTextAsync(path, "x");
            var error = await Assert.ThrowsAsync<ScanFileException>(() => scanner.OpenAsync(path, state, default));
            Assert.Equal("log_generation_changed", error.Code);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task PhysicalLineLimitIncludesCrLf()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "12345\r\n");
            var scanner = new ResumableLogScanner(new FixedEncoding(FileEncoding.Utf8));
            var state = new ResumableFileCheckpoint();
            await using var reader = await scanner.OpenAsync(path, state, default);
            var error = await Assert.ThrowsAsync<ScanFileException>(() => scanner.ReadNextAsync(reader, state,
                SearchRequest.Create("1", false, false, [path], SearchRequestSourceMode.DiskSnapshot, SearchRequestUsage.DiskSearch), null, new ScanWorkBudget(1000, 1024), 6, default));
            Assert.Equal("log_line_too_large", error.Code);
        }
        finally { File.Delete(path); }
    }

    private sealed class FixedEncoding(FileEncoding encoding) : IEncodingDetectionService
    {
        public FileEncoding DetectFileEncoding(string filePath, FileEncoding fallback = FileEncoding.Utf8) => encoding;
        public EncodingHelper.EncodingDecision ResolveEncodingDecision(string filePath, FileEncoding selectedEncoding)
            => EncodingHelper.ResolveManualEncodingDecision(encoding);
    }
}
