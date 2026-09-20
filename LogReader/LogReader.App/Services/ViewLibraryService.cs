namespace LogReader.App.Services;

using System.IO;
using System.Text.Json;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

/// <summary>Owns saved definitions; the group repository remains authoritative for active local edits.</summary>
internal sealed class ViewLibraryService
{
    private readonly IViewLibraryRepository _store;
    private readonly ILogGroupRepository _groups;
    private readonly LogFileCatalogService _files;
    private readonly DashboardMutationCoordinator _mutations;
    private readonly Func<IEnumerable<LogFileEntry>, Task> _cleanup;
    public ViewLibrary? Library { get; private set; }
    public bool IsReadOnly => Library != null && !Library.Active.IsLocal;
    public bool NeedsRecovery { get; private set; }

    public ViewLibraryService(IViewLibraryRepository store, ILogGroupRepository groups, LogFileCatalogService files,
        DashboardMutationCoordinator mutations, Func<IEnumerable<LogFileEntry>, Task> cleanup)
    {
        _store = store;
        _groups = groups;
        _files = files;
        _mutations = mutations;
        _cleanup = cleanup;
    }

    public Task InitializeAsync() => _mutations.ExecuteLibraryAsync(async () =>
    {
        await RecoverAsync();
        Library = await _store.LoadAsync();
        if (Library != null) return;
        var view = new SavedView { Name = "Default View", Definition = await CaptureAsync() };
        var initial = new ViewLibrary { LocalViews = [view], Active = new(ViewIdentity.LocalSourceId, view.Id) };
        await _store.SaveAsync(initial);
        Library = initial;
    });

    public async Task<IReadOnlyList<(ViewIdentity Identity, string Source, string Name)>> ListAsync()
    {
        var library = RequireLibrary();
        var result = library.LocalViews.Select(v => (new ViewIdentity(ViewIdentity.LocalSourceId, v.Id), "My Views", v.Name)).ToList();
        foreach (var source in library.Sources)
        {
            var snapshot = await _store.LoadSnapshotAsync(source.Id, source.AcceptedRevision!);
            result.AddRange(snapshot.Views.Select(v => (new ViewIdentity(source.Id, v.Id), source.Name, v.Name)));
        }
        return result;
    }

    public Task ActivateAsync(ViewIdentity identity) => _mutations.ExecuteLibraryAsync(async () =>
    {
        if (RequireLibrary().Active == identity) return;
        var next = Copy(RequireLibrary());
        await CaptureOutgoingAsync(next);
        next.Active = identity;
        await ActivateCoreAsync(next);
    });

    public Task CreateAsync(string name, bool copyActive = false, ViewExport? imported = null)
        => _mutations.ExecuteLibraryAsync(async () =>
        {
            var next = Copy(RequireLibrary());
            name = ValidateName(next, name);
            await CaptureOutgoingAsync(next);
            var definition = imported != null ? Copy(imported) : copyActive ? await CaptureAsync() : new ViewExport();
            DashboardTopologyValidator.ValidateImportedView(definition);
            var ids = definition.Groups.ToDictionary(g => g.Id, _ => Guid.NewGuid().ToString());
            foreach (var group in definition.Groups)
            {
                group.ParentGroupId = string.IsNullOrWhiteSpace(group.ParentGroupId) ? null : ids[group.ParentGroupId];
                group.Id = ids[group.Id];
            }
            var view = new SavedView { Name = name, Definition = definition };
            next.LocalViews.Add(view);
            next.Active = new(ViewIdentity.LocalSourceId, view.Id);
            await ActivateCoreAsync(next);
        });

    public Task RenameAsync(string viewId, string name) => EditAsync(next =>
    {
        next.LocalViews.Single(v => v.Id == viewId).Name = ValidateName(next, name, viewId);
    });

