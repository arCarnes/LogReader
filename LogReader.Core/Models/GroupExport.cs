namespace LogReader.Core.Models;

public class GroupExport
{
    // Legacy single-dashboard import/export payload kept for backward-compatible imports.
    public string GroupName { get; set; } = string.Empty;
    public List<string> FilePaths { get; set; } = new();
    public DateTime ExportedAt { get; set; } = DateTime.UtcNow;
}
