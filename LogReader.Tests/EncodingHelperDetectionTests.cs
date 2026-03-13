using System.Text;
using LogReader.Core;
using LogReader.Core.Models;

namespace LogReader.Tests;

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
}
