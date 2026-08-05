namespace LogReader.Core;

using LogReader.Core.Models;

public sealed class ConfiguredLogTreeProjector
{
    public ConfiguredLogTreeResult Project(
        ConfiguredLogCatalogSnapshot snapshot,
        ConfiguredLogTreeRequest request)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);

        var requestErrors = ValidateRequest(request);
        if (requestErrors.Count > 0)
            return Rejected(snapshot.Revision, requestErrors);

        if (!ConfiguredLogCatalogIndex.TryCreate(snapshot, out var index, out var catalogError))
            return Rejected(snapshot.Revision, [catalogError!]);

        IReadOnlyList<ConfiguredLogGroup> roots;
        if (!string.IsNullOrWhiteSpace(request.RootGroupId))
        {
            if (!index!.GroupsById.TryGetValue(request.RootGroupId, out var root))
            {
                var code = index.FilesById.ContainsKey(request.RootGroupId)
                    ? "target_kind_mismatch"
                    : "unknown_target";
                return Rejected(
                    snapshot.Revision,
                    [new ConfiguredLogRequestError(
                        code,
                        "The requested tree root is not a current folder or dashboard.",
                        request.RootGroupId)]);
            }

            roots = [root];
        }
        else
        {
            roots = index!.OrderedGroups
                .Where(group => string.IsNullOrWhiteSpace(group.ParentGroupId))
                .ToList();
        }

        var page = new List<ConfiguredLogTreeNode>(request.MaxNodes);
        var totalNodeCount = 0;
        var depthTruncated = false;
        var responseCharacterCount = 0;
        var responseBudgetTruncated = false;
        foreach (var root in roots)
        {
            VisitGroup(
                index,
                root,
                parentId: null,
                depth: 0,
                request,
                page,
                ref totalNodeCount,
                ref depthTruncated,
                ref responseCharacterCount,
                ref responseBudgetTruncated);
        }

        int? nextStartIndex = request.StartIndex + page.Count < totalNodeCount
            ? request.StartIndex + page.Count
            : null;
        return new ConfiguredLogTreeResult(
            snapshot.Revision,
            page,
            errors: null,
            totalNodeCount,
            nextStartIndex,
            depthTruncated,
            responseBudgetTruncated);
    }

    private static List<ConfiguredLogRequestError> ValidateRequest(ConfiguredLogTreeRequest request)
    {
        var errors = new List<ConfiguredLogRequestError>();
        if (request.MaxDepth is < 0 or > ConfiguredLogLimits.DefaultTreeMaxDepth)
        {
            errors.Add(new ConfiguredLogRequestError(
                "invalid_tree_depth",
                $"maxDepth must be between 0 and {ConfiguredLogLimits.DefaultTreeMaxDepth}."));
        }

        if (request.MaxNodes is < 1 or > ConfiguredLogLimits.DefaultTreeMaxNodes)
        {
            errors.Add(new ConfiguredLogRequestError(
                "invalid_tree_node_limit",
                $"maxNodes must be between 1 and {ConfiguredLogLimits.DefaultTreeMaxNodes}."));
        }

        if (request.StartIndex < 0)
        {
            errors.Add(new ConfiguredLogRequestError(
                "invalid_tree_continuation",
                "The tree continuation position cannot be negative."));
        }

        if (request.RootGroupId is { Length: > ConfiguredLogLimits.DefaultMaxIdCharacters })
        {
            errors.Add(new ConfiguredLogRequestError(
                "invalid_target_id",
                $"Configured target IDs cannot exceed {ConfiguredLogLimits.DefaultMaxIdCharacters} characters."));
        }

        return errors;
    }

    private static void VisitGroup(
        ConfiguredLogCatalogIndex index,
        ConfiguredLogGroup group,
        string? parentId,
        int depth,
        ConfiguredLogTreeRequest request,
        List<ConfiguredLogTreeNode> page,
        ref int totalNodeCount,
        ref bool depthTruncated,
        ref int responseCharacterCount,
        ref bool responseBudgetTruncated)
    {
        var hasChildren = group.Kind == LogGroupKind.Dashboard
            ? !group.FileIds.IsEmpty
            : index.ChildrenByParentId.TryGetValue(group.Id, out var children) && children.Count > 0;
        AddToPage(
            new ConfiguredLogTreeNode(
                group.Id,
                group.Kind == LogGroupKind.Branch
                    ? ConfiguredLogTargetKind.Folder
                    : ConfiguredLogTargetKind.Dashboard,
                group.Name,
                index.GroupPaths[group.Id],
                parentId,
                depth,
                hasChildren),
            request,
            page,
            ref totalNodeCount,
            ref responseCharacterCount,
            ref responseBudgetTruncated);

        if (depth >= request.MaxDepth)
        {
            depthTruncated |= hasChildren;
            return;
        }

        if (group.Kind == LogGroupKind.Dashboard)
        {
            foreach (var fileId in group.FileIds)
            {
                var file = index.FilesById[fileId];
                var displayName = Path.GetFileName(file.PhysicalPath);
                if (string.IsNullOrWhiteSpace(displayName))
                    displayName = file.Id;
                AddToPage(
                    new ConfiguredLogTreeNode(
                        file.Id,
                        ConfiguredLogTargetKind.LogFile,
                        displayName,
                        $"{index.GroupPaths[group.Id]} / {displayName}",
                        group.Id,
                        depth + 1,
                        HasChildren: false),
                    request,
                    page,
                    ref totalNodeCount,
                    ref responseCharacterCount,
                    ref responseBudgetTruncated);
            }

            return;
        }

        if (!index.ChildrenByParentId.TryGetValue(group.Id, out var childGroups))
            return;

        foreach (var child in childGroups)
        {
            VisitGroup(
                index,
                child,
                group.Id,
                depth + 1,
                request,
                page,
                ref totalNodeCount,
                ref depthTruncated,
                ref responseCharacterCount,
                ref responseBudgetTruncated);
        }
    }

    private static void AddToPage(
        ConfiguredLogTreeNode node,
        ConfiguredLogTreeRequest request,
        List<ConfiguredLogTreeNode> page,
        ref int totalNodeCount,
        ref int responseCharacterCount,
        ref bool responseBudgetTruncated)
    {
        if (totalNodeCount >= request.StartIndex &&
            page.Count < request.MaxNodes &&
            !responseBudgetTruncated)
        {
            var nodeCharacters = 256 +
                                 node.Id.Length +
                                 node.DisplayName.Length +
                                 node.TreePath.Length +
                                 (node.ParentId?.Length ?? 0);
            if (responseCharacterCount + nodeCharacters <= ConfiguredLogLimits.DefaultTreeResponseCharacters)
            {
                page.Add(node);
                responseCharacterCount += nodeCharacters;
            }
            else
            {
                responseBudgetTruncated = true;
            }
        }

        totalNodeCount++;
    }

    private static ConfiguredLogTreeResult Rejected(
        string catalogRevision,
        IEnumerable<ConfiguredLogRequestError> errors)
        => new(
            catalogRevision,
            nodes: null,
            errors,
            totalNodeCount: 0,
            nextStartIndex: null,
            depthTruncated: false,
            responseBudgetTruncated: false);
}
