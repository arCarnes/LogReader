namespace LogReader.Tests;

using System.Text;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

/// <summary>
/// Tests for BOM handling and UTF-16 indexing/update paths in ChunkedLogReaderService.
/// </summary>
public class LineIndexEncodingTests : IAsyncLifetime
{
    private readonly ChunkedLogReaderService _reader = new();
    private string _testDir = null!;

    public Task InitializeAsync()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "LogReaderTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_testDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, true);
        return Task.CompletedTask;
    }

    private string FilePath(string name) => Path.Combine(_testDir, name);

    private async Task<string> WriteUtf8Bom(string name, string content)
    {
        var path = FilePath(name);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    private async Task<string> WriteUtf16(string name, string content, bool bom = true)
    {
        var path = FilePath(name);
        await File.WriteAllTextAsync(path, content, new UnicodeEncoding(bigEndian: false, byteOrderMark: bom));
        return path;
    }

    /// <summary>Appends UTF-16 LE bytes (no BOM) to an existing file.</summary>
    private async Task AppendUtf16(string path, string content)
    {
        var bytes = new UnicodeEncoding(bigEndian: false, byteOrderMark: false).GetBytes(content);
        await using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        await fs.WriteAsync(bytes);
    }

    // ─── UTF-8 BOM ────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildIndex_Utf8WithBom_LineCountCorrect()
    {
        var path = await WriteUtf8Bom("bom8.log", "Line 1\nLine 2\nLine 3\n");

        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf8);

        Assert.Equal(3, index.LineCount);
    }

    [Fact]
    public async Task BuildIndex_Utf8WithBom_LineContentCorrect()
    {
        var path = await WriteUtf8Bom("bom8.log", "Line 1\nLine 2\n");

        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf8);
        var lines = await _reader.ReadLinesAsync(path, index, 0, 2, FileEncoding.Utf8);

        Assert.Equal(2, lines.Count);
        Assert.Equal("Line 1", lines[0]);
        Assert.Equal("Line 2", lines[1]);
    }

    [Fact]
    public async Task BuildIndex_Utf8WithBom_FirstLineHasNoBomCharacter()
    {
        // If the BOM offset isn't skipped, the first line would start with '\uFEFF'
        var path = await WriteUtf8Bom("bom8.log", "Hello\n");

        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf8);
        var lines = await _reader.ReadLinesAsync(path, index, 0, 1, FileEncoding.Utf8);

        Assert.Single(lines);
        Assert.Equal("Hello", lines[0]);
        Assert.DoesNotContain('\uFEFF', lines[0]);
    }

    // ─── UTF-16 LE with BOM ───────────────────────────────────────────────────

    [Fact]
    public async Task BuildIndex_Utf16WithBom_LineCountCorrect()
    {
        var path = await WriteUtf16("bom16.log", "Line 1\nLine 2\nLine 3\n");

        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf16);

        Assert.Equal(3, index.LineCount);
    }

    [Fact]
    public async Task BuildIndex_Utf16WithBom_LineContentCorrect()
    {
        var path = await WriteUtf16("bom16.log", "Line 1\nLine 2\n");

        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf16);
        var lines = await _reader.ReadLinesAsync(path, index, 0, 2, FileEncoding.Utf16);

        Assert.Equal(2, lines.Count);
        Assert.Equal("Line 1", lines[0]);
        Assert.Equal("Line 2", lines[1]);
    }

    [Fact]
    public async Task BuildIndex_Utf16WithBom_FirstLineHasNoBomCharacter()
    {
        // First line offset must be 2 (past the BOM), not 0
        var path = await WriteUtf16("bom16.log", "Hello\n");

        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf16);
        var lines = await _reader.ReadLinesAsync(path, index, 0, 1, FileEncoding.Utf16);

        Assert.Single(lines);
        Assert.Equal("Hello", lines[0]);
        Assert.DoesNotContain('\uFEFF', lines[0]);
    }

    // ─── UTF-16 LE without BOM ────────────────────────────────────────────────

    [Fact]
    public async Task BuildIndex_Utf16WithoutBom_LineCountCorrect()
    {
        var path = await WriteUtf16("nobom16.log", "Line 1\nLine 2\n", bom: false);

        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf16);

        Assert.Equal(2, index.LineCount);
    }

    [Fact]
    public async Task BuildIndex_Utf16WithoutBom_LineContentCorrect()
    {
        var path = await WriteUtf16("nobom16.log", "Line 1\nLine 2\n", bom: false);

        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf16);
        var lines = await _reader.ReadLinesAsync(path, index, 0, 2, FileEncoding.Utf16);

        Assert.Equal(2, lines.Count);
        Assert.Equal("Line 1", lines[0]);
        Assert.Equal("Line 2", lines[1]);
    }

    // ─── UTF-16 UpdateIndexAsync ──────────────────────────────────────────────

    [Fact]
    public async Task UpdateIndex_Utf16_AppendsNewLines()
    {
        var path = await WriteUtf16("append16.log", "Line 1\nLine 2\n");
        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf16);
        Assert.Equal(2, index.LineCount);

        await AppendUtf16(path, "Line 3\nLine 4\n");

        var updated = await _reader.UpdateIndexAsync(path, index, FileEncoding.Utf16);

        Assert.Equal(4, updated.LineCount);
        var lines = await _reader.ReadLinesAsync(path, updated, 2, 2, FileEncoding.Utf16);
        Assert.Equal("Line 3", lines[0]);
        Assert.Equal("Line 4", lines[1]);
    }

    [Fact]
    public async Task UpdateIndex_Utf16_EndsWithNewline_NewLineOffsetAddedCorrectly()
    {
        // File ends with \n (0x0A 0x00 in UTF-16 LE) — the update path must detect
        // this via the two-byte check and add existingIndex.FileSize as the next offset.
        var path = await WriteUtf16("newline16.log", "Line 1\n");
        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf16);
        Assert.Equal(1, index.LineCount);

        await AppendUtf16(path, "Line 2\n");

        var updated = await _reader.UpdateIndexAsync(path, index, FileEncoding.Utf16);

        Assert.Equal(2, updated.LineCount);
        var line = await _reader.ReadLineAsync(path, updated, 1, FileEncoding.Utf16);
        Assert.Equal("Line 2", line);
    }

    [Fact]
    public async Task UpdateIndex_Utf16_NoNewData_ReturnsSameIndex()
    {
        var path = await WriteUtf16("static16.log", "Line 1\n");
        using var index = await _reader.BuildIndexAsync(path, FileEncoding.Utf16);

        var updated = await _reader.UpdateIndexAsync(path, index, FileEncoding.Utf16);

        Assert.Same(index, updated);
        Assert.Equal(1, updated.LineCount);
    }
}
