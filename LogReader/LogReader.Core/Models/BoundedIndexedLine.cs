namespace LogReader.Core.Models;

public sealed record BoundedIndexedLine(
    int LineNumber,
    string Text,
    bool IsTruncated);