    public Task DeleteAsync(string viewId) => EditAsync(next =>
    {
        if (next.LocalViews.Count == 1 || next.Active == new ViewIdentity(ViewIdentity.LocalSourceId, viewId))
            throw new InvalidOperationException("Switch to another view before deleting this view. Keep at least one local view.");
        if (next.LocalViews.RemoveAll(v => v.Id == viewId) == 0) throw new InvalidOperationException("The view no longer exists.");
    });

    public Task AcceptSourceAsync(ViewSourceRegistration registration, ViewSourceSnapshot snapshot)
        => _mutations.ExecuteLibraryAsync(async () =>
        {
            var next = Copy(RequireLibrary());
            await CaptureOutgoingAsync(next);
            var existing = next.Sources.SingleOrDefault(s => s.Id == registration.Id);
            var source = Copy(registration);
            source.GroupIds = existing?.GroupIds ?? new();
            source.PreviousRevision = existing?.AcceptedRevision;
            source.AcceptedRevision = snapshot.Revision;
            source.Commit = snapshot.Commit;
            source.LastRefreshedAt = DateTime.UtcNow;
            if (string.IsNullOrWhiteSpace(source.Name)) source.Name = snapshot.Name;
            if (next.Active.SourceId == source.Id && !snapshot.Views.Any(v => v.Id == next.Active.ViewId))
                throw new InvalidOperationException("This update removes the active view. Switch to another view before refreshing.");
            await _store.SaveSnapshotAsync(source.Id, snapshot);
            if (existing != null) next.Sources[next.Sources.IndexOf(existing)] = source;
            else next.Sources.Add(source);
            if (next.Active.SourceId == source.Id) await ActivateCoreAsync(next);
            else { await _store.SaveAsync(next); Library = next; }
        });

