namespace LogReader.Core;

using System.Collections.Immutable;
using LogReader.Core.Models;

internal sealed class ConfiguredLogCatalogIndex
{
    private ConfiguredLogCatalogIndex(
        ConfiguredLogCatalogSnapshot snapshot,
        IReadOnlyDictionary<string, ConfiguredLogGroup> groupsById,
        IReadOnlyDictionary<string, ConfiguredLogFile> filesById,
        IReadOnlyDictionary<string, IReadOnlyList<ConfiguredLogGroup>> childrenByParentId,
        IReadOnlyList<ConfiguredLogGroup> orderedGroups,
        IReadOnlyDictionary<string, string> groupPaths,
        IReadOnlyDictionary<string, IReadOnlyList<ConfiguredLogGroup>> memberDashboardsByFileId)
    {
        Snapshot = snapshot;
        GroupsById = groupsById;
        FilesById = filesById;
        ChildrenByParentId = childrenByParentId;
        OrderedGroups = orderedGroups;
        GroupPaths = groupPaths;
        MemberDashboardsByFileId = memberDashboardsByFileId;
    }

    internal ConfiguredLogCatalogSnapshot Snapshot { get; }

    internal IReadOnlyDictionary<string, ConfiguredLogGroup> GroupsById { get; }

    internal IReadOnlyDictionary<string, ConfiguredLogFile> FilesById { get; }

    internal IReadOnlyDictionary<string, IReadOnlyList<ConfiguredLogGroup>> ChildrenByParentId { get; }

    internal IReadOnlyList<ConfiguredLogGroup> OrderedGroups { get; }

    internal IReadOnlyDictionary<string, string> GroupPaths { get; }

    internal IReadOnlyDictionary<string, IReadOnlyList<ConfiguredLogGroup>> MemberDashboardsByFileId { get; }

