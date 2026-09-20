namespace LogReader.Core.Interfaces;

using LogReader.Core.Models;

public interface IViewLibraryRepository
{
    Task<ViewLibrary?> LoadAsync();
    Task SaveAsync(ViewLibrary library);
    Task<ViewActivationJournal?> LoadJournalAsync();
    Task SaveJournalAsync(ViewActivationJournal journal);
    Task ClearJournalAsync();
    Task SaveSnapshotAsync(string sourceId, ViewSourceSnapshot snapshot);
    Task<ViewSourceSnapshot> LoadSnapshotAsync(string sourceId, string revision);
    Task RemoveSourceDataAsync(string sourceId);
}

public interface IViewSourceReader
{
    Task<ViewSourceSnapshot> ReadAsync(ViewSourceRegistration source, CancellationToken cancellationToken = default);
}

public sealed record GitViewReferences(string? DefaultBranch, IReadOnlyList<string> Branches, IReadOnlyList<string> Tags);

public interface IGitViewSourceClient : IViewSourceReader
{
    Task<GitViewReferences> GetReferencesAsync(ViewSourceRegistration source, CancellationToken cancellationToken = default);
    Task RemoveCacheAsync(string sourceId);
}
