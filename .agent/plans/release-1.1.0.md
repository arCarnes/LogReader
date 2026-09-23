# WeezTail 1.1.0 — Execution Plan

## Document control

Requested 2026-09-22. Owner: Codex. Living plan for the 1.1.0 release.

## Resume checkpoint

Release preparation is committed at `d35f53a`. The additional idle-tail response change is committed at `5adb3a5`. The user resumed the release with GitHub Actions build/test validation and a manual release process; automated release publication remains deferred.

## Purpose and observable outcome

Publish WeezTail 1.1.0 from `main` as a stable GitHub release with validated Windows x64 portable ZIP and MSI. Product metadata, source tag, release notes, and uploaded assets agree.

## Scope

- Advance version metadata and current-release documentation to 1.1.0.
- Add a Windows GitHub Actions workflow for solution build and tests on main, pull requests, and manual dispatch.
- Validate the complete solution and both release packages.
- Commit release preparation and evidence, then push `main` and annotated `v1.1.0`.
- Publish and verify the GitHub release with change notes and both assets.

## Non-goals

- No additional runtime changes beyond the committed idle-tail feature, dependency upgrades, signing, or unrelated refactors.
- No automated packaging or release publication workflow.

## Definitions

- Product root: `LogReader/`.
- Artifacts: `LogReader/artifacts/publish/WeezTail-1.1.0-portable-win-x64.zip` and `LogReader/artifacts/installer/WeezTail.Setup.msi`.
- Release source: the commit tagged `v1.1.0` and used for final packaging.

## Existing behavior and evidence

- `LogReader/Directory.Build.props` and `LogReader/docs/DeveloperGuide.md` now identify 1.1.0.
- The two already committed changes add literal/regex filtering to `read_log_tail`, filter-bound cursor behavior, response size measurements, and an 8 MiB line/batch read bound with a distinct oversized-line error.
- `5adb3a5` compacts idle MCP tail responses, with contract, backend, measurement, and guide changes.
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
- [x] Prepare and commit 1.1.0 metadata and documentation (`d35f53a`).
- [x] Integrate the additional idle-tail response feature (`5adb3a5`).
- [ ] Add and verify GitHub Actions build/test workflow.
- [ ] Validate full solution and release packages.
- [ ] Tag and push release source.
- [ ] Publish and verify GitHub release.

Validation before deferral: `dotnet build LogReader.sln -m:1 /p:NuGetAudit=false` passed with zero warnings/errors. The WPF test project passed 933/933; the full solution test command was interrupted while the Core project was running, so its result is not established for this release.

## Final validation and demonstration

Run solution build and tests, then `packaging/Publish-All.ps1 -Configuration Release -Runtime win-x64`. Check version metadata, executable versions, MSI ProductVersion, artifact hashes, tag commit, remote synchronization, and GitHub asset digests.

## Surprises & discoveries

The user paused release completion to add a further feature and consider GitHub Actions CI. The feature is now committed. Actions is enabled on GitHub, and the repository had no workflow files before this release.

## Risks and mitigations

- Packaging regenerates ignored outputs under `LogReader/artifacts`; inspect outputs before uploading.
- The MSI may need host access for WiX ICE validation, as in 1.0.1.
- Upload only validated files and record the unsigned status.

## Deferred work

Production signing and full disposable-machine install, upgrade, rollback, and uninstall lifecycle testing.

## Decision log

- 2026-09-22: Use 1.1.0 per the user's release request and include both committed filtered-tail changes.
- 2026-09-22: Defer packaging, push, tag, and publication until the additional feature is defined and integrated. Explore a Windows GitHub Actions build/test workflow separately.
- 2026-09-23: Resume 1.1.0 with the committed idle-tail response feature. Add Windows hosted build/test CI; keep packaging and GitHub release publication manual for now.

## Outcomes & retrospective

Pending release.

## Handoff history

None.
