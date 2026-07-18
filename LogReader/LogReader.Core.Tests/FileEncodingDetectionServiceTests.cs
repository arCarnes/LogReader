namespace LogReader.Core.Tests;

using System.Text;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

public class FileEncodingDetectionServiceTests
{
    [Fact]
    public void ResolveEncodingDecision_AutoMode_DetectsUtf8Bom()
    {
        var path = Path.Combine(Path.GetTempPath(), $"logreader-enc-{Guid.NewGuid():N}.log");
        try
        {
            File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a', (byte)'\n' });
            var service = new FileEncodingDetectionService();

            var decision = service.ResolveEncodingDecision(path, FileEncoding.Auto);

            Assert.Equal(FileEncoding.Utf8Bom, decision.ResolvedEncoding);
            Assert.Contains("BOM", decision.StatusText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void ResolveEncodingDecision_AutoMode_WhenFileMissing_FallsBackToUtf8()
    {
        var service = new FileEncodingDetectionService();

        var decision = service.ResolveEncodingDecision(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".log"), FileEncoding.Auto);

        Assert.Equal(FileEncoding.Utf8, decision.ResolvedEncoding);
        Assert.Contains("file not found", decision.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveEncodingDecision_AutoMode_WhenReadFails_FallsBackToUtf8()
    {
        var path = Path.Combine(Path.GetTempPath(), $"logreader-enc-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        try
        {
            var service = new FileEncodingDetectionService();

            var decision = service.ResolveEncodingDecision(path, FileEncoding.Auto);

            Assert.Equal(FileEncoding.Utf8, decision.ResolvedEncoding);
            Assert.Contains("read failure", decision.StatusText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
    }

    [Fact]
    public void DetectFileEncoding_WhenFileMissing_ReturnsFallback()
    {
        var service = new FileEncodingDetectionService();

        var detected = service.DetectFileEncoding(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".log"), FileEncoding.Ansi);

        Assert.Equal(FileEncoding.Ansi, detected);
    }

    [Fact]
    public void ResolveEncodingDecision_AutoMode_Utf8SequenceSplitAtSampleBoundary_RemainsUtf8()
    {
        var path = Path.Combine(Path.GetTempPath(), $"logreader-enc-{Guid.NewGuid():N}.log");
        try
        {
            var bytes = Enumerable.Repeat((byte)'a', 4095)
                .Concat(new byte[] { 0xE2, 0x82, 0xAC })
                .ToArray();
            File.WriteAllBytes(path, bytes);
            var service = new FileEncodingDetectionService();

            var decision = service.ResolveEncodingDecision(path, FileEncoding.Auto);

            Assert.Equal(FileEncoding.Utf8, decision.ResolvedEncoding);
            Assert.Contains("continues beyond sample", decision.StatusText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void ResolveEncodingDecision_AutoMode_InvalidUtf8File_UsesWindows1252()
    {
        var path = Path.Combine(Path.GetTempPath(), $"logreader-enc-{Guid.NewGuid():N}.log");
        try
        {
            File.WriteAllBytes(path, new byte[] { (byte)'a', 0x96, (byte)'b' });
            var service = new FileEncodingDetectionService();

            var decision = service.ResolveEncodingDecision(path, FileEncoding.Auto);

            Assert.Equal(FileEncoding.Ansi, decision.ResolvedEncoding);
            Assert.Contains("Windows-1252", decision.StatusText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
