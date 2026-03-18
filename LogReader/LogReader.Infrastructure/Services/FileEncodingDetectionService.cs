namespace LogReader.Infrastructure.Services;

using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public sealed class FileEncodingDetectionService : IEncodingDetectionService
{
    private const int MaxDetectionBytes = 4096;

    public FileEncoding DetectFileEncoding(string filePath, FileEncoding fallback = FileEncoding.Utf8)
    {
        var normalizedFallback = fallback == FileEncoding.Auto ? FileEncoding.Utf8 : fallback;
        if (string.IsNullOrWhiteSpace(filePath))
            return normalizedFallback;

        if (!File.Exists(filePath) && !Directory.Exists(filePath))
            return normalizedFallback;

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = ReadSample(stream);
            return EncodingHelper.DetectFileEncoding(buffer, normalizedFallback);
        }
        catch (Exception)
        {
            return normalizedFallback;
        }
    }

    public EncodingHelper.EncodingDecision ResolveEncodingDecision(string filePath, FileEncoding selectedEncoding)
    {
        if (selectedEncoding != FileEncoding.Auto)
            return EncodingHelper.ResolveManualEncodingDecision(selectedEncoding);

        if (string.IsNullOrWhiteSpace(filePath))
        {
            return new EncodingHelper.EncodingDecision(
                FileEncoding.Auto,
                FileEncoding.Utf8,
                "Auto -> UTF-8 (fallback: file not found)");
        }

        if (!File.Exists(filePath) && !Directory.Exists(filePath))
        {
            return new EncodingHelper.EncodingDecision(
                FileEncoding.Auto,
                FileEncoding.Utf8,
                "Auto -> UTF-8 (fallback: file not found)");
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = ReadSample(stream);
            return EncodingHelper.ResolveAutoEncodingDecision(buffer, FileEncoding.Utf8);
        }
        catch (Exception)
        {
            return new EncodingHelper.EncodingDecision(
                FileEncoding.Auto,
                FileEncoding.Utf8,
                "Auto -> UTF-8 (fallback: read failure)");
        }
    }

    private static ReadOnlySpan<byte> ReadSample(FileStream stream)
    {
        var buffer = new byte[Math.Min(MaxDetectionBytes, (int)Math.Min(int.MaxValue, stream.Length))];
        var read = stream.Read(buffer, 0, buffer.Length);
        return buffer.AsSpan(0, read);
    }
}
