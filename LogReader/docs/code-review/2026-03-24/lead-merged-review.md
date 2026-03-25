# Lead Merged Review

## Repository Summary

- Stack overview: C#, XAML, PowerShell, WiX/VBScript; .NET 8, WPF, `CommunityToolkit.Mvvm`, xUnit.
- Main subsystems: `LogReader.Core`, `LogReader.Infrastructure`, `LogReader.App`, `LogReader.Core.Tests`, `LogReader.Tests`, plus packaging and setup projects.
- Notable architectural shape: layered in project structure, but much of the operational workflow is concentrated in the app layer, especially `MainViewModel` and `DashboardWorkspaceService`.
- Overall risk areas: destructive recovery paths, shared JSON storage assumptions, dashboard orchestration complexity, and scaling cliffs in polling-heavy workflows.

## Top Findings

### [HIGH] Destructive dashboard import replacement
- Facets: Bugs | Reliability | Testing
- Confidence: High
- Location: `LogReader.App/Services/DashboardWorkspaceService.cs` - `ApplyImportedViewAsync`
- Why this is a problem: the current dashboard view is deleted before the replacement is fully committed, so a mid-import failure can leave persisted state partially replaced or empty.
- Evidence: `ApplyImportedViewAsync` deletes existing groups before recreating the replacement tree.
- Likely impact: imported view failures can destroy a user's saved dashboard configuration.
- Recommended fix: validate and materialize the complete import first, then swap atomically or restore the original state on failure.
- Optional regression test: inject a repository failure during replacement and verify the pre-import dashboard remains intact.

### [HIGH] Malformed or unsupported JSON is silently overwritten
- Facets: Bugs | Reliability | Maintainability
- Confidence: High
- Location: `LogReader.Infrastructure/Repositories/JsonSettingsRepository.cs`, `JsonLogFileRepository.cs`, `JsonLogGroupRepository.cs`
- Why this is a problem: corrupted or mismatched persisted files are silently replaced with defaults or empty collections instead of being quarantined.
- Evidence: the repositories catch `JsonException` and immediately persist a replacement payload; existing tests assert this behavior.
- Likely impact: a single parse failure can erase user settings, dashboards, or tracked file metadata.
- Recommended fix: preserve the original file, surface a recovery error, and only write migrated data after explicit validation.

### [HIGH] Invalid group topology is normalized by dropping dashboard membership
- Facets: Architecture | Bugs | Reliability
- Confidence: High
- Location: `LogReader.Infrastructure/Repositories/JsonLogGroupRepository.cs` - `NormalizeTree`
- Why this is a problem: invalid persisted trees are silently mutated by converting dashboard nodes into branches and clearing `FileIds`.
- Evidence: `NormalizeTree` clears dashboard membership rather than rejecting or migrating malformed topology.
- Likely impact: unexpected data loss with little visibility into what was changed.
- Recommended fix: reject invalid topology, or run an explicit migration that preserves the original payload for repair.

### [MEDIUM] Shared state is synchronized only inside one process
- Facets: Architecture | Reliability
- Confidence: Medium
- Location: `LogReader.Infrastructure/Repositories/JsonStore.cs`, `LogReader.App/App.xaml.cs`
- Why this is a problem: writes rely on in-process locking and a fixed temporary-file naming strategy, but the product does not enforce single-instance access to the storage root.
- Evidence: `JsonStore` uses local synchronization only, while startup code also deletes shared cache state opportunistically.
- Likely impact: concurrent processes can interleave writes, clobber temp files, or delete shared cache artifacts.
- Recommended fix: add a named mutex or file lock and use unique temp file names for atomic persistence.

### [MEDIUM] Imported views can trigger unintended UNC or network access
- Facets: Security | Reliability
- Confidence: High
- Location: `LogReader.Infrastructure/Repositories/JsonLogGroupRepository.cs`, `LogReader.App/Services/DashboardWorkspaceService.cs`
- Why this is a problem: imported `FilePaths` are accepted and later probed with file-system checks, which on Windows can authenticate to remote UNC shares.
- Evidence: import accepts arbitrary paths and later refresh/open flows call `File.Exists` on those paths.
- Likely impact: opening an untrusted export can trigger outbound network access or credential leakage.
- Recommended fix: block or explicitly confirm UNC, relative, and other non-local paths before persisting imported data.

### [MEDIUM] MSI uninstall cleanup trusts mutable path data
- Facets: Security | Reliability
- Confidence: Medium
- Location: `LogReader.Setup/InstallerActions.vbs`, `LogReader.Setup/Product.wxs`
- Why this is a problem: uninstall cleanup derives deletion targets from persisted configuration and user selections without revalidating that the resolved path is an app-owned storage root.
- Evidence: cleanup logic deletes `Data` and `Cache` subfolders and may remove the root directory if it appears empty.
- Likely impact: tampered config can turn uninstall into unintended file deletion outside the application's intended storage area.
- Recommended fix: reapply the same path validation used by the app and constrain deletion to known safe roots.

