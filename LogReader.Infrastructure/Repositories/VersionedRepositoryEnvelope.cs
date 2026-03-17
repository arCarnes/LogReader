namespace LogReader.Infrastructure.Repositories;

internal sealed class VersionedRepositoryEnvelope<T>
{
    public int SchemaVersion { get; set; }

    public T? Data { get; set; }
}
