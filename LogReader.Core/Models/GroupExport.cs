namespace LogReader.Core.Models;

public class GroupExport
{
    public string GroupName { get; set; } = string.Empty;
    public List<string> FilePaths { get; set; } = new();
    public DateTime ExportedAt { get; set; } = DateTime.UtcNow;
}
