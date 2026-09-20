namespace LogReader.Infrastructure.Repositories;

using System.Text.Json;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public sealed class JsonViewLibraryRepository : IViewLibraryRepository
{
    public const string JournalFileName = "activation.json";
    private string Root => AppPaths.EnsureDirectory(AppPaths.ViewsDirectory);
    private string LibraryPath => Path.Combine(Root, "library.json");
    private string JournalPath => Path.Combine(Root, JournalFileName);

    public async Task<ViewLibrary?> LoadAsync()
    {
        var value = await ReadAsync<ViewLibrary>(LibraryPath);
        if (value != null) Validate(value);
        return value;
    }

    public Task SaveAsync(ViewLibrary library)
    {
        Validate(library);
        return JsonStore.SaveToFileAsync(LibraryPath, library);
    }

    public async Task<ViewActivationJournal?> LoadJournalAsync()
    {
        var journal = await ReadAsync<ViewActivationJournal>(JournalPath);
        if (journal == null) return null;
        if (journal.SchemaVersion != 1) throw new InvalidDataException("Unsupported view activation journal version.");
        Validate(journal.Before);
        Validate(journal.After);
        DashboardTopologyValidator.ValidatePersistedGroups(journal.BeforeGroups);
        DashboardTopologyValidator.ValidatePersistedGroups(journal.AfterGroups);
        return journal;
    }

    public Task SaveJournalAsync(ViewActivationJournal journal) => JsonStore.SaveToFileAsync(JournalPath, journal);

    public Task ClearJournalAsync()
    {
        File.Delete(JournalPath);
        return Task.CompletedTask;
    }

    public async Task SaveSnapshotAsync(string sourceId, ViewSourceSnapshot snapshot)
    {
        var path = SnapshotPath(sourceId, snapshot.Revision);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) await JsonStore.SaveToFileAsync(path, snapshot);
    }

    public async Task<ViewSourceSnapshot> LoadSnapshotAsync(string sourceId, string revision)
        => await ReadAsync<ViewSourceSnapshot>(SnapshotPath(sourceId, revision))
            ?? throw new InvalidDataException("The accepted view source snapshot is missing. Refresh the source to restore it.");

    public Task RemoveSourceDataAsync(string sourceId)
    {
        var path = Path.Combine(Root, "sources", SafeId(sourceId));
        if (Directory.Exists(path)) Directory.Delete(path, true);
        return Task.CompletedTask;
    }

    private string SnapshotPath(string sourceId, string revision)
        => Path.Combine(Root, "sources", SafeId(sourceId), SafeId(revision) + ".json");

    internal static string SafeId(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || value.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new InvalidDataException("Invalid view storage identifier.");
        return value;
    }

    private static async Task<T?> ReadAsync<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonStore.GetOptions())
                ?? throw new InvalidDataException("The view store is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Cannot read view store '{path}'. Restore it from a backup; the original was preserved.", ex);
        }
    }

    private static void Validate(ViewLibrary library)
    {
        if (library.SchemaVersion != 1 || library.LocalViews == null || library.Sources == null || library.Active == null)
            throw new InvalidDataException("Invalid or unsupported view library. The existing data was preserved.");
        if (library.LocalViews.Count == 0 || library.LocalViews.Select(v => v.Id).Distinct().Count() != library.LocalViews.Count ||
            library.LocalViews.Select(v => v.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != library.LocalViews.Count)
            throw new InvalidDataException("The view library contains missing or duplicate local views.");
        foreach (var view in library.LocalViews)
        {
            SafeId(view.Id);
            if (string.IsNullOrWhiteSpace(view.Name)) throw new InvalidDataException("A view name is required.");
            DashboardTopologyValidator.ValidateImportedView(view.Definition);
        }
        if (library.Sources.Select(s => s.Id).Distinct().Count() != library.Sources.Count)
            throw new InvalidDataException("Duplicate view sources.");
        foreach (var source in library.Sources)
        {
            SafeId(source.Id);
            if (source.AcceptedRevision == null || !Enum.IsDefined(source.Kind) || !Enum.IsDefined(source.RevisionKind))
                throw new InvalidDataException("Invalid view source registration.");
            SafeId(source.AcceptedRevision);
        }
        if (library.Active.IsLocal ? !library.LocalViews.Any(v => v.Id == library.Active.ViewId) :
            !library.Sources.Any(s => s.Id == library.Active.SourceId))
            throw new InvalidDataException("The selected view is missing from the library.");
    }
}