    public Task RenameSourceAsync(string sourceId, string name) => EditAsync(next =>
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Enter a source name.");
        next.Sources.Single(s => s.Id == sourceId).Name = name.Trim();
    });

    public async Task RemoveSourceAsync(string sourceId)
    {
        await EditAsync(next =>
        {
            if (next.Active.SourceId == sourceId) throw new InvalidOperationException("Switch to another source before removing this source.");
            next.Sources.RemoveAll(s => s.Id == sourceId);
        });
        await _store.RemoveSourceDataAsync(sourceId);
    }

    private Task EditAsync(Action<ViewLibrary> edit) => _mutations.ExecuteLibraryAsync(async () =>
    {
        var next = Copy(RequireLibrary());
        edit(next);
        await _store.SaveAsync(next);
        Library = next;
    });

    private async Task ActivateCoreAsync(ViewLibrary next)
    {
        // A leftover journal always wins over subsequent operations.
        await RecoverAsync();
        var definition = await ResolveAsync(next);
        DashboardTopologyValidator.ValidateImportedView(definition);
        var journal = new ViewActivationJournal
        {
            Before = Copy(RequireLibrary()), After = next, BeforeGroups = await _groups.GetAllAsync()
        };
        // Include the captured outgoing edits in the rollback state as well.
        if (journal.Before.Active.IsLocal)
            journal.Before.LocalViews.Single(v => v.Id == journal.Before.Active.ViewId).Definition = await CaptureAsync();
        NeedsRecovery = true;
        await _store.SaveJournalAsync(journal);
        LogFileRegistrationBatch? registration = null;
        var committed = false;
        try
        {
            registration = await _files.EnsureRegisteredWithChangesAsync(definition.Groups.SelectMany(g => g.FilePaths));
            journal.AfterGroups = definition.Groups.Select(g => new LogGroup
            {
                Id = g.Id, Name = g.Name, SortOrder = g.SortOrder, ParentGroupId = g.ParentGroupId, Kind = g.Kind,
                FileIds = g.FilePaths.Select(p => registration.EntriesByPath[p].Id).ToList()
            }).ToList();
            DashboardTopologyValidator.ValidatePersistedGroups(journal.AfterGroups);
            await _store.SaveJournalAsync(journal);
            await _groups.ReplaceAllAsync(journal.AfterGroups);
            await _store.SaveAsync(next);
            journal.Committed = true;
            await _store.SaveJournalAsync(journal);
            committed = true;
            Library = next;
            await _files.CompleteRegistrationAsync(registration.CreatedEntries);
            await _store.ClearJournalAsync();
            NeedsRecovery = false;
        }
        catch (Exception activationError)
        {
            if (committed) throw new IOException("The view was committed, but recovery cleanup failed. Restart WeezTail before editing.", activationError);
            try
            {
                var durableJournal = await _store.LoadJournalAsync();
                await RecoverAsync();
                if (durableJournal?.Committed == true)
                {
                    if (registration != null) await _files.CompleteRegistrationAsync(registration.CreatedEntries);
                    return;
                }
                if (registration != null) await _cleanup(registration.CreatedEntries);
            }
            catch (Exception recoveryError)
            {
                throw new AggregateException("View activation and recovery failed. Restart WeezTail to retry recovery.", activationError, recoveryError);
            }
            throw;
        }
    }

    private async Task RecoverAsync()
    {
        var journal = await _store.LoadJournalAsync();
        if (journal == null)
        {
            await _store.ClearJournalAsync();
            NeedsRecovery = false;
            return;
        }
        var library = journal.Committed ? journal.After : journal.Before;
        await _groups.ReplaceAllAsync(journal.Committed ? journal.AfterGroups : journal.BeforeGroups);
        await _store.SaveAsync(library);
        await _store.ClearJournalAsync();
        Library = library;
        NeedsRecovery = false;
    }

    private async Task<ViewExport> ResolveAsync(ViewLibrary library)
    {
        if (library.Active.IsLocal) return Copy(library.LocalViews.Single(v => v.Id == library.Active.ViewId).Definition);
        var source = library.Sources.Single(s => s.Id == library.Active.SourceId);
        var snapshot = await _store.LoadSnapshotAsync(source.Id, source.AcceptedRevision!);
        var definition = Copy(snapshot.Views.Single(v => v.Id == library.Active.ViewId).Definition);
        if (!source.GroupIds.TryGetValue(library.Active.ViewId, out var ids))
            source.GroupIds[library.Active.ViewId] = ids = new();
        foreach (var group in definition.Groups)
            if (!ids.ContainsKey(group.Id)) ids[group.Id] = Guid.NewGuid().ToString();
        foreach (var group in definition.Groups)
        {
            group.ParentGroupId = string.IsNullOrWhiteSpace(group.ParentGroupId) ? null : ids[group.ParentGroupId];
            group.Id = ids[group.Id];
        }
        return definition;
    }

    private async Task CaptureOutgoingAsync(ViewLibrary library)
    {
        if (library.Active.IsLocal)
            library.LocalViews.Single(v => v.Id == library.Active.ViewId).Definition = await CaptureAsync();
    }

    private async Task<ViewExport> CaptureAsync()
    {
        var groups = await _groups.GetAllAsync();
        var files = await _files.GetByIdsAsync(groups.SelectMany(g => g.FileIds));
        return new ViewExport { Groups = groups.Select(g => new ViewExportGroup
        {
            Id = g.Id, Name = g.Name, SortOrder = g.SortOrder, Kind = g.Kind, ParentGroupId = g.ParentGroupId,
            FilePaths = g.FileIds.Select(id => files.TryGetValue(id, out var file) ? file.FilePath :
                throw new InvalidDataException($"Dashboard '{g.Name}' references missing file metadata. Repair it before switching views.")).ToList()
        }).ToList() };
    }

    private ViewLibrary RequireLibrary()
    {
        if (NeedsRecovery) throw new InvalidOperationException("Restart WeezTail to finish the pending view recovery before making further changes.");
        return Library ?? throw new InvalidOperationException("The view library has not loaded.");
    }
    internal static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    private static string ValidateName(ViewLibrary library, string name, string? exceptId = null)
    {
        name = name.Trim();
        if (name.Length == 0 || library.LocalViews.Any(v => v.Id != exceptId && string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Enter a nonblank, unique name for My Views.");
        return name;
    }
}
