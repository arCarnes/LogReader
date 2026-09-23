# WeezTail 1.1.0 — Execution Plan

## Document control

Requested 2026-09-22. Owner: Codex. Living plan for the 1.1.0 release.

## Resume checkpoint

`main` is clean and two commits ahead of `origin/main`: `88e3089` adds filtered MCP log tailing, and `f3b1155` bounds its memory use. Latest stable release is `v1.0.1`.

## Purpose and observable outcome

Publish WeezTail 1.1.0 from `main` as a stable GitHub release with validated Windows x64 portable ZIP and MSI. Product metadata, source tag, release notes, and uploaded assets agree.

## Scope

- Advance version metadata and current-release documentation to 1.1.0.
- Validate the complete solution and both release packages.
- Commit release preparation and evidence, then push `main` and annotated `v1.1.0`.
- Publish and verify the GitHub release with change notes and both assets.

## Non-goals

- No additional runtime changes, dependency upgrades, signing, or unrelated refactors.

## Definitions

- Product root: `LogReader/`.
- Artifacts: `LogReader/artifacts/publish/WeezTail-1.1.0-portable-win-x64.zip` and `LogReader/artifacts/installer/WeezTail.Setup.msi`.
- Release source: the commit tagged `v1.1.0` and used for final packaging.

## Existing behavior and evidence

- `LogReader/Directory.Build.props` currently declares 1.0.1, and `LogReader/docs/DeveloperGuide.md` names 1.0.1 as the current release line.
- The two already committed changes add literal/regex filtering to `read_log_tail`, filter-bound cursor behavior, response size measurements, and an 8 MiB line/batch read bound with a distinct oversized-line error.
- The implementation plan records a zero-warning solution build, 1,481 passing tests (548 Core, 933 WPF), and focused test and packaged stdio measurements after the memory fix.
- `packaging/Publish-All.ps1` publishes and validates the portable ZIP and MSI.

## Decisions and invariants

- Use 1.1.0 in all centralized product metadata, package naming, tag, and release title.
- Preserve existing MSI identity and release packaging safeguards.
- Publish only artifacts built from the tagged commit; verify local and GitHub sizes and SHA-256 digests.
- Disclose unsigned artifacts and deferred disposable-machine lifecycle testing.

## Open questions

None.

## Milestones / issue summary

1. Prepare version and release documentation; commit.
2. Build, test, and package the exact release source.
3. Tag and push `main` and `v1.1.0`.
4. Publish and verify release notes and assets.

## Progress

- [x] Inspect repository status, versioning, packaging workflow, and previous release.
- [ ] Prepare and commit 1.1.0 metadata and documentation.
- [ ] Validate full solution and release packages.
- [ ] Tag and push release source.
- [ ] Publish and verify GitHub release.

## Final validation and demonstration

Run solution build and tests, then `packaging/Publish-All.ps1 -Configuration Release -Runtime win-x64`. Check version metadata, executable versions, MSI ProductVersion, artifact hashes, tag commit, remote synchronization, and GitHub asset digests.

## Surprises & discoveries

None yet.

## Risks and mitigations

- Packaging regenerates ignored outputs under `LogReader/artifacts`; inspect outputs before uploading.
- The MSI may need host access for WiX ICE validation, as in 1.0.1.
- Upload only validated files and record the unsigned status.

## Deferred work

Production signing and full disposable-machine install, upgrade, rollback, and uninstall lifecycle testing.

## Decision log

- 2026-09-22: Use 1.1.0 per the user's release request and include both committed filtered-tail changes.

## Outcomes & retrospective

Pending release.

## Handoff history

None.