### [MEDIUM] `LogFileEntry` creation is not atomic
- Facets: Bugs | Reliability | Maintainability
- Confidence: Medium
- Location: `LogReader.App/Services/TabWorkspaceService.cs`, `LogReader.App/Services/DashboardWorkspaceService.cs`
- Why this is a problem: file tracking uses a read-then-add pattern without an atomic repository API.
- Evidence: callers perform `GetByPathAsync` followed by `AddAsync`, while the repository has no guarded get-or-create path.
- Likely impact: concurrent opens or imports can create duplicate records for the same file path.
- Recommended fix: add a repository-level `GetOrCreateByPathAsync` protected by one lock and a canonical path uniqueness check.

### [MEDIUM] Whole-word filtering disagrees with whole-word search on underscores
- Facets: Bugs | Testing
- Confidence: High
- Location: `LogReader.Infrastructure/Services/SearchService.cs`, `LogReader.App/Services/LogFilterSession.cs`
- Why this is a problem: the same whole-word query can behave differently in snapshot search versus live filtering because the two code paths use different word-boundary rules.
- Evidence: search treats `_` as part of a word while filter matching does not.
- Likely impact: inconsistent results between search UI and live filtered output.
- Recommended fix: centralize word-boundary logic in a shared helper and cover both paths with the same test cases.

### [MEDIUM] Highlight regexes run without a timeout
- Facets: Performance | Reliability | Testing
- Confidence: High
- Location: `LogReader.App/Helpers/LineHighlighter.cs`
- Why this is a problem: user-supplied highlight rules are evaluated with `Regex.IsMatch` without the timeout policy already used elsewhere.
- Evidence: search and filter services use explicit regex timeouts, but highlighting does not.
- Likely impact: catastrophic regex patterns can freeze rendering or tail updates.
- Recommended fix: apply the same regex timeout used by search/filter and add a regression test for backtracking-heavy patterns.

### [MEDIUM] Dashboard open and refresh repeatedly full-scan `logfiles.json`
- Facets: Maintainability | Performance
- Confidence: High
- Location: `LogReader.App/Services/DashboardWorkspaceService.cs`, `LogReader.Infrastructure/Repositories/JsonLogFileRepository.cs`
- Why this is a problem: dashboard workflows resolve file IDs one at a time through repository methods that reload the entire file store.
- Evidence: `GetByIdAsync` and `GetByPathAsync` both depend on `GetAllAsync`, and dashboard open paths call them repeatedly.
- Likely impact: dashboard loading and recovery become increasingly expensive as tracked files grow.
- Recommended fix: batch-load file metadata once per operation and add repository APIs optimized for lookup-heavy flows.

### [MEDIUM] Tailing and search pipelines have clear scaling cliffs
- Facets: Architecture | Performance
- Confidence: High
- Location: `LogReader.Infrastructure/Services/FileTailService.cs`, `LogReader.App/ViewModels/SearchPanelViewModel.cs`, `LogReader.Infrastructure/Services/SearchService.cs`
- Why this is a problem: each tailed file gets its own polling loop, tail search adds another polling path, and result view models materialize dense hit sets before pushing them into observable collections one item at a time.
- Evidence: tailing, search refresh, and result insertion all use repeated polling or per-item UI updates.
- Likely impact: large active workspaces will hit CPU, I/O, and UI throughput limits sooner than necessary.
- Recommended fix: drive tail search from tail events, batch UI result updates, and cap or paginate large result sets.

### [MEDIUM] Unguarded `async void` handlers can crash the shell
- Facets: Maintainability | Reliability
- Confidence: High
- Location: `LogReader.App/Views/MainWindow.xaml.cs`, `LogReader.App/Views/DashboardTreeView.xaml.cs`
- Why this is a problem: view event handlers directly await service work without centralized exception containment.
- Evidence: multiple `async void` handlers call into persistence and workspace flows directly.
- Likely impact: routine I/O or state errors can surface as unhandled UI-thread exceptions.
- Recommended fix: move this work behind async commands where possible, or wrap view handlers in a shared exception-reporting helper.

## Cross-Cutting Themes

- Persistence currently favors destructive self-healing over recoverable failure handling.
- The app layer owns too much orchestration, especially around dashboards and window-level workflows.
- Whole-store JSON scans, stacked polling loops, and per-item UI updates amplify one another into performance cliffs.
- Failure-path coverage is thin around import rollback, multi-instance access, unsafe paths, and catastrophic regex behavior.

## Quick Wins

1. Make view import transactional and validate imported graphs before deleting current state.
2. Quarantine malformed JSON instead of overwriting it in place.
3. Add a single-instance or interprocess-lock strategy and unique temp-file saves.
4. Reject or confirm UNC and relative paths during import and uninstall cleanup.
5. Add a repository-level `GetOrCreateByPathAsync`.
6. Share one word-boundary helper between search and filter.
7. Add regex timeouts to highlighting.
8. Batch dashboard file lookups instead of reloading `logfiles.json` per member.
9. Replace layered polling with event-driven refreshes where possible.
10. Add targeted tests for rollback, unsafe imports, duplicate file creation, and regex backtracking.

## Deep Risks

1. `MainViewModel` and `DashboardWorkspaceService` are likely to stay high-churn regression hotspots until responsibilities are split.
2. Shared JSON persistence still assumes a single well-behaved process, but the product does not enforce that assumption.
3. The current recovery model optimizes for keeping the app running rather than preserving user state.
4. Large workspaces will continue to strain polling-heavy, whole-store workflows until those costs are reduced structurally.
