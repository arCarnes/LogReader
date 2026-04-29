namespace LogReader.Core.Models;

public enum SearchRequestSourceMode
{
    DiskSnapshot,
    Tail,
    SnapshotAndTail
}

public enum SearchRequestUsage
{
    DiskSearch,
    FilterApply
}

public class SearchRequest
{
    public string Query { get; set; } = string.Empty;
    public bool IsRegex { get; set; }
    public bool CaseSensitive { get; set; }
    public List<string> FilePaths { get; set; } = new();
    public Dictionary<string, IReadOnlyList<int>> AllowedLineNumbersByFilePath { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public long? StartLineNumber { get; set; }
    public long? EndLineNumber { get; set; }
    public string? FromTimestamp { get; set; }
    public string? ToTimestamp { get; set; }
    public SearchRequestSourceMode SourceMode { get; set; } = SearchRequestSourceMode.DiskSnapshot;
    public SearchRequestUsage Usage { get; set; } = SearchRequestUsage.DiskSearch;
    public int? MaxHitsPerFile { get; set; }
    public int? MaxRetainedLineTextLength { get; set; }

    public SearchRequest Clone()
        => new()
        {
            Query = Query,
            IsRegex = IsRegex,
            CaseSensitive = CaseSensitive,
            FilePaths = FilePaths.ToList(),
            AllowedLineNumbersByFilePath = AllowedLineNumbersByFilePath.ToDictionary(
                entry => entry.Key,
                entry => (IReadOnlyList<int>)entry.Value.ToList(),
                StringComparer.OrdinalIgnoreCase),
            StartLineNumber = StartLineNumber,
            EndLineNumber = EndLineNumber,
            FromTimestamp = FromTimestamp,
            ToTimestamp = ToTimestamp,
            SourceMode = SourceMode,
            Usage = Usage,
            MaxHitsPerFile = MaxHitsPerFile,
            MaxRetainedLineTextLength = MaxRetainedLineTextLength
        };

    public static SearchRequest Create(
        string query,
        bool isRegex,
        bool caseSensitive,
        IEnumerable<string> filePaths,
        SearchRequestSourceMode sourceMode,
        SearchRequestUsage usage,
        string? fromTimestamp = null,
        string? toTimestamp = null,
        IReadOnlyDictionary<string, IReadOnlyList<int>>? allowedLineNumbersByFilePath = null,
        long? startLineNumber = null,
        long? endLineNumber = null,
        int? maxHitsPerFile = null,
        int? maxRetainedLineTextLength = null,
        bool cloneAllowedLineNumbers = true)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        return new SearchRequest
        {
            Query = query,
            IsRegex = isRegex,
            CaseSensitive = caseSensitive,
            FilePaths = filePaths.ToList(),
            AllowedLineNumbersByFilePath = cloneAllowedLineNumbers
                ? CloneAllowedLineNumbers(allowedLineNumbersByFilePath)
                : PreserveAllowedLineNumbers(allowedLineNumbersByFilePath),
            StartLineNumber = startLineNumber,
            EndLineNumber = endLineNumber,
            FromTimestamp = NormalizeOptionalText(fromTimestamp),
            ToTimestamp = NormalizeOptionalText(toTimestamp),
            SourceMode = sourceMode,
            Usage = usage,
            MaxHitsPerFile = maxHitsPerFile,
            MaxRetainedLineTextLength = maxRetainedLineTextLength
        };
    }

    private static Dictionary<string, IReadOnlyList<int>> CloneAllowedLineNumbers(
        IReadOnlyDictionary<string, IReadOnlyList<int>>? allowedLineNumbersByFilePath)
    {
        if (allowedLineNumbersByFilePath == null)
            return new Dictionary<string, IReadOnlyList<int>>(StringComparer.OrdinalIgnoreCase);

        return allowedLineNumbersByFilePath.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<int>)entry.Value.ToList(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, IReadOnlyList<int>> PreserveAllowedLineNumbers(
        IReadOnlyDictionary<string, IReadOnlyList<int>>? allowedLineNumbersByFilePath)
    {
        if (allowedLineNumbersByFilePath == null)
            return new Dictionary<string, IReadOnlyList<int>>(StringComparer.OrdinalIgnoreCase);

        return new Dictionary<string, IReadOnlyList<int>>(
            allowedLineNumbersByFilePath,
            StringComparer.OrdinalIgnoreCase);
    }

    private static string? NormalizeOptionalText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
