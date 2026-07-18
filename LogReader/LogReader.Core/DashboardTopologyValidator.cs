namespace LogReader.Core;

using System.IO;
using LogReader.Core.Models;

public static class DashboardTopologyValidator
{
    public static void ValidatePersistedGroups(IReadOnlyList<LogGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        for (var index = 0; index < groups.Count; index++)
        {
            var group = groups[index];
            if (group == null)
                throw new InvalidDataException($"The saved dashboard view contains a null group at index {index}.");
            if (group.FileIds == null)
                throw new InvalidDataException($"Group '{group.Id}' in the saved dashboard view has a null file ID collection.");
            if (!Enum.IsDefined(group.Kind))
                throw new InvalidDataException($"Group '{group.Id}' in the saved dashboard view has an invalid group kind.");
        }

        ValidateNodes(
            groups.Select(group => new DashboardNode(
                group.Id,
                group.Name,
                group.ParentGroupId,
                group.Kind,
                group.FileIds.Count,
                HasBlankMembership(group.FileIds),
                HasDuplicateMembership(group.FileIds, StringComparer.Ordinal))),
            "saved dashboard view",
            "file IDs");
    }

    public static void ValidateImportedView(ViewExport export)
    {
        ArgumentNullException.ThrowIfNull(export);
        if (export.SchemaVersion != ViewExport.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"The imported dashboard view uses unsupported schema version {export.SchemaVersion}. " +
                $"This version of WeezTail supports schema version {ViewExport.CurrentSchemaVersion}.");
        }

        if (export.Groups == null)
            throw new InvalidDataException("The imported dashboard view has a null group collection.");

        for (var index = 0; index < export.Groups.Count; index++)
        {
            var group = export.Groups[index];
            if (group == null)
                throw new InvalidDataException($"The imported dashboard view contains a null group at index {index}.");
            if (group.FilePaths == null)
                throw new InvalidDataException($"Group '{group.Id}' in the imported dashboard view has a null file path collection.");
            if (!Enum.IsDefined(group.Kind))
                throw new InvalidDataException($"Group '{group.Id}' in the imported dashboard view has an invalid group kind.");

            foreach (var filePath in group.FilePaths)
            {
                if (string.IsNullOrWhiteSpace(filePath))
                    continue;

                try
                {
                    _ = Path.GetFullPath(filePath);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    throw new InvalidDataException(
                        $"Group '{group.Id}' in the imported dashboard view contains an invalid file path.",
                        ex);
                }
            }
        }

        ValidateNodes(
            export.Groups
            .Select(group => new DashboardNode(
                group.Id,
                group.Name,
                group.ParentGroupId,
                group.Kind,
                group.FilePaths.Count,
                HasBlankMembership(group.FilePaths),
                HasDuplicateMembership(group.FilePaths, StringComparer.OrdinalIgnoreCase))),
            "imported dashboard view",
            "file paths");
    }

    private static void ValidateNodes(
        IEnumerable<DashboardNode> nodes,
        string sourceName,
        string membershipLabel)
    {
        var nodeList = nodes.ToList();
        var nodesById = new Dictionary<string, DashboardNode>(StringComparer.Ordinal);
        foreach (var node in nodeList)
        {
            if (string.IsNullOrWhiteSpace(node.Id))
                throw new InvalidDataException($"The {sourceName} contains a group with a missing ID.");

            if (!nodesById.TryAdd(node.Id, node))
                throw new InvalidDataException($"The {sourceName} contains a duplicate group ID: '{node.Id}'.");

            if (string.IsNullOrWhiteSpace(node.Name))
                throw new InvalidDataException($"Group '{node.Id}' in the {sourceName} is missing a name.");

            if (node.ParentId is { Length: > 0 } && string.IsNullOrWhiteSpace(node.ParentId))
            {
                throw new InvalidDataException(
                    $"Group '{node.Id}' in the {sourceName} has an invalid parent group ID.");
            }

            if (node.HasBlankMembership)
            {
                throw new InvalidDataException(
                    $"Group '{node.Id}' in the {sourceName} contains blank {membershipLabel}.");
            }

            if (node.HasDuplicateMembership)
            {
                throw new InvalidDataException(
                    $"Group '{node.Id}' in the {sourceName} contains duplicate {membershipLabel}.");
            }
        }

        var childCountById = nodeList
            .Where(node => !string.IsNullOrWhiteSpace(node.ParentId))
            .GroupBy(node => node.ParentId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        foreach (var node in nodeList)
        {
            if (!string.IsNullOrWhiteSpace(node.ParentId) && !nodesById.ContainsKey(node.ParentId))
            {
                throw new InvalidDataException(
                    $"Group '{node.Id}' in the {sourceName} references a missing parent group '{node.ParentId}'.");
            }

            if (node.Kind == LogGroupKind.Branch && node.MembershipCount > 0)
            {
                throw new InvalidDataException(
                    $"Branch group '{node.Id}' in the {sourceName} cannot own {membershipLabel}.");
            }

            if (node.Kind == LogGroupKind.Dashboard &&
                childCountById.TryGetValue(node.Id, out var childCount) &&
                childCount > 0)
            {
                throw new InvalidDataException(
                    $"Dashboard group '{node.Id}' in the {sourceName} cannot have child groups.");
            }

            var visited = new HashSet<string>(StringComparer.Ordinal) { node.Id };
            var currentParentId = node.ParentId;
            while (!string.IsNullOrWhiteSpace(currentParentId))
            {
                if (!nodesById.TryGetValue(currentParentId, out var parent))
                    break;

                if (!visited.Add(parent.Id))
                {
                    throw new InvalidDataException(
                        $"Group '{node.Id}' in the {sourceName} participates in a parent cycle.");
                }

                currentParentId = parent.ParentId;
            }
        }
    }

    private static bool HasBlankMembership(IEnumerable<string> memberships)
        => memberships.Any(string.IsNullOrWhiteSpace);

    private static bool HasDuplicateMembership(IEnumerable<string> memberships, StringComparer comparer)
    {
        var seen = new HashSet<string>(comparer);
        foreach (var membership in memberships)
        {
            if (!string.IsNullOrWhiteSpace(membership) && !seen.Add(membership))
                return true;
        }

        return false;
    }

    private sealed record DashboardNode(
        string Id,
        string Name,
        string? ParentId,
        LogGroupKind Kind,
        int MembershipCount,
        bool HasBlankMembership,
        bool HasDuplicateMembership);
}
