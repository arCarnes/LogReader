namespace LogReader.Infrastructure.Repositories;

using System.Text.Json;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public class JsonLogGroupRepository : ILogGroupRepository
{
    private const string FileName = "loggroups.json";
    private const int CurrentSchemaVersion = 1;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogFileRepository _fileRepo;

    public JsonLogGroupRepository(ILogFileRepository fileRepo)
    {
        _fileRepo = fileRepo;
    }

    public async Task<List<LogGroup>> GetAllAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var (all, shouldRewrite) = await LoadGroupsCoreAsync().ConfigureAwait(false);

            if (shouldRewrite)
                await SaveGroupsCoreAsync(all).ConfigureAwait(false);

            return all.OrderBy(g => g.SortOrder).ToList();
        }
        finally { _lock.Release(); }
    }

    public async Task<LogGroup?> GetByIdAsync(string id)
    {
        var all = await GetAllAsync();
        return all.FirstOrDefault(g => g.Id == id);
    }

    public async Task AddAsync(LogGroup group)
    {
        await _lock.WaitAsync();
        try
        {
            var (all, shouldRewrite) = await LoadGroupsCoreAsync().ConfigureAwait(false);
            if (shouldRewrite)
                await SaveGroupsCoreAsync(all).ConfigureAwait(false);

            all.Add(group);
            DashboardTopologyValidator.ValidatePersistedGroups(all);
            await SaveGroupsCoreAsync(all).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    public async Task ReplaceAllAsync(IReadOnlyList<LogGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        await _lock.WaitAsync();
        try
        {
            var replacement = groups.ToList();
            DashboardTopologyValidator.ValidatePersistedGroups(replacement);
            await SaveGroupsCoreAsync(replacement).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    public async Task UpdateAsync(LogGroup group)
    {
        await _lock.WaitAsync();
        try
        {
            var (all, shouldRewrite) = await LoadGroupsCoreAsync().ConfigureAwait(false);
            if (shouldRewrite)
                await SaveGroupsCoreAsync(all).ConfigureAwait(false);

            var idx = all.FindIndex(g => g.Id == group.Id);
            if (idx >= 0) all[idx] = group;
            DashboardTopologyValidator.ValidatePersistedGroups(all);
            await SaveGroupsCoreAsync(all).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            var (all, shouldRewrite) = await LoadGroupsCoreAsync().ConfigureAwait(false);
            if (shouldRewrite)
                await SaveGroupsCoreAsync(all).ConfigureAwait(false);

            var toRemove = CollectDescendantIds(all, id);
            toRemove.Add(id);
            all.RemoveAll(g => toRemove.Contains(g.Id));
            DashboardTopologyValidator.ValidatePersistedGroups(all);
            await SaveGroupsCoreAsync(all).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    public async Task ReorderAsync(List<string> orderedIds)
    {
        await _lock.WaitAsync();
        try
        {
            var (all, shouldRewrite) = await LoadGroupsCoreAsync().ConfigureAwait(false);
            if (shouldRewrite)
                await SaveGroupsCoreAsync(all).ConfigureAwait(false);

            for (int i = 0; i < orderedIds.Count; i++)
            {
                var group = all.FirstOrDefault(g => g.Id == orderedIds[i]);
                if (group != null) group.SortOrder = i;
            }
            DashboardTopologyValidator.ValidatePersistedGroups(all);
            await SaveGroupsCoreAsync(all).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    public async Task ExportViewAsync(string exportPath)
    {
        var allGroups = await GetAllAsync();
        var allFiles = await _fileRepo.GetAllAsync();
        var filePathById = allFiles.ToDictionary(f => f.Id, f => f.FilePath, StringComparer.Ordinal);

        var export = new ViewExport
        {
            Groups = allGroups
                .Select(group => new ViewExportGroup
                {
                    Id = group.Id,
                    Name = group.Name,
                    SortOrder = group.SortOrder,
                    ParentGroupId = group.ParentGroupId,
                    Kind = group.Kind,
                    FilePaths = group.FileIds
                        .Where(filePathById.ContainsKey)
                        .Select(fileId => filePathById[fileId])
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()
                })
                .ToList(),
            ExportedAt = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(export, JsonStore.GetOptions());
        await File.WriteAllTextAsync(exportPath, json);
    }

    public async Task<ViewExport?> ImportViewAsync(string importPath)
    {
        if (!File.Exists(importPath)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(importPath);
            var export = JsonSerializer.Deserialize<ViewExport>(json, JsonStore.GetOptions());
            if (export == null)
                throw new JsonException("Import file did not contain a valid dashboard view export.");
            export.Groups ??= new List<ViewExportGroup>();
            DashboardTopologyValidator.ValidateImportedView(export);
            return export;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"The selected file is not valid dashboard view JSON: {Path.GetFileName(importPath)}",
                ex);
        }
    }

    private static HashSet<string> CollectDescendantIds(List<LogGroup> all, string parentId)
    {
        var result = new HashSet<string>();
        foreach (var child in all.Where(g => g.ParentGroupId == parentId))
        {
            result.Add(child.Id);
            result.UnionWith(CollectDescendantIds(all, child.Id));
        }
        return result;
    }

    private static async Task<(List<LogGroup> Groups, bool ShouldRewrite)> LoadGroupsCoreAsync()
    {
        try
        {
            using var document = await JsonStore.LoadDocumentAsync(FileName).ConfigureAwait(false);
            if (document == null)
                return (new List<LogGroup>(), false);

            var (groups, shouldRewrite) = DeserializeGroups(document.RootElement);
            DashboardTopologyValidator.ValidatePersistedGroups(groups);
            return (groups, shouldRewrite);
        }
        catch (PersistedStateRecoveryException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw CreateRecoveryException(
                "The saved dashboard view data is not valid JSON.",
                ex);
        }
        catch (InvalidDataException ex)
        {
            throw CreateRecoveryException(ex.Message, ex);
        }
    }

    private static (List<LogGroup> Groups, bool ShouldRewrite) DeserializeGroups(JsonElement root)
        => JsonRepositoryEnvelope.Deserialize<List<LogGroup>>(
            root,
            CurrentSchemaVersion,
            "log group");

    private static Task SaveGroupsCoreAsync(List<LogGroup> groups)
        => JsonStore.SaveAsync(
            FileName,
            new VersionedRepositoryEnvelope<List<LogGroup>>
            {
                SchemaVersion = CurrentSchemaVersion,
                Data = groups
            });

    private static PersistedStateRecoveryException CreateRecoveryException(string reason, Exception innerException)
        => new(
            "dashboard view",
            JsonStore.GetFilePath(FileName),
            reason,
            innerException);
}
