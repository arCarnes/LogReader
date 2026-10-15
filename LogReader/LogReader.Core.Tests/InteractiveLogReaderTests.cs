namespace LogReader.Core.Tests;

using System.Text;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

public sealed class InteractiveLogReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WeezTail-interactive-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _scope;

    public InteractiveLogReaderTests()
    {
        Directory.CreateDirectory(_root);
        _scope = AppPaths.BeginTestScope(rootPath: _root, localCacheDirectory: Path.Combine(_root, "Cache"));
    }

    [Theory]
    [InlineData(FileEncoding.Utf8)]
    [InlineData(FileEncoding.Utf8Bom)]
    [InlineData(FileEncoding.Ansi)]
    [InlineData(FileEncoding.Utf16)]
    [InlineData(FileEncoding.Utf16Be)]
    public async Task SuspendedIndex_MatchesExistingReaderAcrossEncodingAndNewlineBoundaries(FileEncoding encoding)
    {
        var path = await WriteAsync(string.Concat(Enumerable.Range(0, 20000)
            .Select(i => $"row {i} café" + (i % 3 == 0 ? "\r\n" : i % 3 == 1 ? "\r" : "\n"))) + "unfinished", encoding);
        var reader = new ChunkedLogReaderService();
        using var expected = await reader.BuildIndexAsync(path, encoding);
        await using var build = await ((IInteractiveLogReaderService)reader).CreateIndexBuildAsync(path, encoding, default);
        var previous = 0L;
        while (!await build.AdvanceAsync(65535, () => false, default))
        {
            Assert.True(build.Progress.ProcessedBytes >= previous);
            previous = build.Progress.ProcessedBytes;
            await build.SuspendAsync();
            Assert.Throws<InvalidOperationException>(() => build.TakeCompletedIndex());
        }
        Assert.Equal(1, build.Progress.Attempt);
        using var actual = build.TakeCompletedIndex();
        Assert.Equal(expected.FileSize, actual.FileSize);
        Assert.Equal(expected.LineCount, actual.LineCount);
        for (var i = 0; i < actual.LineCount; i++) Assert.Equal(expected.LineOffsets[i], actual.LineOffsets[i]);
        Assert.Throws<InvalidOperationException>(() => build.TakeCompletedIndex());
    }

    [Fact]
    public async Task PriorityYield_DoesNotAdvanceAndResumptionKeepsCompletedBytes()
    {
        var path = await WriteAsync(new string('x', 200000) + "\r\nlast", FileEncoding.Utf8);
        var reader = (IInteractiveLogReaderService)new ChunkedLogReaderService();
        await using var build = await reader.CreateIndexBuildAsync(path, FileEncoding.Utf8, default);
        Assert.False(await build.AdvanceAsync(65536, () => true, default));
        Assert.Equal(0, build.Progress.ProcessedBytes);
        Assert.False(await build.AdvanceAsync(65536, () => false, default));
        await build.SuspendAsync();
        Assert.Equal(65536, build.Progress.ProcessedBytes);
        Assert.False(await build.AdvanceAsync(65536, () => false, default));
        Assert.Equal(131072, build.Progress.ProcessedBytes);
        Assert.Equal(1, build.Progress.Attempt);
    }

    [Fact]
    public async Task SuspendedIndex_AppendKeepsFrozenExtent()
    {
        var path = await WriteAsync(new string('x', 100000) + "\nlast", FileEncoding.Utf8);
        var reader = (IInteractiveLogReaderService)new ChunkedLogReaderService();
        await using var build = await reader.CreateIndexBuildAsync(path, FileEncoding.Utf8, default);
        var extent = build.Progress.TotalBytes;
        await build.AdvanceAsync(65536, () => false, default);
        await build.SuspendAsync();
        await File.AppendAllTextAsync(path, "\nappended");
        while (!await build.AdvanceAsync(65536, () => false, default)) { }
        using var index = build.TakeCompletedIndex();
        Assert.Equal(extent, index.FileSize);
        Assert.Equal(2, index.LineCount);
        Assert.Equal(1, build.Progress.Attempt);
    }

    [Theory]
    [InlineData(FileEncoding.Utf8)]
    [InlineData(FileEncoding.Utf16)]
    public async Task SuspendedIndex_SameSizeRewriteOutsideEvidence_RestartsWithCurrentOffsets(FileEncoding encoding)
    {
        var path = await WriteAsync(new string('x', 100000), encoding);
        var reader = new ChunkedLogReaderService();
        await using var build = await ((IInteractiveLogReaderService)reader).CreateIndexBuildAsync(path, encoding, default);
        Assert.False(await build.AdvanceAsync(65536, () => false, default));
        await build.SuspendAsync();
        FileGenerationToken generation;
        using (var original = File.OpenRead(path))
            generation = FileGenerationTokenProvider.Capture(original);
        Assert.True(generation.IsKnown);
        var originalBytes = await File.ReadAllBytesAsync(path);
        var bytes = (byte[])originalBytes.Clone();
        var newline = EncodingHelper.GetEncoding(encoding).GetBytes("\n");
        newline.CopyTo(bytes, 1000);
        var priorWrite = File.GetLastWriteTimeUtc(path);
        await File.WriteAllBytesAsync(path, bytes);
        File.SetLastWriteTimeUtc(path, priorWrite.AddSeconds(2));
        using (var current = File.OpenRead(path))
            Assert.Equal(generation, FileGenerationTokenProvider.Capture(current));
        Assert.NotEqual(priorWrite, File.GetLastWriteTimeUtc(path));
        Assert.Equal(originalBytes.Length, bytes.Length);
        Assert.Equal(originalBytes.Take(64), bytes.Take(64));
        Assert.Equal(originalBytes.Skip(65536 - 64).Take(64), bytes.Skip(65536 - 64).Take(64));
        while (!await build.AdvanceAsync(65536, () => false, default)) { }
        using var actual = build.TakeCompletedIndex();
        using var expected = await reader.BuildIndexAsync(path, encoding);
        Assert.Equal(2, build.Progress.Attempt);
        Assert.Equal(2, expected.LineCount);
        Assert.Equal(expected.LineCount, actual.LineCount);
        Assert.Equal(expected.FileSize, actual.FileSize);
        for (var i = 0; i < expected.LineCount; i++)
            Assert.Equal(expected.LineOffsets[i], actual.LineOffsets[i]);
        Assert.Equal(await reader.ReadLinesAsync(path, expected, 0, expected.LineCount, encoding),
            await reader.ReadLinesAsync(path, actual, 0, actual.LineCount, encoding));
    }

    [Fact]
    public async Task SuspendedIndex_ReplacementRestartsAndNeverReturnsOldOffsets()
    {
        var path = await WriteAsync(new string('x', 100000) + "\nold", FileEncoding.Utf8);
        var reader = (IInteractiveLogReaderService)new ChunkedLogReaderService();
        await using var build = await reader.CreateIndexBuildAsync(path, FileEncoding.Utf8, default);
        await build.AdvanceAsync(65536, () => false, default);
        await build.SuspendAsync();
        File.Move(path, path + ".old");
        await File.WriteAllTextAsync(path, "new\nfile");
        while (!await build.AdvanceAsync(65536, () => false, default)) { }
        using var index = build.TakeCompletedIndex();
        Assert.Equal(2, build.Progress.Attempt);
        Assert.Equal(8, index.FileSize);
        Assert.Equal(4, index.LineOffsets[1]);
    }

    [Theory]
    [InlineData(FileEncoding.Utf8)]
    [InlineData(FileEncoding.Utf8Bom)]
    [InlineData(FileEncoding.Ansi)]
    [InlineData(FileEncoding.Utf16)]
    [InlineData(FileEncoding.Utf16Be)]
    public async Task Preview_RetainsLatestLinesAndExactByteAnchors(FileEncoding encoding)
    {
        var path = await WriteAsync(string.Concat(Enumerable.Range(0, 400)
            .Select(i => $"row {i} café" + (i % 3 == 0 ? "\r\n" : i % 3 == 1 ? "\r" : "\n"))) + "unfinished", encoding);
        var reader = new ChunkedLogReaderService();
        var preview = await ((IInteractiveLogReaderService)reader).ReadRecentPreviewAsync(path, encoding, default);
        Assert.True(preview.IsAvailable);
        Assert.Equal(200, preview.Lines.Count);
        Assert.StartsWith("row 201", preview.Lines[0].Text);
        Assert.Equal("unfinished", preview.Lines[^1].Text);
        using var index = await reader.BuildIndexAsync(path, encoding);
        for (var i = 0; i < preview.Lines.Count; i++)
        {
            Assert.Equal(index.LineOffsets[201 + i], preview.Lines[i].ByteOffset);
            Assert.False(preview.Lines[i].IsTruncated);
        }
    }

    [Fact]
    public async Task Preview_WindowAndCharacterBounds_AreIndependentOfFileSize()
    {
        var path = await WriteAsync(new string('x', 2 * 1024 * 1024) + "\n" +
            string.Concat(Enumerable.Range(0, 200).Select(i => new string('z', 6000) + "\n")), FileEncoding.Utf8);
        var preview = await ((IInteractiveLogReaderService)new ChunkedLogReaderService())
            .ReadRecentPreviewAsync(path, FileEncoding.Utf8, default);
        Assert.True(preview.IsAvailable);
        Assert.InRange(preview.BytesRead, 1, InteractiveLogLoadLimits.PreviewBytes + InteractiveLogLoadLimits.PreviewValidationBytes);
        Assert.InRange(preview.Lines.Count, 1, 200);
        Assert.All(preview.Lines, line => Assert.InRange(line.Text.Length, 0, 4096));
        Assert.True(preview.Lines.Sum(line => line.Text.Length) <= 200000);
        Assert.All(preview.Lines, line => Assert.True(line.IsTruncated));
    }

    [Fact]
    public async Task Preview_SingleOversizedPartialLine_IsUnavailable()
    {
        var path = await WriteAsync(new string('x', 2 * 1024 * 1024), FileEncoding.Utf8);
        var preview = await ((IInteractiveLogReaderService)new ChunkedLogReaderService())
            .ReadRecentPreviewAsync(path, FileEncoding.Utf8, default);
        Assert.False(preview.IsAvailable);
        Assert.Empty(preview.Lines);
    }

    [Fact]
    public async Task Preview_UnknownIdentity_IsWithheld()
    {
        var path = await WriteAsync("secret-looking synthetic content", FileEncoding.Utf8);
        var reader = new ChunkedLogReaderService(ChunkedLogReaderService.GetLastWriteTimeUtc, _ => FileGenerationToken.Unknown);
        var preview = await ((IInteractiveLogReaderService)reader).ReadRecentPreviewAsync(path, FileEncoding.Utf8, default);
        Assert.False(preview.IsAvailable);
        Assert.Empty(preview.Lines);
    }

    [Theory]
    [InlineData(FileEncoding.Utf8Bom)]
    [InlineData(FileEncoding.Utf16)]
    [InlineData(FileEncoding.Utf16Be)]
    public async Task EmptyAndBomOnly_AreUsableEmptySnapshots(FileEncoding encoding)
    {
        var path = await WriteAsync("", encoding);
        var reader = (IInteractiveLogReaderService)new ChunkedLogReaderService();
        var preview = await reader.ReadRecentPreviewAsync(path, encoding, default);
        Assert.True(preview.IsAvailable);
        Assert.Empty(preview.Lines);
        await using var build = await reader.CreateIndexBuildAsync(path, encoding, default);
        Assert.True(await build.AdvanceAsync(65536, () => false, default));
        using var index = build.TakeCompletedIndex();
        Assert.Equal(0, index.LineCount);
    }

    [Fact]
    public async Task Cancellation_DisposesOwnedMappedOffsets()
    {
        var path = await WriteAsync(string.Concat(Enumerable.Repeat("a\n", 50000)), FileEncoding.Utf8);
        var reader = (IInteractiveLogReaderService)new ChunkedLogReaderService();
        var build = await reader.CreateIndexBuildAsync(path, FileEncoding.Utf8, default);
        await build.AdvanceAsync(65536, () => false, default);
        var indexFiles = Directory.GetFiles(AppPaths.IndexDirectory, "idx_*.bin", SearchOption.AllDirectories);
        Assert.NotEmpty(indexFiles);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await build.AdvanceAsync(65536, () => false, cancellation.Token));
        await build.DisposeAsync();
        Assert.All(indexFiles, file => Assert.False(File.Exists(file)));
    }

    [Fact]
    public async Task Preview_ReplacementBetweenReadAndDecode_IsRejectedAtPublication()
    {
        var path = await WriteAsync("old\ncontent", FileEncoding.Utf8);
        var calls = 0;
        var reader = new ChunkedLogReaderService(ChunkedLogReaderService.GetLastWriteTimeUtc, stream =>
        {
            if (++calls == 2)
            {
                File.Move(path, path + ".old");
                File.WriteAllText(path, "new\ncontent");
            }
            return FileGenerationTokenProvider.Capture(stream);
        });
        var preview = await ((IInteractiveLogReaderService)reader).ReadRecentPreviewAsync(path, FileEncoding.Utf8, default);
        Assert.False(preview.IsAvailable);
        Assert.Empty(preview.Lines);
    }

    [Theory]
    [InlineData(FileEncoding.Utf8)]
    [InlineData(FileEncoding.Utf8Bom)]
    [InlineData(FileEncoding.Ansi)]
    [InlineData(FileEncoding.Utf16)]
    [InlineData(FileEncoding.Utf16Be)]
    public async Task Preview_TailWindowInsideMultibyteText_DropsThePartialLeadingLine(FileEncoding encoding)
    {
        var leading = string.Concat(Enumerable.Repeat("café 😀", 400000)) + "\r\n";
        var suffix = "safe café\r\nlast";
        var path = await WriteAsync(leading + suffix, encoding);
        var codec = EncodingHelper.GetEncoding(encoding);
        var preview = await ((IInteractiveLogReaderService)new ChunkedLogReaderService())
            .ReadRecentPreviewAsync(path, encoding, default);
        Assert.True(preview.IsAvailable);
        Assert.Equal(new[] { "safe café", "last" }, preview.Lines.Select(line => line.Text));
        Assert.Equal(codec.GetPreamble().Length + codec.GetByteCount(leading), preview.Lines[0].ByteOffset);
        Assert.InRange(preview.BytesRead, 1, InteractiveLogLoadLimits.PreviewBytes + InteractiveLogLoadLimits.PreviewValidationBytes);
    }

    [Fact]
    public async Task SuspendedIndex_ShrinkRestartsWithoutPublishingOldOffsets()
    {
        var path = await WriteAsync(new string('x', 100000) + "\nold", FileEncoding.Utf8);
        var reader = (IInteractiveLogReaderService)new ChunkedLogReaderService();
        await using var build = await reader.CreateIndexBuildAsync(path, FileEncoding.Utf8, default);
        await build.AdvanceAsync(65536, () => false, default);
        await build.SuspendAsync();
        await File.WriteAllTextAsync(path, "short\nfile");
        while (!await build.AdvanceAsync(65536, () => false, default)) { }
        using var index = build.TakeCompletedIndex();
        Assert.Equal(2, build.Progress.Attempt);
        Assert.Equal(10, index.FileSize);
        Assert.Equal(6, index.LineOffsets[1]);
    }

    [Fact]
    public async Task PriorityChangeDuringQuantum_YieldsAtTheNextScanBlock()
    {
        var path = await WriteAsync(new string('x', 500000), FileEncoding.Utf8);
        var reader = (IInteractiveLogReaderService)new ChunkedLogReaderService();
        await using var build = await reader.CreateIndexBuildAsync(path, FileEncoding.Utf8, default);
        var checks = 0;
        Assert.False(await build.AdvanceAsync(InteractiveLogLoadLimits.ScanQuantumBytes, () => ++checks >= 3, default));
        Assert.Equal(65536, build.Progress.ProcessedBytes);
        await build.SuspendAsync();
        Assert.True(await build.AdvanceAsync(InteractiveLogLoadLimits.ScanQuantumBytes, () => false, default));
        Assert.Equal(1, build.Progress.Attempt);
    }

    [Fact]
    public async Task SuspendedIndex_InPlaceEncodingReplacementWithGrowth_InvalidatesTheCheckpoint()
    {
        var path = await WriteAsync(new string('x', 100000) + "\nold", FileEncoding.Utf8);
        var reader = (IInteractiveLogReaderService)new ChunkedLogReaderService();
        await using var build = await reader.CreateIndexBuildAsync(path, FileEncoding.Utf8, default);
        await build.AdvanceAsync(65536, () => false, default);
        await build.SuspendAsync();
        await File.WriteAllTextAsync(path, new string('z', 100000) + "\nnew", Encoding.Unicode);
        await build.AdvanceAsync(65536, () => false, default);
        Assert.Equal(2, build.Progress.Attempt);
    }

    [Fact]
    public async Task SuspendedIndex_GrowthWithChangedScanBoundary_DiscardsOldOffsets()
    {
        var path = await WriteAsync(new string('x', 100000) + "\nold", FileEncoding.Utf8);
        var reader = (IInteractiveLogReaderService)new ChunkedLogReaderService();
        await using var build = await reader.CreateIndexBuildAsync(path, FileEncoding.Utf8, default);
        await build.AdvanceAsync(65536, () => false, default);
        await build.SuspendAsync();
        await using (var changed = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            changed.Position = 65500;
            await changed.WriteAsync(Encoding.UTF8.GetBytes("changed\n"));
            changed.Position = changed.Length;
            await changed.WriteAsync(Encoding.UTF8.GetBytes("\nappended"));
        }
        while (!await build.AdvanceAsync(65536, () => false, default)) { }
        using var index = build.TakeCompletedIndex();
        Assert.Equal(2, build.Progress.Attempt);
        Assert.Equal(65508, index.LineOffsets[1]);
    }

    [Fact]
    public async Task Preview_InPlaceGrowthAndEncodingChangeDuringCapture_IsWithheld()
    {
        var path = await WriteAsync("old\ncontent", FileEncoding.Utf8);
        var calls = 0;
        var reader = new ChunkedLogReaderService(ChunkedLogReaderService.GetLastWriteTimeUtc, stream =>
        {
            if (++calls == 2) File.WriteAllText(path, new string('z', 2000) + "\nnew", Encoding.Unicode);
            return FileGenerationTokenProvider.Capture(stream);
        });
        var preview = await ((IInteractiveLogReaderService)reader).ReadRecentPreviewAsync(path, FileEncoding.Utf8, default);
        Assert.False(preview.IsAvailable);
        Assert.Empty(preview.Lines);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(32)]
    public async Task Preview_InstrumentedReadsHaveTheSameBoundForDifferentFileSizes(int megabytes)
    {
        var path = Path.Combine(_root, "bounded-" + megabytes + ".log");
        await using (var file = File.Create(path))
        {
            file.SetLength(megabytes * 1024L * 1024);
            file.Position = file.Length - 5;
            await file.WriteAsync(Encoding.UTF8.GetBytes("\nlast"));
        }
        var observedBytes = 0;
        var reader = new ChunkedLogReaderService(ChunkedLogReaderService.GetLastWriteTimeUtc,
            previewReadObserver: count => observedBytes += count);
        var preview = await ((IInteractiveLogReaderService)reader).ReadRecentPreviewAsync(path, FileEncoding.Utf8, default);
        Assert.True(preview.IsAvailable);
        Assert.Equal("last", Assert.Single(preview.Lines).Text);
        Assert.Equal(observedBytes, preview.BytesRead);
        Assert.InRange(observedBytes, 1, InteractiveLogLoadLimits.PreviewBytes + InteractiveLogLoadLimits.PreviewValidationBytes);
    }

    private async Task<string> WriteAsync(string text, FileEncoding encoding)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".log");
        var codec = EncodingHelper.GetEncoding(encoding);
        await File.WriteAllBytesAsync(path, codec.GetPreamble().Concat(codec.GetBytes(text)).ToArray());
        return path;
    }

    public void Dispose()
    {
        _scope.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}
