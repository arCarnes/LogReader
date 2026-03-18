namespace LogReader.Core.Interfaces;

using LogReader.Core.Models;

public interface IEncodingDetectionService
{
    FileEncoding DetectFileEncoding(string filePath, FileEncoding fallback = FileEncoding.Utf8);

    EncodingHelper.EncodingDecision ResolveEncodingDecision(string filePath, FileEncoding selectedEncoding);
}
