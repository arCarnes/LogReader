namespace LogReader.Core.Models;

public class ReplacementPattern
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string FindPattern { get; set; } = string.Empty;
    public string ReplacePattern { get; set; } = string.Empty;
}
