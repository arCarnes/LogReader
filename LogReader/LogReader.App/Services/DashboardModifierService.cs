namespace LogReader.App.Services;

using System.IO;
using LogReader.App.ViewModels;
using LogReader.Core;
using LogReader.Core.Models;

internal sealed record DashboardModifierRefreshSnapshot(
    IReadOnlyDictionary<string, IReadOnlyList<ResolvedModifierMember>> DashboardMembers,
    IReadOnlyList<ResolvedModifierMember> AdHocMembers,
    IReadOnlySet<string> ModifiedPaths);

internal sealed class DashboardModifierService
{
    private readonly Dictionary<string, AppliedDashboardModifierState> _dashboardModifiers = new(StringComparer.Ordinal);
    private AppliedAdHocModifierState? _adHocModifier;

    public bool HasActiveModifiers => _dashboardModifiers.Count > 0 || _adHocModifier != null;

    public bool HasDashboardModifier(string dashboardId)
        => _dashboardModifiers.ContainsKey(dashboardId);

    public bool HasAdHocModifier()
        => _adHocModifier != null;

    public string? GetDashboardModifierLabel(string dashboardId)
        => _dashboardModifiers.TryGetValue(dashboardId, out var modifier)
            ? modifier.Label
            : null;

    public string? GetAdHocModifierLabel()
        => _adHocModifier?.Label;

    public bool TryGetDashboardEffectivePaths(string dashboardId, out IReadOnlySet<string> effectivePaths)
    {
        if (_dashboardModifiers.TryGetValue(dashboardId, out var modifier))
        {
            effectivePaths = modifier.EffectivePaths;
            return true;
        }

        effectivePaths = EmptyPathSet.Instance;
        return false;
    }

    public bool TryGetAdHocEffectivePaths(out IReadOnlySet<string> effectivePaths)
    {
        if (_adHocModifier != null)
        {
            effectivePaths = _adHocModifier.EffectivePaths;
            return true;
        }

        effectivePaths = EmptyPathSet.Instance;
        return false;
    }

    public bool IsManagedByActiveModifier(string filePath)
    {
        foreach (var modifier in _dashboardModifiers.Values)
        {
            if (modifier.EffectivePaths.Contains(filePath))
                return true;
        }

        return _adHocModifier?.EffectivePaths.Contains(filePath) == true;
    }

    public string? FindDashboardForModifierPath(string filePath)
    {
        foreach (var (dashboardId, modifier) in _dashboardModifiers)
        {
            if (modifier.EffectivePaths.Contains(filePath))
                return dashboardId;
        }

        return null;
    }

    public bool IsAdHocModifierPath(string filePath)
        => _adHocModifier?.EffectivePaths.Contains(filePath) == true;

    public IReadOnlyList<string> GetAdHocBasePathsSnapshot()
        => _adHocModifier?.BasePaths ?? Array.Empty<string>();

