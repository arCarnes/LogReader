namespace LogReader.Core.Models;

public class LogFileEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string? DisplayName { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public DateTime LastOpenedAt { get; set; } = DateTime.UtcNow;
}
