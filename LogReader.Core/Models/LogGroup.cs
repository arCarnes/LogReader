namespace LogReader.Core.Models;

public class LogGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public string? ParentGroupId { get; set; }
    public LogGroupKind Kind { get; set; } = LogGroupKind.Neutral;
    public List<string> FileIds { get; set; } = new();
}
