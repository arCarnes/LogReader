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

    public async Task ExportGroupAsync(string groupId, string exportPath)
    {
        var group = await GetByIdAsync(groupId);
        if (group == null) throw new InvalidOperationException($"Group {groupId} not found");

        var allGroups = await GetAllAsync();
        var resolvedFileIds = ResolveFileIdsRecursive(allGroups, groupId);

        var allFiles = await _fileRepo.GetAllAsync();
        var filePaths = allFiles
            .Where(f => resolvedFileIds.Contains(f.Id))
            .Select(f => f.FilePath)
            .ToList();

        var export = new GroupExport
        {
            GroupName = group.Name,
            FilePaths = filePaths,
            ExportedAt = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(export, JsonStore.GetOptions());
        await File.WriteAllTextAsync(exportPath, json);
    }

    public async Task<GroupExport?> ImportGroupAsync(string importPath)
    {
        if (!File.Exists(importPath)) return null;
        var json = await File.ReadAllTextAsync(importPath);
        return JsonSerializer.Deserialize<GroupExport>(json, JsonStore.GetOptions());
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

    private static HashSet<string> ResolveFileIdsRecursive(List<LogGroup> all, string groupId)
    {
        var result = new HashSet<string>();
        var group = all.FirstOrDefault(g => g.Id == groupId);
        if (group == null) return result;
        result.UnionWith(group.FileIds);
        foreach (var child in all.Where(g => g.ParentGroupId == groupId))
            result.UnionWith(ResolveFileIdsRecursive(all, child.Id));
        return result;
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
                // Repair data written by the earlier "leaf=>dashboard" normalization bug.
                else if (group.ParentGroupId == null &&
                         string.Equals(group.Name, "New Branch", StringComparison.Ordinal) &&
                         group.FileIds.Count == 0)
                {
                    group.Kind = LogGroupKind.Branch;
                    changed = true;
                }
            }
        }

        return changed;
    }
}
