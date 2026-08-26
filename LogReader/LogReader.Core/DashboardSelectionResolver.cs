namespace LogReader.Core;

using System.Collections.Immutable;
using LogReader.Core.Models;

public sealed class DashboardSelectionResolver
{
    public ConfiguredLogSelectionResult Resolve(
        ConfiguredLogCatalogSnapshot snapshot,
        ConfiguredLogSelectionRequest request,
        IConfiguredLogPathCandidateSelector? pathCandidateSelector = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);
        pathCandidateSelector ??= FirstPathCandidateSelector.Instance;

        var requestErrors = ValidateRequest(request);
        if (requestErrors.Count > 0)
            return CreateRejectedResult(snapshot.Revision, request, requestErrors);

        if (!ConfiguredLogCatalogIndex.TryCreate(snapshot, out var index, out var catalogError))
            return CreateRejectedResult(snapshot.Revision, request, [catalogError!]);

        var validatedTargets = new List<ValidatedTarget>(request.Targets.Length);
        foreach (var target in request.Targets)
        {
            if (!TryValidateTarget(index!, target, out var validated, out var targetError))
                requestErrors.Add(targetError!);
            else
                validatedTargets.Add(validated!);
        }

        if (requestErrors.Count > 0)
            return CreateRejectedResult(snapshot.Revision, request, requestErrors);

        var pendingByFileId = new Dictionary<string, MutablePendingFile>(StringComparer.Ordinal);
        var pendingInOrder = new List<MutablePendingFile>();
        var expansionBudget = new ExpansionBudget(
            request.MaxExpandedStableFiles,
            ConfiguredLogLimits.HardMaxCatalogMemberships);

        foreach (var target in validatedTargets)
        {
            ct.ThrowIfCancellationRequested();
            switch (target.Target.Kind)
            {
                case ConfiguredLogTargetKind.Folder:
                    foreach (var dashboard in index!.EnumerateDescendantDashboards(target.Group!))
                    {
                        AddPendingDashboardFiles(index, target.Target, dashboard, pendingByFileId, pendingInOrder, expansionBudget, ct);
                        if (expansionBudget.IsExceeded)
                            break;
                    }
                    break;

                case ConfiguredLogTargetKind.Dashboard:
                    AddPendingDashboardFiles(index!, target.Target, target.Group!, pendingByFileId, pendingInOrder, expansionBudget, ct);
                    break;

                case ConfiguredLogTargetKind.LogFile:
                    foreach (var dashboard in index!.MemberDashboardsByFileId[target.File!.Id])
                    {
                        AddPendingFile(index, target.Target, dashboard, target.File, pendingByFileId, pendingInOrder, expansionBudget);
                    }
                    break;
            }

            if (expansionBudget.IsExceeded)
                break;
        }

        if (expansionBudget.IsExceeded)
        {
            var candidateLimitExceeded = expansionBudget.StableFileCount > request.MaxExpandedStableFiles;
            return new ConfiguredLogSelectionResult(
                snapshot.Revision,
                files: null,
                errors: [candidateLimitExceeded
                    ? new ConfiguredLogRequestError(
                        "search_candidate_limit_exceeded",
                        $"Target expansion exceeded the supported limit of {request.MaxExpandedStableFiles} configured file candidates.")
                    : new ConfiguredLogRequestError(
                        "configured_expansion_limit_exceeded",
                        "Target expansion exceeded the bounded configured-file provenance limit.")],
                fileErrors: null,
                new ConfiguredLogSelectionSummary(
                    request.Targets.Length,
                    expansionBudget.StableFileCount,
                    0,
                    0,
                    request.MaxTargets,
                    request.MaxResolvedFiles,
                    RejectedByLimit: true));
        }

        var startIndex = request.Continuation?.NextStableFileIndex ?? 0;
        if (startIndex < 0 || startIndex > pendingInOrder.Count ||
            request.Continuation is { SeenPhysicalPathIdentities.Length: > ConfiguredLogLimits.HardMaxCatalogFiles })
        {
            return CreateRejectedResult(
                snapshot.Revision,
                request,
                [new ConfiguredLogRequestError("invalid_selection_continuation", "The configured selection continuation is invalid.")]);
        }

