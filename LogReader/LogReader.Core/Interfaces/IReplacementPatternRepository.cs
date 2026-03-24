namespace LogReader.Core.Interfaces;

using LogReader.Core.Models;

public interface IReplacementPatternRepository
{
    Task<List<ReplacementPattern>> LoadAsync();

    Task SaveAsync(List<ReplacementPattern> patterns);
}
