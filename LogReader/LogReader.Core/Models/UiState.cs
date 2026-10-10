namespace LogReader.Core.Models;

public sealed class UiState
{
    public Dictionary<string, bool> GroupExpansionById { get; set; } = new(StringComparer.Ordinal);
}
