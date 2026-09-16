# WeezTail 0.17.1 — Execution Plan

This is a living document. Keep Progress, Surprises & discoveries, Decision log, Outcomes & retrospective, and the resume checkpoint current throughout execution.

## Document control

- Created: 2026-09-16 (America/New_York).
- Owner: Codex.
- Authorization: the user requested a GitHub CLI release from `main` for 0.17.1.
- Source branch: `main`; starting revision `a658f0c8546f34f4a97cf558f8b9ce7a367d5d1b`.
- Publication target: `arCarnes/LogReader`, tag `v0.17.1`.

## Resume checkpoint

- Current milestone: release complete.
- Completed work: prepared 0.17.1, passed clean/build/test and package validation, built both final assets from `26f8dcf`, atomically pushed `main` and annotated tag `v0.17.1`, published the stable GitHub release with CLI, and verified the public assets.
- Uncommitted changes: none after this completion record is committed. The ignored release notes and generated artifacts remain local evidence.
- Last validation: the public asset sizes and GitHub SHA-256 digests match the local files; `gh release list` reports 0.17.1 as Latest; the remote annotated tag dereferences to `26f8dcff6e54e5a02ceeda769707183cb827c055`.
- Blockers: none. Production signing and real disposable-machine lifecycle testing remain open limitations, not release-task prerequisites under the user's explicit publication request.
- **One next action:** no release action remains; resume the separate MSI reliability plan only when disposable-machine testing or signing is authorized.

## Purpose and observable outcome

Publish WeezTail 0.17.1 from `main` as a stable GitHub release with a self-contained Windows x64 portable ZIP and MSI. The tag, source commit, embedded versions, local files, uploaded assets, sizes, and SHA-256 digests must agree.

## Scope

- Update all centralized product version fields and current-release documentation from 0.17.0 to 0.17.1.
- Validate the exact release commit with clean/build/test and the repository packaging flow.
- Create an annotated `v0.17.1` tag, push `main` and the tag, publish both assets with `gh release create`, and verify the public release with GitHub CLI.
- Record failures, final evidence, and remaining installer limitations.

## Non-goals

- No new MSI behavior, storage contract, UpgradeCode, public API, signing service, or disposable-machine lifecycle work.
- No changes to exploration branches or existing releases.
- No push beyond `main` and `v0.17.1`.

## Definitions

- Product root: `LogReader/`.
- Release artifacts: `LogReader/artifacts/publish/WeezTail-0.17.1-portable-win-x64.zip` and `LogReader/artifacts/installer/WeezTail.Setup.msi`.
- Exact release commit: the version-preparation commit tagged by `v0.17.1`.

## Existing behavior and evidence

- v0.17.0 is the current latest stable release and has the same two artifact types.
- `Publish-All.ps1` reads `Directory.Build.props`, creates the versioned portable ZIP, builds the MSI, and invokes portable, MCP, installer-action, MSI identity, ICE, and shortcut checks.
- Changes since v0.17.0 harden test isolation, replace VBScript installer actions with compiled x64 actions, constrain cleanup paths, align cache cleanup, make migration and cleanup rollback-aware, and add installer fixture/lifecycle harnesses.
- Local reliability validation at `a658f0c` passed Release build and tests (493 core + 930 UI), strict fixtures, and a full MSI/ICE package build. No real install–upgrade–uninstall guest cycle has been completed.

## Decisions and invariants

- Preserve UpgradeCode `{93530218-C7A8-4BC1-B4C0-8A670BA3776A}` and all existing MSI identity/sequence guards.
- Build final packages after the release commit so embedded informational versions contain the tagged source revision.
- Publish a stable latest release, not a draft or prerelease.
- Release notes must state the deferred real-machine lifecycle limitation and unsigned status without overstating fixture coverage.
- Do not upload an artifact unless its local validation passes and its digest can be verified after upload.

## Open questions

None.

## Milestones / issue summary

1. Prepare and commit the 0.17.1 version.
2. Clean, build, test, and package the exact commit.
3. Tag and push the exact commit.
4. Publish and verify the GitHub release.

## Progress

- [x] Inspect release procedure, source delta, repository state, GitHub authentication, and prior release.
- [x] Update and commit 0.17.1 metadata.
- [x] Validate the release source; the subsequent plan-only evidence commit does not require repeating runtime tests.
- [x] Build and inspect final release packages.
- [x] Tag and push `main` and `v0.17.1`.
- [x] Publish and verify the GitHub release.