    public IReadOnlyList<string> GetDashboardOpenTargets(string dashboardId)
    {
        if (!_dashboardModifiers.TryGetValue(dashboardId, out var modifier))
            return Array.Empty<string>();

        return modifier.Members
            .Where(member => member.ErrorMessage == null && !string.IsNullOrWhiteSpace(member.EffectivePath))
            .Select(member => member.EffectivePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void SetDashboardModifier(string dashboardId, int daysBack, IReadOnlyList<ReplacementPattern> patterns)
    {
        _dashboardModifiers[dashboardId] = new AppliedDashboardModifierState(daysBack, ClonePatterns(patterns));
    }

    public bool ClearDashboardModifier(string dashboardId)
        => _dashboardModifiers.Remove(dashboardId);

    public void SetAdHocModifier(int daysBack, IReadOnlyList<ReplacementPattern> patterns, IReadOnlyList<string> basePaths)
    {
        _adHocModifier = new AppliedAdHocModifierState(daysBack, ClonePatterns(patterns), basePaths);
    }

    public bool ClearAdHocModifier()
    {
        if (_adHocModifier == null)
            return false;

        _adHocModifier = null;
        return true;
    }

    public void PruneModifierState(IEnumerable<LogGroupViewModel> groups)
    {
        var validDashboardIds = groups
            .Where(group => group.Kind == LogGroupKind.Dashboard)
            .Select(group => group.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var dashboardId in _dashboardModifiers.Keys.ToList())
        {
            if (!validDashboardIds.Contains(dashboardId))
                _dashboardModifiers.Remove(dashboardId);
        }
    }

    public DashboardModifierRefreshSnapshot ResolveRefreshSnapshot(
        IReadOnlyCollection<LogGroupViewModel> groups,
        IReadOnlyDictionary<string, string> fileIdToPath)
    {
        var resolvedByDashboard = ResolveDashboardModifierMembers(groups, fileIdToPath);
        var adHocMembers = ResolveAdHocModifierMembers();
        var modifiedPaths = resolvedByDashboard.Values
            .SelectMany(members => members)
            .Where(member => member.ErrorMessage == null && !string.IsNullOrWhiteSpace(member.EffectivePath))
            .Select(member => member.EffectivePath)
            .Concat(adHocMembers
                .Where(member => member.ErrorMessage == null && !string.IsNullOrWhiteSpace(member.EffectivePath))
                .Select(member => member.EffectivePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new DashboardModifierRefreshSnapshot(resolvedByDashboard, adHocMembers, modifiedPaths);
    }

    public void SyncModifierLabels(IEnumerable<LogGroupViewModel> groups)
    {
        foreach (var group in groups)
        {
            group.ModifierLabel = group.Kind == LogGroupKind.Dashboard &&
                                  _dashboardModifiers.TryGetValue(group.Id, out var modifier)
                ? modifier.Label
                : string.Empty;
        }
    }

    public static IReadOnlyList<GroupFileMemberViewModel> BuildModifierMemberViewModels(
        IReadOnlyList<ResolvedModifierMember> resolvedMembers,
        IReadOnlyDictionary<string, LogTabViewModel> openTabsByPath,
        IReadOnlyDictionary<string, DashboardFileProbeResult> pathStatusByPath,
        string? selectedFilePath,
        bool showFullPath)
    {
        var memberViewModels = new List<GroupFileMemberViewModel>(resolvedMembers.Count);
        foreach (var member in resolvedMembers)
        {
            openTabsByPath.TryGetValue(member.EffectivePath, out var openTab);
            var effectivePath = openTab?.FilePath ?? member.EffectivePath;
            var errorMessage = member.ErrorMessage;
            if (errorMessage == null && openTab == null)
            {
                if (!pathStatusByPath.TryGetValue(member.EffectivePath, out var pathStatus))
                    pathStatus = DashboardFileProbeResult.Missing;

                errorMessage = pathStatus.ErrorMessage;
            }

            memberViewModels.Add(new GroupFileMemberViewModel(
                member.BaseKey,
                openTab?.FileName ?? Path.GetFileName(effectivePath),
                effectivePath,
                showFullPath,
                errorMessage,
                isSelected: string.Equals(effectivePath, selectedFilePath, StringComparison.OrdinalIgnoreCase),
                fileSizeText: openTab == null ? null : GroupFileMemberViewModel.CreateFileSizeText(openTab)));
        }

        return memberViewModels;
    }

    private Dictionary<string, IReadOnlyList<ResolvedModifierMember>> ResolveDashboardModifierMembers(
        IReadOnlyCollection<LogGroupViewModel> groups,
        IReadOnlyDictionary<string, string> fileIdToPath)
    {
        var resolvedByDashboard = new Dictionary<string, IReadOnlyList<ResolvedModifierMember>>(StringComparer.Ordinal);
        foreach (var (dashboardId, modifier) in _dashboardModifiers)
        {
            var dashboard = groups.FirstOrDefault(group =>
                group.Kind == LogGroupKind.Dashboard &&
                string.Equals(group.Id, dashboardId, StringComparison.Ordinal));
            if (dashboard == null)
            {
                modifier.UpdateMembers(Array.Empty<ResolvedModifierMember>());
                continue;
            }

            var members = new List<ResolvedModifierMember>();
            foreach (var fileId in dashboard.Model.FileIds)
            {
                if (!fileIdToPath.TryGetValue(fileId, out var basePath) || string.IsNullOrWhiteSpace(basePath))
                {
                    members.Add(new ResolvedModifierMember(fileId, string.Empty, "Base file path is missing."));
                    continue;
                }

                if (!TryTransformPath(basePath, modifier.Patterns, modifier.TargetDate, out var effectivePath, out var errorMessage))
                {
                    members.Add(new ResolvedModifierMember(fileId, basePath, errorMessage));
                    continue;
                }

                members.Add(new ResolvedModifierMember(fileId, effectivePath, ErrorMessage: null));
            }

            modifier.UpdateMembers(members);
            resolvedByDashboard[dashboardId] = members;
        }

        return resolvedByDashboard;
    }

    private IReadOnlyList<ResolvedModifierMember> ResolveAdHocModifierMembers()
    {
        if (_adHocModifier == null)
            return Array.Empty<ResolvedModifierMember>();

        var members = new List<ResolvedModifierMember>();
        foreach (var basePath in _adHocModifier.BasePaths)
        {
            if (!TryTransformPath(basePath, _adHocModifier.Patterns, _adHocModifier.TargetDate, out var effectivePath, out var errorMessage))
            {
                members.Add(new ResolvedModifierMember(basePath, basePath, errorMessage));
                continue;
            }

            members.Add(new ResolvedModifierMember(basePath, effectivePath, ErrorMessage: null));
        }

        _adHocModifier.UpdateMembers(members);
        return members;
    }

    private static bool TryTransformPath(
        string basePath,
        IReadOnlyList<ReplacementPattern> patterns,
        DateTime targetDate,
        out string effectivePath,
        out string? errorMessage)
    {
        effectivePath = basePath;
        if (string.IsNullOrWhiteSpace(basePath))
        {
            errorMessage = "Base file path is missing.";
            return false;
        }

        if (patterns.Count == 0)
        {
            errorMessage = "No date rolling patterns are configured.";
            return false;
        }

        string? firstError = null;
        string? firstTransformedPath = null;
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern.FindPattern))
            {
                firstError ??= "Find pattern is empty.";
                continue;
            }

            if (!ReplacementTokenParser.TryExpand(pattern.ReplacePattern, targetDate, out var expandedReplace, out var expandError))
            {
                firstError ??= expandError ?? "Pattern expansion failed.";
                continue;
            }

            if (basePath.IndexOf(pattern.FindPattern, StringComparison.OrdinalIgnoreCase) < 0)
            {
                firstError ??= "Pattern did not match path.";
                continue;
            }

            var candidatePath = basePath.Replace(pattern.FindPattern, expandedReplace, StringComparison.OrdinalIgnoreCase);
            firstTransformedPath ??= candidatePath;

            if (File.Exists(candidatePath))
            {
                effectivePath = candidatePath;
                errorMessage = null;
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(firstTransformedPath))
        {
            effectivePath = firstTransformedPath;
            errorMessage = null;
            return true;
        }

        errorMessage = firstError ?? "Pattern did not match path.";
        return false;
    }

    private static IReadOnlyList<ReplacementPattern> ClonePatterns(IReadOnlyList<ReplacementPattern> patterns)
        => patterns
            .Select(pattern => new ReplacementPattern
            {
                Id = pattern.Id,
                Name = pattern.Name,
                FindPattern = pattern.FindPattern,
                ReplacePattern = pattern.ReplacePattern
            })
            .ToList();
}

internal class AppliedDashboardModifierState
{
    public AppliedDashboardModifierState(int daysBack, IReadOnlyList<ReplacementPattern> patterns)
    {
        DaysBack = daysBack;
        Patterns = patterns;
    }

    public int DaysBack { get; }

    public IReadOnlyList<ReplacementPattern> Patterns { get; }

    public string Label => $"T-{DaysBack}";

    public DateTime TargetDate => DateTime.Today.AddDays(-DaysBack);

    public IReadOnlyList<ResolvedModifierMember> Members { get; private set; } = Array.Empty<ResolvedModifierMember>();

    public IReadOnlySet<string> EffectivePaths { get; private set; } = EmptyPathSet.Instance;

    public void UpdateMembers(IReadOnlyList<ResolvedModifierMember> members)
    {
        Members = members;
        EffectivePaths = members
            .Where(member => member.ErrorMessage == null && !string.IsNullOrWhiteSpace(member.EffectivePath))
            .Select(member => member.EffectivePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}

internal sealed class AppliedAdHocModifierState : AppliedDashboardModifierState
{
    public AppliedAdHocModifierState(int daysBack, IReadOnlyList<ReplacementPattern> patterns, IReadOnlyList<string> basePaths)
        : base(daysBack, patterns)
    {
        BasePaths = basePaths;
    }

    public IReadOnlyList<string> BasePaths { get; }
}

internal sealed record ResolvedModifierMember(string BaseKey, string EffectivePath, string? ErrorMessage);

internal static class EmptyPathSet
{
    public static IReadOnlySet<string> Instance { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