        var seenIdentities = request.Continuation?.SeenPhysicalPathIdentities.ToHashSet(StringComparer.Ordinal) ??
                             new HashSet<string>(StringComparer.Ordinal);
        var pageByPath = new Dictionary<string, MutableSelectedFile>(StringComparer.OrdinalIgnoreCase);
        var pageFiles = new List<MutableSelectedFile>();
        var pageErrors = new List<ConfiguredLogFileError>();
        var processed = 0;
        while (startIndex + processed < pendingInOrder.Count && processed < request.MaxResolvedFiles)
        {
            ct.ThrowIfCancellationRequested();
            var pending = pendingInOrder[startIndex + processed];
            processed++;
            if (!TryResolvePendingFile(index!, request, pathCandidateSelector, pending, out var selected, out var fileError))
            {
                pageErrors.Add(fileError!);
                continue;
            }

            var pathIdentity = CreatePhysicalPathIdentity(selected!.PhysicalPath);
            if (pageByPath.TryGetValue(selected.PhysicalPath, out var existing))
            {
                existing.Merge(selected);
                continue;
            }

            if (seenIdentities.Contains(pathIdentity))
                continue;

            pageByPath.Add(selected.PhysicalPath, selected);
            pageFiles.Add(selected);
            seenIdentities.Add(pathIdentity);
        }

        var nextIndex = startIndex + processed;
        var continuation = nextIndex < pendingInOrder.Count
            ? new ConfiguredLogSelectionContinuation(nextIndex, seenIdentities.Order(StringComparer.Ordinal).ToImmutableArray())
            : null;
        var remaining = Math.Max(0, pendingInOrder.Count - nextIndex);