## Release 0.17.1

- State: Done.
- Dependencies: .NET 8 SDK, WiX/MSVC build tooling, authenticated `git` and `gh`, Windows Installer service access for ICE.
- Purpose: ship the completed MSI reliability work as a traceable patch release.
- Expected implementation areas: `Directory.Build.props`, `docs/DeveloperGuide.md`, this plan, ignored release notes/log/evidence files.
- Tasks: bump version, commit, validate, package, inspect metadata/digests, tag/push, publish with GitHub CLI, verify the release and assets.
- Acceptance criteria: the stable public v0.17.1 release targets the exact validated commit; both expected assets exist and match local sizes/digests; version, MSI identity, and UpgradeCode checks pass; failures and limitations are recorded.
- Focused validation: version search, `git diff --check`, Release clean/build/test, `Publish-All.ps1`, file version inspection, MSI validators, local/public SHA-256 comparison, `gh release view`.
- Progress/evidence: version metadata and guide updated in `8c5b67b`; validation record and final release source committed as `26f8dcf`. Release clean/build passed with zero warnings/errors and full Release tests passed 493 core + 930 UI. `Publish-All.ps1` passed portable layout/ZIP, both MCP smoke tests, native-action inspection and complete safeguard fixture matrix, WiX/ICE, MSI identity, and shortcut validation. The MSI reported ProductVersion 0.17.1, ProductCode `{2D423617-D9FA-4692-BF59-9441B41B9CC1}`, and preserved UpgradeCode `{93530218-C7A8-4BC1-B4C0-8A670BA3776A}`. GitHub publication and public digest verification passed.

## Final validation and demonstration

From the product root, run Release clean, build, and full tests. Run `packaging/Publish-All.ps1 -Configuration Release -Runtime win-x64` with Windows Installer service access. Verify file versions, asset names, sizes, SHA-256 values, MSI ProductVersion/ProductCode/UpgradeCode, and clean repository state. After publication, compare GitHub asset metadata and digests against the local files and confirm the tag dereferences to the exact release commit.

## Surprises & discoveries

- The installed GitHub CLI does not expose `isLatest` in `gh release view --json`; use the release list and stable publication behavior to verify latest status.
- GitHub release creation took longer than the initial 30-second command window but completed successfully in the same process; no retry or duplicate release was created.

## Risks and mitigations

- Release publication is public and externally visible. Upload only the final validated artifacts and use exact paths.
- The MSI remains unsigned and full lifecycle validation is deferred. State both limits in the release notes.
- Packaging replaces ignored artifact directories. The script constrains those paths under `LogReader/artifacts`; inspect outputs before publication.
- A retry does not erase a failed validation; record the first failure and root cause.

## Deferred work

- Disposable Windows install/upgrade/rollback/uninstall matrix.
- Terminal Server and alternate-user elevation coverage.
- Production code signing after a signing identity/service is chosen.

## Decision log

- 2026-09-16: use patch version 0.17.1 and publish from `main` because the user explicitly requested that release.
- 2026-09-16: retain the existing two-asset format and stable-release presentation established by v0.17.0.

## Outcomes & retrospective

Published [WeezTail 0.17.1](https://github.com/arCarnes/LogReader/releases/tag/v0.17.1) as the latest stable release from `26f8dcff6e54e5a02ceeda769707183cb827c055`. The annotated remote tag dereferences to that exact commit. The portable ZIP is 96,434,577 bytes with SHA-256 `1282BF31598CC947D728EE5534F86D6B341275799B91A75C9E1EAA66A96D958F`; the MSI is 83,783,680 bytes with SHA-256 `483C153362D83E5B1B8EC7A88323C708D12DD38196B8B9D3EC4A9C4F7E6593A4`. GitHub reports matching sizes/digests and uploaded state for both assets. All four packaged executables report FileVersion 0.17.1.0 and ProductVersion `0.17.1+26f8dcff6e54e5a02ceeda769707183cb827c055`.

The release is unsigned and the real disposable-machine lifecycle matrix remains deferred, both stated in the public notes. No runtime or installer source changed during release preparation; only centralized version metadata, the current-release guide line, and this execution plan changed. Final completion documentation requires no repeat build or package generation and intentionally follows the immutable release tag.

## Handoff history

- 2026-09-16: release completed and verified; final documentation record prepared for `main`. No open release action remains.
