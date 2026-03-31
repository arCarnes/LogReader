namespace LogReader.App.Services;

using System.IO;
using LogReader.Core.Models;

internal readonly struct FileSessionKey : IEquatable<FileSessionKey>
{
    public FileSessionKey(string filePath, FileEncoding requestedEncoding)
    {
        FilePath = Path.GetFullPath(filePath ?? string.Empty);
        RequestedEncoding = requestedEncoding;
    }

    public string FilePath { get; }

    public FileEncoding RequestedEncoding { get; }

    public bool Equals(FileSessionKey other)
        => RequestedEncoding == other.RequestedEncoding &&
           StringComparer.OrdinalIgnoreCase.Equals(FilePath, other.FilePath);

    public override bool Equals(object? obj)
        => obj is FileSessionKey other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(FilePath), RequestedEncoding);
}
