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
                Assert.Equal("file_grew_during_scan", Assert.Single(state.Reasons));
                Assert.DoesNotContain("file_changed_during_search", state.Reasons);
            }
            await File.WriteAllTextAsync(path, "x");
            var error = await Assert.ThrowsAsync<ScanFileException>(() => scanner.OpenAsync(path, state, default));
            Assert.Equal("log_generation_changed", error.Code);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task FrozenExtentGrowthDuringReadRemainsACaveatAndExcludesNewBytes()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "needle\nneedle\n");
            var scanner = new ResumableLogScanner(new FixedEncoding(FileEncoding.Utf8));
            var state = new ResumableFileCheckpoint();
            var request = SearchRequest.Create("needle", false, false, [path],
                SearchRequestSourceMode.DiskSnapshot, SearchRequestUsage.DiskSearch);
            await using var reader = await scanner.OpenAsync(path, state, default);
            var extent = state.Extent;
            var first = await scanner.ReadNextAsync(reader, state, request, null,
                new ScanWorkBudget(1000, 1024), 1024, default);
            Assert.Equal(1, first!.Number);
            await File.AppendAllTextAsync(path, "needle appended\n");
            scanner.ValidateAfterRead(reader.Stream, state);
            Assert.Equal("file_grew_during_scan", Assert.Single(state.Reasons));
            Assert.DoesNotContain("file_changed_during_search", state.Reasons);
            var lines = new List<EvaluatedScanLine> { first };
            for (var attempt = 0; !state.Done && attempt < 10; attempt++)
            {
                var line = await scanner.ReadNextAsync(reader, state, request, null,
                    new ScanWorkBudget(1000, 1024), 1024, default);
                if (line != null) lines.Add(line);
            }
            scanner.ValidateAfterRead(reader.Stream, state);
            Assert.True(state.Done);
            Assert.Equal(extent, state.Extent);
            Assert.Equal(extent, state.ReadOffset);
            Assert.True(state.ObservedLength > extent);
            Assert.Equal(new[] { "needle", "needle" }, lines.Select(line => line.Text));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("replace")]
    [InlineData("truncate")]
    [InlineData("same-size-edit")]
    public async Task IncompatibleChangesStillRejectContinuation(string change)
    {
        var path = Path.GetTempFileName();
        var replacement = path + ".replacement";
        try
        {
            await File.WriteAllTextAsync(path, "needle\n");
            var scanner = new ResumableLogScanner(new FixedEncoding(FileEncoding.Utf8));
            var state = new ResumableFileCheckpoint();
            await using (var reader = await scanner.OpenAsync(path, state, default)) { }
            if (change == "replace")
            {
                await File.WriteAllTextAsync(replacement, "needle\n");
                File.Move(replacement, path, overwrite: true);
            }
            else
            {
                await File.WriteAllTextAsync(path, change == "truncate" ? "x" : "other!\n");
                File.SetLastWriteTimeUtc(path, state.ObservedWriteUtc.AddMinutes(1));
            }
            var error = await Assert.ThrowsAsync<ScanFileException>(() => scanner.OpenAsync(path, state, default));
            Assert.Equal("log_generation_changed", error.Code);
            Assert.Empty(state.Reasons);
        }
        finally
        {
            File.Delete(path);
            File.Delete(replacement);
        }
    }

    [Fact]
    public async Task LiveUtf8_ByteSlicesRetainPendingBytesWhileSnapshotDecodingRemainsFrozen()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, new byte[] { (byte)'a', 0xC3 });
            var scanner = new ResumableLogScanner(new FixedEncoding(FileEncoding.Utf8));
            var request = SearchRequest.Create("", false, false, [path],
                SearchRequestSourceMode.Tail, SearchRequestUsage.DiskSearch);
            var state = new ResumableFileCheckpoint { IsLiveObservation = true };
            for (var slice = 0; slice < 3; slice++)
            {
                state = state.Clone();
                await using var reader = await scanner.OpenAsync(path, state, default);
                var budget = new ScanWorkBudget(1000, 1);
                Assert.Null(await scanner.ReadNextAsync(reader, state, request, null, budget, 1024, default));
                Assert.InRange(budget.Bytes, 0, 1);
                scanner.ValidateAfterRead(reader.Stream, state);
            }
            Assert.True(state.AwaitingUtf8Continuation);
            Assert.False(state.Done);
            Assert.Equal(2, state.PartialCount);
            Assert.Equal(0, state.EvaluatedLines);
            Assert.Equal(1, state.LineNumber);

            var snapshot = new ResumableFileCheckpoint();
            await using (var reader = await scanner.OpenAsync(path, snapshot, default))
            {
                var line = await scanner.ReadNextAsync(reader, snapshot, request, null,
                    new ScanWorkBudget(1000, 1024), 1024, default);
                Assert.Equal("a\uFFFD", line!.Text);
                Assert.True(snapshot.Done);
                Assert.False(snapshot.AwaitingUtf8Continuation);
            }

            await using (var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                await writer.WriteAsync(new byte[] { 0xA9, 10 });
            state = state.Clone();
            Assert.True(state.IsLiveObservation);
            Assert.True(state.AwaitingUtf8Continuation);
            await using (var reader = await scanner.OpenAsync(path, state, default))
            {
                state.Extent = state.ObservedLength;
                var line = await scanner.ReadNextAsync(reader, state, request, null,
                    new ScanWorkBudget(1000, 1024), 1024, default);
                Assert.Equal("aé", line!.Text);
                Assert.Equal(0, state.LastLineStartOffset);
                Assert.Equal(1, state.EvaluatedLines);
                Assert.False(state.AwaitingUtf8Continuation);
                Assert.True(state.Done);
                scanner.ValidateAfterRead(reader.Stream, state);
            }
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
