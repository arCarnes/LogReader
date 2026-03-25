# Agent F - Maintainability and Developer Experience

## Scope

This pass focused on readability, local complexity, duplication, confusing abstractions, onboarding friction, and areas that are unnecessarily hard to change.

## Findings

### [MEDIUM] `MainViewModel` is doing too much
- Confidence: High
- Location: `LogReader.App/ViewModels/MainViewModel.cs`
- Evidence: the file owns command wiring, tab and dashboard coordination, shell behavior, search, navigation, settings interactions, and error reporting.
- Why it matters: developers have to reason about many unrelated workflows in one class, which raises the cost of even small changes.
- Fix direction: split the class by workflow boundary, especially dashboard orchestration versus shell-level view behavior.

### [MEDIUM] `DashboardWorkspaceService` mixes import/export, persistence, activation, and repair logic
- Confidence: High
- Location: `LogReader.App/Services/DashboardWorkspaceService.cs`
- Evidence: the service manages imported views, path resolution, state activation, recovery behavior, and repository writes.
- Why it matters: one service has become the change hotspot for both new features and bug fixes.
- Fix direction: carve out dedicated services for import/export, file registration, and dashboard activation.

### [MEDIUM] Recovery behavior is hard to discover because it is silent
- Confidence: High
- Location: JSON repositories and group normalization logic
- Evidence: malformed or invalid persisted data is rewritten or normalized with little diagnostic surface.
- Why it matters: developers debugging user data issues have to infer what happened from side effects rather than logs or explicit repair artifacts.
- Fix direction: preserve originals, emit diagnostics, and document the repair flow.

### [MEDIUM] Repository APIs encourage awkward call patterns
- Confidence: High
- Location: `JsonLogFileRepository.cs`
- Evidence: common service needs require repeated `GetByIdAsync` and `GetByPathAsync` calls that each reload the full store.
- Why it matters: the abstraction hides cost and pushes complexity into callers.
- Fix direction: add batch and atomic APIs that match how callers actually use file metadata.

### [LOW] Installer and runtime path policies are difficult to keep aligned
- Confidence: Medium
- Location: `LogReader.Setup/InstallerActions.vbs`, runtime storage-path logic
- Evidence: similar safety rules are implemented in different languages and projects.
- Why it matters: onboarding new contributors into storage behavior requires tracing both app and installer code.
- Fix direction: document path-policy invariants clearly and minimize duplicated logic where possible.

### [LOW] High-churn async event-handler code is harder to reason about than command-based flows
- Confidence: Medium
- Location: `MainWindow.xaml.cs`, `DashboardTreeView.xaml.cs`
- Evidence: multiple UI handlers directly trigger asynchronous work.
- Why it matters: behavior is spread between XAML event wiring and imperative code-behind, which complicates debugging and testability.
- Fix direction: move more of the workflow behind view-model commands or a shared UI action helper.

### [LOW] Current docs do not highlight the main operational risk areas
- Confidence: Medium
- Location: `docs` folder
- Evidence: user and installation guides exist, but there is little repository-level documentation around persistence assumptions, dashboard import behavior, or storage safety constraints.
- Why it matters: contributors can make local changes without realizing which workflows are especially sensitive.
- Fix direction: add a short architecture or contributor note covering persistence, dashboards, and path validation rules.

## Maintainability Theme

The repository is readable overall, but a few central classes have become coordination hubs that slow safe changes. The highest-leverage maintainability work is reducing silent behavior, aligning repository APIs with real use, and documenting the assumptions around persistence and dashboard workflows.
