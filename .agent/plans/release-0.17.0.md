# WeezTail 0.17.0 — Execution Plan

## Document control
Release requested 2026-09-15. Owner: Codex. Living plan for the MCP minor release.

## Resume checkpoint
Version 0.17.0 implemented and validated. All 1,421 tests pass after dispatcher synchronization of one existing test. Initial packaging passed all checks. Next: commit release, regenerate packages to embed release commit, publish, then merge/validate/push exploration.

## Purpose and observable outcome
Publish v0.17.0 on arCarnes/LogReader with validated Windows x64 portable ZIP and MSI; merge release changes into explore/wql-structured-fields and push that branch.

## Scope
Version metadata, current-version documentation, release packaging, GitHub publication, exploration branch merge and push.

## Non-goals
No WQL feature changes, unrelated refactors, or changes to the existing v0.16.8 draft.

## Definitions
The product root is LogReader/. The exploration branch is explore/wql-structured-fields. Published artifacts are self-contained Windows x64 packages.

## Existing behavior and evidence
Directory.Build.props centralizes version 0.16.8 and four version fields. MCP was introduced afterward. Latest published GitHub release is v0.16.6; v0.16.8 is a draft. Publish-All.ps1 creates both packages and validates portable layout, MCP stdio, MSI identity and shortcuts.

## Decisions and invariants
Use 0.17.0 per user request. Preserve MSI upgrade identity and existing draft. Release main without WQL changes. Merge main into exploration after publication; keep the source checkout on main when finished. Push exploration as a separate remote branch.

## Open questions
None.

## Milestones / issue summary
1. Bump, validate and package main.
2. Commit, push, tag and publish v0.17.0.
3. Merge into exploration, validate and push branch.

## Progress
- [x] Inspect repository, version history, packaging, GitHub authentication and releases.
- [x] Bump and validate main; build release packages.
- [ ] Commit and push main; publish verified tag and assets.
- [ ] Merge, validate and push exploration branch.

## Release implementation
- State: in progress.
- Dependencies: .NET 8 SDK, WiX restore, authenticated git/gh, Windows.
- Purpose: make MCP available in a new minor release.
- Expected implementation areas: Directory.Build.props, DeveloperGuide.md, this plan.
- Tasks: update four version fields and guide; run documented clean/build/test; package; commit/tag/push; publish notes and assets; merge and push exploration.
- Acceptance criteria: tests and package checks pass; GitHub v0.17.0 targets release commit with both assets; exploration contains release commit and matches its remote.
- Focused validation: version diff and artifact metadata.
- Progress/evidence: SDK 8.0.418 and gh 2.96.0 available; authenticated account arCarnes.

## Final validation and demonstration
Run dotnet clean LogReader.sln -m:1, dotnet build LogReader.sln -m:1, dotnet test LogReader.sln in that order from product root. Run packaging/Publish-All.ps1 (Release win-x64). Verify asset sizes and SHA256 against GitHub release API. Repeat clean/build/test on merged exploration before pushing.

## Surprises & discoveries
Sandbox credential access reports an invalid token, but normal credential access authenticates successfully. Prior device login completed authorization but could not write hosts.yml; existing keyring authentication works outside sandbox.

## Risks and mitigations
Release publishes executable downloads publicly; upload only validated outputs. Existing packaging removes generated artifacts; verify all resolved cleanup paths remain under product artifacts before invoking it. Merge may expose branch-specific test failures; repair or report before pushing. Do not discard unrelated changes.

## Deferred work
Existing v0.16.8 draft remains unchanged.

## Decision log
- Validation revealed an existing timing race in TailSearch_ExpandedResultGrowth_RefreshesVisibleRowsOnce: the collection count becomes visible before CollectionChanged is delivered. Run this test on the existing WPF dispatcher so updates and assertions are serialized; preserve the exact notification-count assertion.
- User selected 0.17.0 to reflect introduction of MCP and authorized main/release publication plus exploration merge and remote branch push.

## Outcomes & retrospective
Pending.

## Handoff history
None.

Validation evidence: initial clean passed; build passed after forced restore refreshed cached NU1900 audit warnings (zero warnings/errors). Initial full test: core 493 passed; WPF 927 passed, 1 failed at SearchPanelViewModelTests.cs:3740 (expected notification count 1, actual 0). Repaired with WpfTestHost.RunAsync; focused test passed, then full suite passed (493 core + 928 WPF, zero failures/skips). Subsequent build had zero warnings/errors. Publish-All.ps1 completed successfully with portable layout/ZIP, both MCP stdio smoke tests, installer actions, MSI identity/version and shortcut checks.

Packaging decision: regenerate after the release commit so executable ProductVersion embeds the exact tagged source revision. No source changes or further test repetition required for this metadata-only rebuild.
