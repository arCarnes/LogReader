# WeezTail 1.2.0 — Execution Plan

## Document control

Requested 2026-09-28. Owner: Codex. Living plan for the 1.2.0 release.

## Resume checkpoint

The stable 1.2.0 release is published from tagged commit 4366d0df82b5b854e3e15447b1f3d1014f895684. Windows CI, local solution tests, portable packaging, MSI validation, and remote asset hash checks passed.

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
- [x] Pass portable and MSI packaging validation.
- [x] Push main and annotated v1.2.0 tag.
- [x] Publish and verify release notes and both assets.

## Version preparation

- State: complete.
- Dependencies: merged main.
- Purpose: align all product version fields and current-release documentation.
- Expected implementation areas: Directory.Build.props, DeveloperGuide.md.
- Tasks: update version fields; inspect diff; commit.
- Acceptance criteria: all product version fields and current-release text say 1.2.0.
- Focused validation: inspect version metadata and diff.
- Progress/evidence: Commit 4366d0d updates all four version fields and the current release line; git diff --check passed.

## Build and package

- State: complete.
- Dependencies: version commit.
- Purpose: validate the exact release source and artifacts.
- Expected implementation areas: build outputs and ignored artifacts.
- Tasks: run Release build/test and Publish-All.ps1; inspect product version and hashes.
- Acceptance criteria: all commands pass; ZIP and MSI report 1.2.0.
- Focused validation: dotnet build, dotnet test, Publish-All.ps1.
- Progress/evidence: Release build passed with zero warnings/errors; 951 WPF and 560 Core tests passed. Publish-All.ps1 passed portable layout/ZIP, MCP stdio smoke tests, installer action fixtures, WiX build, MSI identity, and shortcut checks. The packaged app and MCP report file version 1.2.0.0; MSI ProductVersion is 1.2.0.

## Publish

- State: complete.
- Dependencies: validated source and artifacts.
- Purpose: make the release available with matching assets.
- Expected implementation areas: Git tag, GitHub release.
- Tasks: push main/tag; publish release notes and assets; verify remote hashes.
- Acceptance criteria: stable v1.2.0 release points to validated source and contains both matching assets.
- Focused validation: GitHub release and asset metadata inspection.
- Progress/evidence: Main and annotated tag were pushed. GitHub Actions run 36423111067 passed build and tests on 4366d0d. GitHub release v1.2.0 is stable with both assets; remote sizes and SHA-256 digests match local files.

## Final validation and demonstration

The main-branch GitHub Actions build and full test run passed on the tagged source: https://github.com/arCarnes/LogReader/actions/runs/36423111067. Local release build and 1,511 tests passed. Publish-All.ps1 completed all portable, MCP, installer, identity, and shortcut checks with a zero-warning WiX build.

The portable ZIP is 99,543,295 bytes with SHA-256 90884DCC692CBB53445CAB36201903C2C31576CAB449F17479C8476BB5A7E5F7. The MSI is 83,816,448 bytes with SHA-256 195946BA6278FB7D18181FFE2607367400F426918BAC7B7757BE560440942B86. GitHub reports identical asset sizes and digests. The annotated v1.2.0 tag peels to 4366d0d.

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

Published https://github.com/arCarnes/LogReader/releases/tag/v1.2.0 as a stable release on 2026-09-28. The release adds three appearance themes, dashboard font controls, a shared Settings color palette, dashboard border alignment, and compact MCP metadata. Local and hosted tests passed on the release source; both packaged assets passed validation and remote hash verification. The release remains unsigned, and full installer lifecycle testing is deferred.

## Handoff history

None.