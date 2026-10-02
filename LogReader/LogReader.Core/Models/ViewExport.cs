namespace LogReader.Core.Models;

public class ViewExport
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public DateTime ExportedAt { get; set; } = DateTime.UtcNow;
    public Dictionary<string, string?> FileDisplayNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ViewExportGroup> Groups { get; set; } = new();
}

public class ViewExportGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public string? ParentGroupId { get; set; }
    public LogGroupKind Kind { get; set; } = LogGroupKind.Dashboard;
    public List<string> FilePaths { get; set; } = new();
}
