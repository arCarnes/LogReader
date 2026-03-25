# Agent E - Testing and Reliability

## Scope

This pass focused on missing test coverage, flaky behavior, weak observability, rollback hazards, and failure paths that are important in production-oriented code.

## Findings

### [HIGH] Import rollback behavior is not protected by tests
- Confidence: High
- Location: `LogReader.App/Services/DashboardWorkspaceService.cs` - `ApplyImportedViewAsync`
- Evidence: the code deletes existing groups before replacement is complete, but there is no regression test proving state survives a mid-import failure.
- Why it matters: one of the riskiest workflows in the repo lacks coverage for its failure path.
- Fix direction: add a test that injects a repository failure after deletion has started and assert that original state is preserved or restored.

### [HIGH] Corrupted persisted JSON is only tested for overwrite behavior, not safe recovery
- Confidence: High
- Location: `JsonSettingsRepositoryTests.cs`, `JsonLogFileRepositoryTests.cs`, `JsonLogGroupRepositoryTests.cs`
- Evidence: tests assert that malformed files are replaced with defaults or empty collections.
- Why it matters: current tests lock in destructive recovery behavior instead of protecting user-state preservation.
- Fix direction: change tests to assert quarantine, recovery prompts, or explicit migration handling once the implementation is fixed.

### [MEDIUM] A timing-sensitive dashboard cancellation path appears flaky
- Confidence: Medium
- Location: `LogReader.Tests` dashboard cancellation coverage
- Evidence: a full-solution `dotnet test LogReader.sln --no-build` run failed once on a dashboard-cancellation assertion and passed on rerun during the review.
- Why it matters: timing-sensitive tests often hide real race conditions or create noisy CI results.
- Fix direction: make cancellation tests deterministic by controlling scheduling and timing explicitly, and inspect the production path for race windows.

### [MEDIUM] Unsafe import paths lack direct regression coverage
- Confidence: High
- Location: dashboard import and file-resolution flows
- Evidence: import persists arbitrary `FilePaths`, but there is no test explicitly covering UNC or other untrusted path inputs.
- Why it matters: a safety-sensitive boundary is currently unguarded by tests.
- Fix direction: add tests for UNC, relative, and invalid paths and define the expected policy clearly.

### [MEDIUM] Duplicate file-registration races are untested
- Confidence: Medium
- Location: `TabWorkspaceService`, `DashboardWorkspaceService`, `JsonLogFileRepository`
- Evidence: file registration uses a read-then-add pattern, but there is no concurrency-focused test around duplicate creation.
- Why it matters: race conditions in identity creation often remain latent until production load exposes them.
- Fix direction: add a concurrent open/import test that asserts only one `LogFileEntry` is persisted per canonical path.

### [MEDIUM] Regex backtracking risk is covered for search, but not for highlighting
- Confidence: High
- Location: `SearchService`, `LogFilterSession`, `LineHighlighter`
- Evidence: search and filter already use timeouts, while highlighting does not and lacks a matching regression test.
- Why it matters: one hot path has stronger reliability safeguards than another path using the same risk surface.
- Fix direction: add a catastrophic-pattern test for highlighting once timeout handling is added.

### [LOW] Multi-instance persistence behavior is not exercised by tests
- Confidence: Medium
- Location: `JsonStore` and startup cache cleanup paths
- Evidence: no tests simulate two processes or conflicting storage access.
- Why it matters: if the app can be launched twice, some of the highest-risk persistence behavior is outside the current test envelope.
- Fix direction: add targeted tests around locking or, at minimum, document and enforce a single-instance constraint.

### [LOW] UI exception containment paths are weakly observable
- Confidence: Medium
- Location: WPF view event handlers and shell startup
- Evidence: `async void` handlers do real work, but tests do not validate error reporting for those paths.
- Why it matters: user-facing crashes and swallowed exceptions are both hard to diagnose without explicit coverage or logging guarantees.
- Fix direction: centralize exception handling for UI-initiated async work and add tests around reported failures.

## Reliability Theme

The suite has good volume, but several of the highest-risk paths are only tested for happy-case behavior or current destructive recovery semantics. The biggest leverage is adding tests around rollback, invalid persisted state, unsafe imports, and concurrency-sensitive file registration.