        return new ConfiguredLogSelectionResult(
            snapshot.Revision,
            pageFiles.Select(file => file.ToContract()),
            errors: null,
            pageErrors,
            new ConfiguredLogSelectionSummary(
                request.Targets.Length,
                pendingInOrder.Count,
                pageFiles.Count,
                pageErrors.Count,
                request.MaxTargets,
                request.MaxResolvedFiles,
                RejectedByLimit: false)
            {
                PageCandidateCount = processed,
                RemainingCandidateCount = remaining
            },
            continuation);
    }

    private static List<ConfiguredLogRequestError> ValidateRequest(ConfiguredLogSelectionRequest request)
    {
        var errors = new List<ConfiguredLogRequestError>();
        if (request.MaxTargets is < 1 or > ConfiguredLogLimits.DefaultMaxTargets)
        {
            errors.Add(new ConfiguredLogRequestError(
                "invalid_target_limit",
                $"maxTargets must be between 1 and {ConfiguredLogLimits.DefaultMaxTargets}."));
        }

        if (request.MaxResolvedFiles is < 1 or > ConfiguredLogLimits.DefaultMaxResolvedFiles)
        {
            errors.Add(new ConfiguredLogRequestError(
                "invalid_file_limit",
                $"maxResolvedFiles must be between 1 and {ConfiguredLogLimits.DefaultMaxResolvedFiles}."));
        }

        if (request.MaxExpandedStableFiles is < 1 or > ConfiguredLogLimits.DefaultMaxSearchCandidates)
        {
            errors.Add(new ConfiguredLogRequestError(
                "invalid_search_candidate_limit",
                $"maxExpandedStableFiles must be between 1 and {ConfiguredLogLimits.DefaultMaxSearchCandidates}."));
        }

        if (request.Targets.IsEmpty)
        {
            errors.Add(new ConfiguredLogRequestError(
                "targets_required",
                "At least one configured folder, dashboard, or log-file target is required."));
        }
        else if (request.Targets.Length > Math.Max(0, request.MaxTargets))
        {
            errors.Add(new ConfiguredLogRequestError(
                "target_limit_exceeded",
                $"The request contains {request.Targets.Length} targets, exceeding the effective limit of {request.MaxTargets}."));
        }

        if (request.DateOffsetDays < 0)
        {
            errors.Add(new ConfiguredLogRequestError(
                "invalid_date_offset",
                "dateOffsetDays cannot be negative."));
        }
        else
        {
            try
            {
                _ = request.ReferenceDate.AddDays(-request.DateOffsetDays);
            }
            catch (ArgumentOutOfRangeException)
            {
                errors.Add(new ConfiguredLogRequestError(
                    "invalid_date_offset",
                    "dateOffsetDays is outside the supported calendar range."));
            }
        }

        return errors;
    }

    private static bool TryValidateTarget(
        ConfiguredLogCatalogIndex index,
        ConfiguredLogTarget target,
        out ValidatedTarget? validated,
        out ConfiguredLogRequestError? error)
    {
        validated = null;
        error = null;
        if (!Enum.IsDefined(target.Kind) || string.IsNullOrWhiteSpace(target.Id))
        {
            error = new ConfiguredLogRequestError(
                "invalid_target",
                "Each target must contain a supported kind and a non-empty configured ID.",
                target.Id,
                Enum.IsDefined(target.Kind) ? target.Kind : null);
            return false;
        }

        if (target.Id.Length > ConfiguredLogLimits.DefaultMaxIdCharacters)
        {
            error = new ConfiguredLogRequestError(
                "invalid_target_id",
                $"Configured target IDs cannot exceed {ConfiguredLogLimits.DefaultMaxIdCharacters} characters.");
            return false;
        }

        if (target.Kind == ConfiguredLogTargetKind.LogFile)
        {
            if (index.FilesById.TryGetValue(target.Id, out var file))
            {
                if (!index.MemberDashboardsByFileId.ContainsKey(file.Id))
                {
                    error = new ConfiguredLogRequestError(
                        "file_not_dashboard_member",
                        "The configured log file is not a current member of any dashboard.",
                        target.Id,
                        target.Kind);
                    return false;
                }

                validated = new ValidatedTarget(target, null, file);
                return true;
            }

            error = index.GroupsById.ContainsKey(target.Id)
                ? KindMismatch(target)
                : UnknownTarget(target);
            return false;
        }

        if (index.GroupsById.TryGetValue(target.Id, out var group))
        {
            var expectedKind = group.Kind == LogGroupKind.Branch
                ? ConfiguredLogTargetKind.Folder
                : ConfiguredLogTargetKind.Dashboard;
            if (target.Kind != expectedKind)
            {
                error = KindMismatch(target);
                return false;
            }

            validated = new ValidatedTarget(target, group, null);
            return true;
        }

        error = index.FilesById.ContainsKey(target.Id)
            ? KindMismatch(target)
            : UnknownTarget(target);
        return false;
    }

    private static ConfiguredLogRequestError UnknownTarget(ConfiguredLogTarget target)
        => new(
            "unknown_target",
            "The configured target no longer exists.",
            target.Id,
            target.Kind);

    private static ConfiguredLogRequestError KindMismatch(ConfiguredLogTarget target)
        => new(
            "target_kind_mismatch",
            "The configured target exists, but its kind does not match the request.",
            target.Id,
            target.Kind);

    private static void AddPendingDashboardFiles(
        ConfiguredLogCatalogIndex index,
        ConfiguredLogTarget requestedTarget,
        ConfiguredLogGroup dashboard,
        Dictionary<string, MutablePendingFile> pendingByFileId,
        List<MutablePendingFile> pendingInOrder,
        ExpansionBudget expansionBudget,
        CancellationToken ct)
    {
        foreach (var fileId in dashboard.FileIds)
        {
            ct.ThrowIfCancellationRequested();
            AddPendingFile(
                index,
                requestedTarget,
                dashboard,
                index.FilesById[fileId],
                pendingByFileId,
                pendingInOrder,
                expansionBudget);
            if (expansionBudget.IsExceeded)
                return;
        }

    }

    private static void AddPendingFile(
        ConfiguredLogCatalogIndex index,
        ConfiguredLogTarget requestedTarget,
        ConfiguredLogGroup dashboard,
        ConfiguredLogFile file,
        Dictionary<string, MutablePendingFile> pendingByFileId,
        List<MutablePendingFile> pendingInOrder,
        ExpansionBudget expansionBudget)
    {
        var provenance = CreateProvenance(index, requestedTarget, dashboard, GetDisplayName(file));
        if (pendingByFileId.TryGetValue(file.Id, out var existing))
        {
            if (existing.AddProvenance(provenance))
                expansionBudget.AddProvenance();
            return;
        }

        expansionBudget.AddStableFileAndProvenance();
        if (expansionBudget.IsExceeded)
            return;

        var pending = new MutablePendingFile(file, provenance);
        pendingByFileId.Add(file.Id, pending);
        pendingInOrder.Add(pending);
    }

    private static bool TryResolvePendingFile(
        ConfiguredLogCatalogIndex index,
        ConfiguredLogSelectionRequest request,
        IConfiguredLogPathCandidateSelector pathCandidateSelector,
        MutablePendingFile pending,
        out MutableSelectedFile? selected,
        out ConfiguredLogFileError? error)
    {
        selected = null;
        error = null;
        var file = pending.File;
        var displayName = GetDisplayName(file);
        if (!ConfiguredDatePathResolver.TryResolveCandidates(
                file.PhysicalPath,
                index.Snapshot.DatePathPatterns,
                request.ReferenceDate,
                request.DateOffsetDays,
                out var candidates,
                out var errorCode,
                out var errorMessage))
        {
            error = new ConfiguredLogFileError(
                file.Id,
                displayName,
                errorCode!,
                errorMessage!,
                pending.Provenance.ToImmutableArray());
            return false;
        }

        string selectedPath;
        try
        {
            selectedPath = Path.GetFullPath(pathCandidateSelector.SelectPath(file.Id, candidates));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            error = new ConfiguredLogFileError(
                file.Id,
                displayName,
                "path_candidate_selection_failed",
                "The effective configured log path could not be selected.",
                pending.Provenance.ToImmutableArray());
            return false;
        }

        if (!candidates.Contains(selectedPath, StringComparer.OrdinalIgnoreCase))
        {
            error = new ConfiguredLogFileError(
                file.Id,
                displayName,
                "path_candidate_selection_failed",
                "The effective path is outside the configured date-path candidates.",
                pending.Provenance.ToImmutableArray());
            return false;
        }

        selected = new MutableSelectedFile(file.Id, displayName, selectedPath, candidates, pending.Provenance[0]);
        foreach (var provenance in pending.Provenance.Skip(1))
            selected.AddProvenance(provenance);
        return true;
    }

    private static string CreatePhysicalPathIdentity(string physicalPath)
    {
        var normalized = Path.GetFullPath(physicalPath).ToUpperInvariant();
        var bytes = System.Text.Encoding.UTF8.GetBytes(normalized);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes).AsSpan(0, 16));
    }

    private static void AddDashboardFiles(
        ConfiguredLogCatalogIndex index,
        ConfiguredLogSelectionRequest request,
        IConfiguredLogPathCandidateSelector pathCandidateSelector,
        ConfiguredLogTarget requestedTarget,
        ConfiguredLogGroup dashboard,
        Dictionary<string, MutableSelectedFile> selectedByFileId,
        List<MutableSelectedFile> selectedInOrder,
        HashSet<string> resolvedPhysicalPaths,
        Dictionary<string, MutableFileError> errorsByFileId,
        ExpansionBudget expansionBudget)
    {
        foreach (var fileId in dashboard.FileIds)
        {
            AddFile(
                index,
                request,
                pathCandidateSelector,
                requestedTarget,
                dashboard,
                index.FilesById[fileId],
                selectedByFileId,
                selectedInOrder,
                resolvedPhysicalPaths,
                errorsByFileId,
                expansionBudget);
            if (expansionBudget.IsExceeded || resolvedPhysicalPaths.Count > request.MaxResolvedFiles)
                break;
        }
    }

    private static void AddFile(
        ConfiguredLogCatalogIndex index,
        ConfiguredLogSelectionRequest request,
        IConfiguredLogPathCandidateSelector pathCandidateSelector,
        ConfiguredLogTarget requestedTarget,
        ConfiguredLogGroup dashboard,
        ConfiguredLogFile file,
        Dictionary<string, MutableSelectedFile> selectedByFileId,
        List<MutableSelectedFile> selectedInOrder,
        HashSet<string> resolvedPhysicalPaths,
        Dictionary<string, MutableFileError> errorsByFileId,
        ExpansionBudget expansionBudget)
    {
        if (expansionBudget.IsExceeded)
            return;

        var displayName = GetDisplayName(file);
        var provenance = CreateProvenance(index, requestedTarget, dashboard, displayName);
        if (selectedByFileId.TryGetValue(file.Id, out var selected))
        {
            if (selected.AddProvenance(provenance))
                expansionBudget.AddProvenance();
            return;
        }

        if (errorsByFileId.TryGetValue(file.Id, out var existingError))
        {
            if (existingError.AddProvenance(provenance))
                expansionBudget.AddProvenance();
            return;
        }

        expansionBudget.AddStableFileAndProvenance();
        if (expansionBudget.IsExceeded)
            return;

        if (!ConfiguredDatePathResolver.TryResolveCandidates(
                file.PhysicalPath,
                index.Snapshot.DatePathPatterns,
                request.ReferenceDate,
                request.DateOffsetDays,
                out var candidates,
                out var errorCode,
                out var errorMessage))
        {
            errorsByFileId.Add(
                file.Id,
                new MutableFileError(
                    file.Id,
                    displayName,
                    errorCode!,
                    errorMessage!,
                    provenance));
            return;
        }

        string selectedPath;
        try
        {
            selectedPath = Path.GetFullPath(pathCandidateSelector.SelectPath(file.Id, candidates));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            errorsByFileId.Add(
                file.Id,
                new MutableFileError(
                    file.Id,
                    displayName,
                    "path_candidate_selection_failed",
                    "The effective configured log path could not be selected.",
                    provenance));
            return;
        }

        if (!candidates.Contains(selectedPath, StringComparer.OrdinalIgnoreCase))
        {
            errorsByFileId.Add(
                file.Id,
                new MutableFileError(
                    file.Id,
                    displayName,
                    "path_candidate_selection_failed",
                    "The effective path is outside the configured date-path candidates.",
                    provenance));
            return;
        }

        selected = new MutableSelectedFile(file.Id, displayName, selectedPath, candidates, provenance);
        selectedByFileId.Add(file.Id, selected);
        selectedInOrder.Add(selected);
        resolvedPhysicalPaths.Add(selectedPath);
    }

    private static ConfiguredLogProvenance CreateProvenance(
        ConfiguredLogCatalogIndex index,
        ConfiguredLogTarget target,
        ConfiguredLogGroup dashboard,
        string displayName)
    {
        var dashboardPath = index.GroupPaths[dashboard.Id];
        var targetPath = target.Kind switch
        {
            ConfiguredLogTargetKind.Folder or ConfiguredLogTargetKind.Dashboard => index.GroupPaths[target.Id],
            ConfiguredLogTargetKind.LogFile => $"{dashboardPath} / {displayName}",
            _ => string.Empty
        };
        return new ConfiguredLogProvenance(
            target.Id,
            target.Kind,
            targetPath,
            dashboard.Id,
            dashboardPath);
    }

    private static List<MutableSelectedFile> DeduplicatePhysicalPaths(
        IEnumerable<MutableSelectedFile> selectedInOrder)
    {
        var byPath = new Dictionary<string, MutableSelectedFile>(StringComparer.OrdinalIgnoreCase);
        var result = new List<MutableSelectedFile>();
        foreach (var selected in selectedInOrder)
        {
            if (!byPath.TryGetValue(selected.PhysicalPath, out var existing))
            {
                byPath.Add(selected.PhysicalPath, selected);
                result.Add(selected);
                continue;
            }

            existing.Merge(selected);
        }

        return result;
    }

    private static ConfiguredLogSelectionResult CreateResolvedFileLimitResult(
        string catalogRevision,
        ConfiguredLogSelectionRequest request,
        int expandedFileCount,
        int resolvedPhysicalFileCount,
        IReadOnlyDictionary<string, MutableFileError> errorsByFileId)
    {
        var limitError = new ConfiguredLogRequestError(
            "resolved_file_limit_exceeded",
            $"Target expansion resolved {resolvedPhysicalFileCount} physical files, exceeding the effective limit of {request.MaxResolvedFiles}.");
        return new ConfiguredLogSelectionResult(
            catalogRevision,
            files: null,
            errors: [limitError],
            fileErrors: errorsByFileId.Values.Select(error => error.ToContract()),
            new ConfiguredLogSelectionSummary(
                request.Targets.Length,
                expandedFileCount,
                resolvedPhysicalFileCount,
                errorsByFileId.Count,
                request.MaxTargets,
                request.MaxResolvedFiles,
                RejectedByLimit: true));
    }

    private static string GetDisplayName(ConfiguredLogFile file)
    {
        var displayName = Path.GetFileName(file.PhysicalPath);
        return string.IsNullOrWhiteSpace(displayName) ? file.Id : displayName;
    }

    private static ConfiguredLogSelectionResult CreateRejectedResult(
        string catalogRevision,
        ConfiguredLogSelectionRequest request,
        IEnumerable<ConfiguredLogRequestError> errors)
        => new(
            catalogRevision,
            files: null,
            errors,
            fileErrors: null,
            new ConfiguredLogSelectionSummary(
                request.Targets.Length,
                0,
                0,
                0,
                request.MaxTargets,
                request.MaxResolvedFiles,
                RejectedByLimit: errors.Any(error => error.Code.EndsWith("limit_exceeded", StringComparison.Ordinal))));

    private sealed record ValidatedTarget(
        ConfiguredLogTarget Target,
        ConfiguredLogGroup? Group,
        ConfiguredLogFile? File);

    private sealed class MutablePendingFile
    {
        private readonly List<ConfiguredLogProvenance> _provenance;

        internal MutablePendingFile(ConfiguredLogFile file, ConfiguredLogProvenance provenance)
        {
            File = file;
            _provenance = [provenance];
        }

        internal ConfiguredLogFile File { get; }

        internal IReadOnlyList<ConfiguredLogProvenance> Provenance => _provenance;

        internal bool AddProvenance(ConfiguredLogProvenance provenance)
        {
            if (_provenance.Contains(provenance))
                return false;

            _provenance.Add(provenance);
            return true;
        }
    }

    private sealed class MutableSelectedFile
    {
        private readonly List<string> _equivalentFileIds;
        private readonly List<string> _orderedPathCandidates;
        private readonly List<ConfiguredLogProvenance> _provenance;

        internal MutableSelectedFile(
            string fileId,
            string displayName,
            string physicalPath,
            ImmutableArray<string> orderedPathCandidates,
            ConfiguredLogProvenance provenance)
        {
            FileId = fileId;
            DisplayName = displayName;
            PhysicalPath = physicalPath;
            _equivalentFileIds = [fileId];
            _orderedPathCandidates = orderedPathCandidates.ToList();
            _provenance = [provenance];
        }

        internal string FileId { get; }

        internal string DisplayName { get; }

        internal string PhysicalPath { get; }

        internal IReadOnlyList<string> OrderedPathCandidates => _orderedPathCandidates;

        internal bool AddProvenance(ConfiguredLogProvenance provenance)
        {
            if (!_provenance.Contains(provenance))
            {
                _provenance.Add(provenance);
                return true;
            }

            return false;
        }

        internal void Merge(MutableSelectedFile other)
        {
            foreach (var fileId in other._equivalentFileIds)
            {
                if (!_equivalentFileIds.Contains(fileId, StringComparer.Ordinal))
                    _equivalentFileIds.Add(fileId);
            }

            foreach (var candidate in other._orderedPathCandidates)
            {
                if (!_orderedPathCandidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    _orderedPathCandidates.Add(candidate);
            }

            foreach (var provenance in other._provenance)
                _ = AddProvenance(provenance);
        }

        internal ResolvedConfiguredLogFile ToContract()
            => new(
                FileId,
                DisplayName,
                PhysicalPath,
                _equivalentFileIds.ToImmutableArray(),
                _orderedPathCandidates.ToImmutableArray(),
                _provenance.ToImmutableArray());
    }

    private sealed class MutableFileError
    {
        private readonly List<ConfiguredLogProvenance> _provenance;

        internal MutableFileError(
            string fileId,
            string displayName,
            string code,
            string message,
            ConfiguredLogProvenance provenance)
        {
            FileId = fileId;
            DisplayName = displayName;
            Code = code;
            Message = message;
            _provenance = [provenance];
        }

        private string FileId { get; }

        private string DisplayName { get; }

        private string Code { get; }

        private string Message { get; }

        internal bool AddProvenance(ConfiguredLogProvenance provenance)
        {
            if (!_provenance.Contains(provenance))
            {
                _provenance.Add(provenance);
                return true;
            }

            return false;
        }

        internal ConfiguredLogFileError ToContract()
            => new(FileId, DisplayName, Code, Message, _provenance.ToImmutableArray());
    }

    private sealed class FirstPathCandidateSelector : IConfiguredLogPathCandidateSelector
    {
        internal static FirstPathCandidateSelector Instance { get; } = new();

        public string SelectPath(string fileId, ImmutableArray<string> orderedCandidates)
            => orderedCandidates[0];
    }

    private sealed class ExpansionBudget(int maximumStableFiles, int maximumProvenanceEntries)
    {
        internal int StableFileCount { get; private set; }

        private int ProvenanceCount { get; set; }

        internal bool IsExceeded =>
            StableFileCount > maximumStableFiles ||
            ProvenanceCount > maximumProvenanceEntries;

        internal void AddStableFileAndProvenance()
        {
            StableFileCount++;
            ProvenanceCount++;
        }

        internal void AddProvenance() => ProvenanceCount++;
    }
}
