namespace LogReader.Core.Models;

using System.Collections.Immutable;

public sealed record ConfiguredLogTreeRequest(
    string? RootGroupId = null,
    int MaxDepth = ConfiguredLogLimits.DefaultTreeMaxDepth,
    int MaxNodes = ConfiguredLogLimits.DefaultTreeMaxNodes,
    int StartIndex = 0);

public sealed record ConfiguredLogTreeNode(
    string Id,
    ConfiguredLogTargetKind Kind,
    string DisplayName,
    string TreePath,
    string? ParentId,
    int Depth,
    bool HasChildren);

public sealed class ConfiguredLogTreeResult
{
    public ConfiguredLogTreeResult(
        string catalogRevision,
        IEnumerable<ConfiguredLogTreeNode>? nodes,
        IEnumerable<ConfiguredLogRequestError>? errors,
        int totalNodeCount,
        int? nextStartIndex,
        bool depthTruncated)
    {
        CatalogRevision = catalogRevision;
        Nodes = (nodes ?? Enumerable.Empty<ConfiguredLogTreeNode>())
            .Select(static node => node with { })
            .ToImmutableArray();
        Errors = (errors ?? Enumerable.Empty<ConfiguredLogRequestError>())
            .Select(static error => error with { })
            .ToImmutableArray();
        TotalNodeCount = totalNodeCount;
        NextStartIndex = nextStartIndex;
        DepthTruncated = depthTruncated;
    }

    public string CatalogRevision { get; }

    public ImmutableArray<ConfiguredLogTreeNode> Nodes { get; }

    public ImmutableArray<ConfiguredLogRequestError> Errors { get; }

    public int TotalNodeCount { get; }

    public int? NextStartIndex { get; }

    public bool DepthTruncated { get; }

    public bool IsSuccess => Errors.IsEmpty;

    public bool IsTruncated => NextStartIndex.HasValue || DepthTruncated;
}
