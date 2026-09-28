# WeezTail 1.2.0 — Execution Plan

## Document control

Requested 2026-09-28. Owner: Codex. Living plan for the 1.2.0 release.

## Resume checkpoint

PR #3 merged into main at c55d248. The isolated release checkout is at that commit. Version metadata is 1.2.0. Release build and all 1,511 tests passed. Next: commit release preparation and run packaging from that commit.

## Purpose and observable outcome

Publish WeezTail 1.2.0 from main as a stable GitHub release with a validated Windows x64 portable ZIP and MSI. The source tag, product metadata, release notes, and uploaded assets must agree.

## Scope

- Advance product version metadata and current-release documentation to 1.2.0.
- Build and test the full solution.
- Package and validate portable and MSI artifacts from the exact release commit.
- Push main and an annotated v1.2.0 tag, then publish and verify the GitHub release.

## Non-goals

No unrelated feature changes, dependency upgrades, signing, or packaging automation changes.

## Definitions

- Product root: LogReader/.
- Artifacts: LogReader/artifacts/publish/WeezTail-1.2.0-portable-win-x64.zip and LogReader/artifacts/installer/WeezTail.Setup.msi.
- Release source: the commit tagged v1.2.0 and used for final packaging.

## Existing behavior and evidence

- v1.1.0 is the last published release; version metadata is centralized in LogReader/Directory.Build.props.
- PR #3 merged the appearance, settings color, dashboard border, MCP response, and test reliability changes. Its Windows build-and-test check passed.
- LogReader/packaging/Publish-All.ps1 builds and validates both official packages.

## Decisions and invariants

- Use a minor version, 1.2.0, for the user-visible appearance features.
- Keep the existing commits and merge history; release preparation is a separate commit.
- Publish only artifacts built from the tagged commit and verify uploaded asset sizes and SHA-256 digests.
- Preserve existing MSI identity checks and disclose unsigned artifacts.

## Open questions

None.

## Milestones / issue summary

1. Prepare and commit 1.2.0 metadata and documentation.
2. Build, test, and package the exact release source.
3. Tag and push the release source.
4. Publish and verify the release.

## Progress

- [x] Confirm PR #3 merged and prior release process.
- [x] Prepare and commit 1.2.0 metadata and documentation.
- [x] Pass full solution build and tests.
- [ ] Pass portable and MSI packaging validation.
- [ ] Push main and annotated v1.2.0 tag.
- [ ] Publish and verify release notes and both assets.

## Version preparation

- State: complete.
- Dependencies: merged main.
- Purpose: align all product version fields and current-release documentation.
- Expected implementation areas: Directory.Build.props, DeveloperGuide.md.
- Tasks: update version fields; inspect diff; commit.
- Acceptance criteria: all product version fields and current-release text say 1.2.0.
- Focused validation: inspect version metadata and diff.
- Progress/evidence: The version diff updates all four fields and the current release line; git diff --check passed.

## Build and package

- State: pending.
- Dependencies: version commit.
- Purpose: validate the exact release source and artifacts.
- Expected implementation areas: build outputs and ignored artifacts.
- Tasks: run Release build/test and Publish-All.ps1; inspect product version and hashes.
- Acceptance criteria: all commands pass; ZIP and MSI report 1.2.0.
- Focused validation: dotnet build, dotnet test, Publish-All.ps1.
- Progress/evidence: Release build passed with zero warnings/errors; 951 WPF and 560 Core tests passed. Packaging pending.

## Publish

- State: pending.
- Dependencies: validated source and artifacts.
- Purpose: make the release available with matching assets.
- Expected implementation areas: Git tag, GitHub release.
- Tasks: push main/tag; publish release notes and assets; verify remote hashes.
- Acceptance criteria: stable v1.2.0 release points to validated source and contains both matching assets.
- Focused validation: GitHub release and asset metadata inspection.
- Progress/evidence: Pending.

## Final validation and demonstration

Pending.

## Surprises & discoveries

None so far.

## Risks and mitigations

- Packaging regenerates ignored artifacts; inspect outputs before uploading.
- MSI packages are unsigned; state that in release notes.
- Full disposable-machine install and upgrade testing is outside this release's automated validation.

## Deferred work

Production signing, disposable-machine installer lifecycle validation, and automated release publication.

## Decision log

- 2026-09-28: Release 1.2.0 from merged main after passing solution and package validation.

## Outcomes & retrospective

Pending.

## Handoff history

None.