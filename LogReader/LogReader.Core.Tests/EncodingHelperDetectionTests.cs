using System.Text;
using LogReader.Core;
using LogReader.Core.Models;

namespace LogReader.Core.Tests;

public class EncodingHelperDetectionTests
{
    [Fact]
    public void DetectFileEncoding_ReturnsUtf8Bom_WhenUtf8BomIsPresent()
    {
        var sample = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a', (byte)'\n' };

        var detected = EncodingHelper.DetectFileEncoding(sample, FileEncoding.Ansi);

        Assert.Equal(FileEncoding.Utf8Bom, detected);
    }

    [Fact]
    public void DetectFileEncoding_ReturnsUtf16_WhenUtf16LeBomIsPresent()
    {
        var sample = new byte[] { 0xFF, 0xFE, (byte)'a', 0x00, 0x0A, 0x00 };

        var detected = EncodingHelper.DetectFileEncoding(sample, FileEncoding.Ansi);

        Assert.Equal(FileEncoding.Utf16, detected);
    }

    [Fact]
    public void DetectFileEncoding_ReturnsUtf16Be_WhenUtf16BeBomIsPresent()
    {
        var sample = new byte[] { 0xFE, 0xFF, 0x00, (byte)'a', 0x00, 0x0A };

        var detected = EncodingHelper.DetectFileEncoding(sample, FileEncoding.Ansi);

        Assert.Equal(FileEncoding.Utf16Be, detected);
    }

    [Fact]
    public void DetectFileEncoding_ReturnsUtf16_WhenUtf16LePatternIsDetectedWithoutBom()
    {
        var sample = Encoding.Unicode.GetBytes("line one\nline two\n");

        var detected = EncodingHelper.DetectFileEncoding(sample, FileEncoding.Ansi);

        Assert.Equal(FileEncoding.Utf16, detected);
    }

    [Fact]
    public void DetectFileEncoding_ReturnsUtf16Be_WhenUtf16BePatternIsDetectedWithoutBom()
    {
        var sample = Encoding.BigEndianUnicode.GetBytes("line one\nline two\n");

        var detected = EncodingHelper.DetectFileEncoding(sample, FileEncoding.Ansi);

        Assert.Equal(FileEncoding.Utf16Be, detected);
    }

    [Fact]
    public void DetectFileEncoding_ReturnsUtf8_WhenValidUtf8WithoutBomContainsMultibyteChars()
    {
        var sample = Encoding.UTF8.GetBytes("cafe\u00E9\n");

        var detected = EncodingHelper.DetectFileEncoding(sample, FileEncoding.Ansi);

        Assert.Equal(FileEncoding.Utf8, detected);
    }

    [Fact]
    public void DetectFileEncoding_ReturnsFallback_WhenContentIsAmbiguousAscii()
    {
        var sample = Encoding.ASCII.GetBytes("line one\nline two\n");

        var detected = EncodingHelper.DetectFileEncoding(sample, FileEncoding.Ansi);

        Assert.Equal(FileEncoding.Ansi, detected);
    }

    [Fact]
    public void ResolveManualEncodingDecision_UsesSelectedEncoding()
    {
        var decision = EncodingHelper.ResolveManualEncodingDecision(FileEncoding.Utf16Be);

        Assert.Equal(FileEncoding.Utf16Be, decision.SelectedEncoding);
        Assert.Equal(FileEncoding.Utf16Be, decision.ResolvedEncoding);
        Assert.Contains("Manual", decision.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveAutoEncodingDecision_InvalidUtf8_UsesWindows1252()
    {
        var decision = EncodingHelper.ResolveAutoEncodingDecision(new byte[] { (byte)'a', 0x96, (byte)'b' });

        Assert.Equal(FileEncoding.Ansi, decision.ResolvedEncoding);
        Assert.Contains("invalid UTF-8", decision.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Windows-1252", decision.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveAutoEncodingDecision_IncompletePrefixSample_UsesUtf8()
    {
        var decision = EncodingHelper.ResolveAutoEncodingDecision(
            new byte[] { (byte)'a', 0xF0, 0x9F },
            sampleIsComplete: false);

        Assert.Equal(FileEncoding.Utf8, decision.ResolvedEncoding);
        Assert.Contains("continues beyond sample", decision.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveAutoEncodingDecision_IncompleteSequenceAtEndOfFile_UsesWindows1252()
    {
        var decision = EncodingHelper.ResolveAutoEncodingDecision(
            new byte[] { (byte)'a', 0xE2, 0x82 },
            sampleIsComplete: true);

        Assert.Equal(FileEncoding.Ansi, decision.ResolvedEncoding);
    }

    [Theory]
    [InlineData(0xC0, 0x80)]
    [InlineData(0xED, 0xA0)]
    [InlineData(0xF4, 0x90)]
    public void ResolveAutoEncodingDecision_InvalidUtf8Ranges_UseWindows1252(byte first, byte second)
    {
        var decision = EncodingHelper.ResolveAutoEncodingDecision(new[] { first, second, (byte)0x80, (byte)0x80 });

        Assert.Equal(FileEncoding.Ansi, decision.ResolvedEncoding);
    }
}