    internal static bool TryCreate(
        ConfiguredLogCatalogSnapshot snapshot,
        out ConfiguredLogCatalogIndex? index,
        out ConfiguredLogRequestError? error)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var creation = (IndexCreation)snapshot.GetOrCreateCatalogIndexCache(() => Create(snapshot));
        index = creation.Index;
        error = creation.Error;
        return index != null;
    }

    private static IndexCreation Create(ConfiguredLogCatalogSnapshot snapshot)
    {
        try
        {
            ValidateMetadataBounds(snapshot);
            var mutableGroups = snapshot.Groups.Select(group => new LogGroup
            {
                Id = group.Id,
                Name = group.Name,
                SortOrder = group.SortOrder,
                ParentGroupId = group.ParentGroupId,
                Kind = group.Kind,
                FileIds = group.FileIds.IsDefault ? null! : group.FileIds.ToList()
            }).ToList();
            DashboardTopologyValidator.ValidatePersistedGroups(mutableGroups);
            ValidateTopologyDepth(snapshot.Groups);

            var groupsById = snapshot.Groups.ToDictionary(group => group.Id, StringComparer.Ordinal);
            var filesById = new Dictionary<string, ConfiguredLogFile>(StringComparer.Ordinal);
            foreach (var file in snapshot.Files)
            {
                if (string.IsNullOrWhiteSpace(file.Id))
                    throw new InvalidDataException("The configured log catalog contains a file with a missing ID.");
                if (string.IsNullOrWhiteSpace(file.PhysicalPath))
                    throw new InvalidDataException($"Configured log file '{file.Id}' has a missing physical path.");
                if (!filesById.TryAdd(file.Id, file))
                    throw new InvalidDataException($"The configured log catalog contains a duplicate file ID: '{file.Id}'.");
            }

            foreach (var group in snapshot.Groups.Where(group => group.Kind == LogGroupKind.Dashboard))
            {
                foreach (var fileId in group.FileIds)
                {
                    if (!filesById.ContainsKey(fileId))
                    {
                        throw new InvalidDataException(
                            $"Dashboard '{group.Id}' references a missing configured log file '{fileId}'.");
                    }
                }
            }

            var childrenByParentId = snapshot.Groups
                .Where(group => !string.IsNullOrWhiteSpace(group.ParentGroupId))
                .GroupBy(group => group.ParentGroupId!, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<ConfiguredLogGroup>)group
                        .OrderBy(child => child.SortOrder)
                        .ThenBy(child => child.Id, StringComparer.Ordinal)
                        .ToList(),
                    StringComparer.Ordinal);

            var orderedGroups = new List<ConfiguredLogGroup>();
            foreach (var root in snapshot.Groups
                         .Where(group => string.IsNullOrWhiteSpace(group.ParentGroupId))
                         .OrderBy(group => group.SortOrder)
                         .ThenBy(group => group.Id, StringComparer.Ordinal))
            {
                AddPreOrder(root, childrenByParentId, orderedGroups);
            }

            var groupPaths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var group in orderedGroups)
                groupPaths[group.Id] = BuildGroupPath(group, groupsById);

            var memberDashboardsByFileId = new Dictionary<string, IReadOnlyList<ConfiguredLogGroup>>(StringComparer.Ordinal);
            foreach (var group in orderedGroups.Where(group => group.Kind == LogGroupKind.Dashboard))
            {
                foreach (var fileId in group.FileIds)
                {
                    if (!memberDashboardsByFileId.TryGetValue(fileId, out var existing))
                    {
                        memberDashboardsByFileId[fileId] = new List<ConfiguredLogGroup> { group };
                        continue;
                    }

                    ((List<ConfiguredLogGroup>)existing).Add(group);
                }
            }

            var index = new ConfiguredLogCatalogIndex(
                snapshot,
                groupsById,
                filesById,
                childrenByParentId,
                orderedGroups,
                groupPaths,
                memberDashboardsByFileId);
            return new IndexCreation(index, null);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            return new IndexCreation(
                null,
                new ConfiguredLogRequestError(
                    "invalid_catalog",
                    "The configured dashboard catalog is invalid."));
        }
    }

    internal IEnumerable<ConfiguredLogGroup> EnumerateDescendantDashboards(ConfiguredLogGroup folder)
    {
        if (!ChildrenByParentId.TryGetValue(folder.Id, out var children))
            yield break;

        foreach (var child in children)
        {
            if (child.Kind == LogGroupKind.Dashboard)
            {
                yield return child;
                continue;
            }

            foreach (var dashboard in EnumerateDescendantDashboards(child))
                yield return dashboard;
        }
    }

    private static void AddPreOrder(
        ConfiguredLogGroup group,
        IReadOnlyDictionary<string, IReadOnlyList<ConfiguredLogGroup>> childrenByParentId,
        List<ConfiguredLogGroup> destination)
    {
        destination.Add(group);
        if (!childrenByParentId.TryGetValue(group.Id, out var children))
            return;

        foreach (var child in children)
            AddPreOrder(child, childrenByParentId, destination);
    }

    private static string BuildGroupPath(
        ConfiguredLogGroup group,
        IReadOnlyDictionary<string, ConfiguredLogGroup> groupsById)
    {
        var segments = new Stack<string>();
        var current = group;
        while (true)
        {
            segments.Push(current.Name);
            if (string.IsNullOrWhiteSpace(current.ParentGroupId) ||
                !groupsById.TryGetValue(current.ParentGroupId, out current))
            {
                break;
            }
        }

        var path = string.Join(" / ", segments);
        if (path.Length > ConfiguredLogLimits.DefaultMaxTreePathCharacters)
            throw new InvalidDataException("A configured dashboard path is too long.");
        return path;
    }

    private static void ValidateMetadataBounds(ConfiguredLogCatalogSnapshot snapshot)
    {
        if (snapshot.Groups.Length > ConfiguredLogLimits.HardMaxTreeNodes)
            throw new InvalidDataException("The configured dashboard tree contains too many nodes.");
        if (snapshot.Files.Length > ConfiguredLogLimits.HardMaxCatalogFiles)
            throw new InvalidDataException("The configured log catalog contains too many files.");

        var membershipCount = 0L;
        foreach (var group in snapshot.Groups)
        {
            ValidateId(group.Id);
            if (group.Name is { Length: > ConfiguredLogLimits.DefaultMaxNameCharacters })
                throw new InvalidDataException("A configured dashboard name is too long.");
            if (group.ParentGroupId is { Length: > ConfiguredLogLimits.DefaultMaxIdCharacters })
                throw new InvalidDataException("A configured dashboard parent ID is too long.");
            if (!group.FileIds.IsDefault)
            {
                membershipCount += group.FileIds.Length;
                if (membershipCount > ConfiguredLogLimits.HardMaxCatalogMemberships)
                    throw new InvalidDataException("The configured dashboard tree contains too many file memberships.");
                foreach (var fileId in group.FileIds)
                    ValidateId(fileId);
            }
        }

        foreach (var file in snapshot.Files)
        {
            ValidateId(file.Id);
            if (file.PhysicalPath is { Length: > ConfiguredLogLimits.DefaultMaxPhysicalPathCharacters })
                throw new InvalidDataException("A configured log path is too long.");
            if (!string.IsNullOrWhiteSpace(file.PhysicalPath) &&
                Path.GetFileName(file.PhysicalPath).Length > ConfiguredLogLimits.DefaultMaxNameCharacters)
            {
                throw new InvalidDataException("A configured log display name is too long.");
            }
        }

        if (snapshot.DatePathPatterns.Length > ConfiguredLogLimits.DefaultMaxDatePathPatterns)
            throw new InvalidDataException("Too many configured date path patterns were saved.");

        foreach (var pattern in snapshot.DatePathPatterns)
        {
            ValidateId(pattern.Id);
            if (pattern.Name is { Length: > ConfiguredLogLimits.DefaultMaxNameCharacters } ||
                pattern.FindPattern is { Length: > ConfiguredLogLimits.DefaultMaxDatePatternCharacters } ||
                pattern.ReplacePattern is { Length: > ConfiguredLogLimits.DefaultMaxDatePatternCharacters })
            {
                throw new InvalidDataException("A configured date path pattern is too long.");
            }
        }
    }

    private static void ValidateId(string? id)
    {
        if (id is { Length: > ConfiguredLogLimits.DefaultMaxIdCharacters })
            throw new InvalidDataException("A configured ID is too long.");
    }

    private static void ValidateTopologyDepth(ImmutableArray<ConfiguredLogGroup> groups)
    {
        var byId = groups.ToDictionary(group => group.Id, StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var depth = 0;
            var current = group;
            while (!string.IsNullOrWhiteSpace(current.ParentGroupId) &&
                   byId.TryGetValue(current.ParentGroupId, out current))
            {
                depth++;
                if (depth > ConfiguredLogLimits.HardMaxTreeDepth)
                    throw new InvalidDataException("The configured dashboard tree is too deep.");
            }
        }
    }

    private sealed record IndexCreation(
        ConfiguredLogCatalogIndex? Index,
        ConfiguredLogRequestError? Error);
}
