# Agent B - Correctness and Bugs

## Scope

This pass focused on logic errors, edge cases, state consistency, null handling, race windows, and incorrect recovery behavior.

## Findings

### [HIGH] Import replacement can destroy existing dashboards on partial failure
- Confidence: High
- Location: `LogReader.App/Services/DashboardWorkspaceService.cs` - `ApplyImportedViewAsync`
- Evidence: existing groups are deleted before replacement groups and file links are fully committed.
- Why it matters: partial failure leaves persisted state empty or inconsistent instead of rolling back.
- Fix direction: stage the import, validate it, and commit it atomically.

### [HIGH] Corrupted JSON inputs are treated as empty/default state
- Confidence: High
- Location: `JsonSettingsRepository.cs`, `JsonLogFileRepository.cs`, `JsonLogGroupRepository.cs`
- Evidence: `JsonException` handlers replace the current file contents with defaults or empty collections.
- Why it matters: the code turns a recoverable parse error into silent state loss.
- Fix direction: preserve the original file and surface an explicit recovery or migration step.

### [HIGH] Invalid persisted trees lose dashboard file membership during normalization
- Confidence: High
- Location: `LogReader.Infrastructure/Repositories/JsonLogGroupRepository.cs` - `NormalizeTree`
- Evidence: invalid nodes are coerced and `FileIds` are cleared.
- Why it matters: load-time repair changes persisted meaning without telling the caller what was removed.
- Fix direction: fail fast on invalid topology or record a reversible migration.

### [MEDIUM] Duplicate `LogFileEntry` records can be created for the same path
- Confidence: Medium
- Location: `LogReader.App/Services/TabWorkspaceService.cs`, `LogReader.App/Services/DashboardWorkspaceService.cs`
- Evidence: both flows do `GetByPathAsync` then `AddAsync` with no atomic repository operation.
- Why it matters: concurrent opens/imports can create duplicate identities and inconsistent group membership.
- Fix direction: add an atomic get-or-create repository method keyed by canonicalized path.

### [MEDIUM] Whole-word matching behaves differently between search and live filtering
- Confidence: High
- Location: `LogReader.Infrastructure/Services/SearchService.cs`, `LogReader.App/Services/LogFilterSession.cs`
- Evidence: search treats `_` as part of a word while filter matching treats it as a boundary.
- Why it matters: users can see different results for the same query depending on feature surface.
- Fix direction: share one word-boundary helper and align tests across both call paths.

### [MEDIUM] UI event handlers can surface unhandled exceptions
- Confidence: High
- Location: `LogReader.App/Views/MainWindow.xaml.cs`, `LogReader.App/Views/DashboardTreeView.xaml.cs`
- Evidence: multiple `async void` handlers await workspace or persistence work without a containment wrapper.
- Why it matters: ordinary I/O failures can become app-crashing UI-thread exceptions.
- Fix direction: move the work into commands or wrap handlers with centralized exception reporting.

### [MEDIUM] Multi-process access can corrupt JSON persistence state
- Confidence: Medium
- Location: `LogReader.Infrastructure/Repositories/JsonStore.cs`
- Evidence: synchronization is in-process only and temp-file names are deterministic.
- Why it matters: two app instances can overwrite or race each other even if each instance is internally consistent.
- Fix direction: add interprocess coordination and unique temp-file save paths.

### [LOW] Recovery logic silently changes persisted semantics
- Confidence: Medium
- Location: `JsonLogGroupRepository.NormalizeTree`
- Evidence: branch/dashboard repair mutates shape and membership on load.
- Why it matters: later code receives a "valid" object graph but no longer knows that data was lost.
- Fix direction: attach diagnostics or surface invalid state instead of silently normalizing it away.

## Correctness Theme

The biggest bug pattern is silent mutation on failure paths. Several code paths that look defensive at first glance actually erase or reshape persisted user state, which makes production issues harder to recover from and harder to diagnose.
