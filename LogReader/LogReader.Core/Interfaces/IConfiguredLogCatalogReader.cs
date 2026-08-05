namespace LogReader.Core.Interfaces;

using LogReader.Core.Models;

public interface IConfiguredLogCatalogReader
{
    Task<ConfiguredLogCatalogReadResult> ReadAsync(CancellationToken cancellationToken = default);
}
