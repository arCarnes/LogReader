namespace LogReader.Infrastructure.Repositories;

using System.Text.Json;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

public class JsonLogGroupRepository : ILogGroupRepository
{
    private const string FileName = "loggroups.json";
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
            List<LogGroup> all;
            try
            {
                all = await JsonStore.LoadAsync<List<LogGroup>>(FileName);
            }
            catch (JsonException)
            {
                // Clean break: incompatible legacy group data is reset.
                all = new List<LogGroup>();
                await JsonStore.SaveAsync(FileName, all);
            }

            if (NormalizeTree(all))
            {
                await JsonStore.SaveAsync(FileName, all);
            }
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
            var all = await JsonStore.LoadAsync<List<LogGroup>>(FileName);
            all.Add(group);
            await JsonStore.SaveAsync(FileName, all);
        }
        finally { _lock.Release(); }
    }

    public async Task UpdateAsync(LogGroup group)
    {
        await _lock.WaitAsync();
        try
        {
            var all = await JsonStore.LoadAsync<List<LogGroup>>(FileName);
            var idx = all.FindIndex(g => g.Id == group.Id);
            if (idx >= 0) all[idx] = group;
            await JsonStore.SaveAsync(FileName, all);
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            var all = await JsonStore.LoadAsync<List<LogGroup>>(FileName);
            var toRemove = CollectDescendantIds(all, id);
            toRemove.Add(id);
            all.RemoveAll(g => toRemove.Contains(g.Id));
            await JsonStore.SaveAsync(FileName, all);
        }
        finally { _lock.Release(); }
    }

    public async Task ReorderAsync(List<string> orderedIds)
    {
        await _lock.WaitAsync();
        try
        {
            var all = await JsonStore.LoadAsync<List<LogGroup>>(FileName);
            for (int i = 0; i < orderedIds.Count; i++)
            {
                var group = all.FirstOrDefault(g => g.Id == orderedIds[i]);
                if (group != null) group.SortOrder = i;
            }
            await JsonStore.SaveAsync(FileName, all);
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
            SchemaVersion = 1,
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
            var export = TryDeserializeViewExport(json);
            if (export == null)
                throw new JsonException("Import file did not contain a valid dashboard view export.");
            return export;
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

    private static ViewExport? TryDeserializeViewExport(string json)
    {
        var export = JsonSerializer.Deserialize<ViewExport>(json, JsonStore.GetOptions());
        if (export?.SchemaVersion >= 1)
        {
            export.Groups ??= new List<ViewExportGroup>();
            return export;
        }

        var legacyExport = JsonSerializer.Deserialize<GroupExport>(json, JsonStore.GetOptions());
        if (legacyExport == null)
            return null;

        legacyExport.FilePaths ??= new List<string>();

        if (string.IsNullOrWhiteSpace(legacyExport.GroupName) && legacyExport.FilePaths.Count == 0)
            return null;

        return new ViewExport
        {
            SchemaVersion = 1,
            ExportedAt = legacyExport.ExportedAt,
            Groups = new List<ViewExportGroup>
            {
                new()
                {
                    Name = string.IsNullOrWhiteSpace(legacyExport.GroupName)
                        ? "Imported Dashboard"
                        : legacyExport.GroupName,
                    Kind = LogGroupKind.Dashboard,
                    SortOrder = 0,
                    FilePaths = legacyExport.FilePaths.ToList()
                }
            }
        };
    }

    private static bool NormalizeTree(List<LogGroup> all)
    {
        bool changed = false;
        var hasChildren = all
            .Where(g => g.ParentGroupId != null)
            .Select(g => g.ParentGroupId!)
            .ToHashSet();

        foreach (var group in all)
        {
            var hasChild = hasChildren.Contains(group.Id);
            if (group.Kind == LogGroupKind.Branch)
            {
                // Branches are organizational only, even if currently leaf.
                if (group.FileIds.Count > 0)
                {
                    group.FileIds.Clear();
                    changed = true;
                }
            }
            else
            {
                // Dashboards cannot have children.
                if (hasChild)
                {
                    group.Kind = LogGroupKind.Branch;
                    changed = true;
                    if (group.FileIds.Count > 0)
                    {
                        group.FileIds.Clear();
                        changed = true;
                    }
                }
            }
        }

        return changed;
    }
}
