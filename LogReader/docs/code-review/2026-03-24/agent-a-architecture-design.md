# Agent A - Architecture and Design

## Scope

This pass focused on module boundaries, dependency direction, configuration sprawl, ownership clarity, and areas where future changes are likely to be unusually expensive.

## Findings

### [HIGH] Dashboard replacement is orchestrated as a destructive multi-step workflow
- Confidence: High
- Location: `LogReader.App/Services/DashboardWorkspaceService.cs` - `ApplyImportedViewAsync`
- Evidence: the method deletes the existing view tree before the replacement graph is fully persisted.
- Why it matters: a failure in the middle of this workflow turns one feature into a state migration problem, which raises the change cost for all future dashboard work.
- Fix direction: stage imported state separately and swap it in only after the full graph validates and persists.

### [HIGH] Persistence boundaries normalize invalid domain data instead of rejecting it
- Confidence: High
- Location: `LogReader.Infrastructure/Repositories/JsonLogGroupRepository.cs` - `NormalizeTree`
- Evidence: invalid group structures are silently transformed, including clearing dashboard `FileIds`.
- Why it matters: domain invariants are enforced by hidden mutation in infrastructure instead of by explicit model validation.
- Fix direction: move topology validation closer to the domain model and treat invalid persisted state as a migration or repair case.

### [MEDIUM] Shared storage behavior assumes a single process without enforcing that contract
- Confidence: Medium
- Location: `LogReader.Infrastructure/Repositories/JsonStore.cs`, `LogReader.App/App.xaml.cs`
- Evidence: only in-process synchronization is present, while cache cleanup and temp-file persistence touch shared storage paths.
- Why it matters: architecture implicitly depends on a deployment assumption that is not guaranteed by the product.
- Fix direction: make the single-instance constraint explicit or upgrade persistence to use interprocess coordination.

### [MEDIUM] `MainViewModel` and `DashboardWorkspaceService` are absorbing too many responsibilities
- Confidence: High
- Location: `LogReader.App/ViewModels/MainViewModel.cs`, `LogReader.App/Services/DashboardWorkspaceService.cs`
- Evidence: these classes coordinate navigation, persistence, imports, workspace refresh, commands, error reporting, and UI-facing orchestration.
- Why it matters: high-churn work will keep concentrating in the same files, increasing regression risk and slowing refactors.
- Fix direction: split dashboard import/export, file-resolution, and view activation into narrower services with clearer ownership.

### [MEDIUM] Repository APIs force lookup-heavy callers into whole-store scans
- Confidence: High
- Location: `LogReader.Infrastructure/Repositories/JsonLogFileRepository.cs`
- Evidence: common lookup methods are layered on `GetAllAsync`, and dashboard flows call them repeatedly.
- Why it matters: API shape is pushing service-layer code toward inefficient and increasingly brittle orchestration.
- Fix direction: introduce batch and indexed lookup APIs that match the actual workload.

### [MEDIUM] Business rules are spread across view models, services, and repositories
- Confidence: Medium
- Location: `MainViewModel`, `DashboardWorkspaceService`, `JsonLogGroupRepository`
- Evidence: path validation, group normalization, import repair, and activation behavior live in different layers.
- Why it matters: future behavior changes will require synchronized edits across multiple layers with no single source of truth.
- Fix direction: centralize rules for imported views, group invariants, and file registration in domain-facing services.

### [LOW] Recovery behavior is implemented as silent self-healing instead of an explicit repair workflow
- Confidence: High
- Location: `JsonSettingsRepository.cs`, `JsonLogFileRepository.cs`, `JsonLogGroupRepository.cs`
- Evidence: parse failures are converted directly into replacement writes.
- Why it matters: architecture hides state corruption behind automatic mutation, which makes diagnosis and user recovery harder.
- Fix direction: add a recovery surface that preserves originals and records what failed.

### [LOW] Configuration and storage path rules are duplicated across app and installer code
- Confidence: Medium
- Location: `LogReader.App` storage-path services, `LogReader.Setup/InstallerActions.vbs`
- Evidence: uninstall cleanup re-derives safe roots instead of calling shared validation logic.
- Why it matters: duplicated policy logic tends to drift and creates inconsistent safety boundaries over time.
- Fix direction: document and centralize storage-root rules, then reuse them in packaging and runtime code paths.

## Architectural Theme

The repository has a clean project split, but core operational behavior is still concentrated in a few app-layer orchestrators. The main design risk is not dependency cycles; it is that failure handling, data repair, and business rules are distributed across layers in ways that make future dashboard and persistence changes costly.
