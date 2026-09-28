# WeezTail 1.1.0 — Execution Plan

## Document control

Requested 2026-09-22. Owner: Codex. Living plan for the 1.1.0 release.

## Resume checkpoint

The stable 1.1.0 release is published at `v1.1.0`, which peels to `ad86fd37da2a5740bab3ee75d5ad6fc07ccd10df`. The GitHub Actions build/test run and manual release packaging passed. This plan records final evidence after the tagged source commit.

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
- [x] Add and verify GitHub Actions build/test workflow (`ad86fd3`; [successful run](https://github.com/arCarnes/LogReader/actions/runs/35828032654)).
- [x] Validate full solution through GitHub Actions and both release packages locally.
- [x] Tag and push release source.
- [x] Publish and verify GitHub release and both asset hashes.

Validation before deferral: `dotnet build LogReader.sln -m:1 /p:NuGetAudit=false` passed with zero warnings/errors. The WPF test project passed 933/933; the full solution test command was interrupted while the Core project was running, so its result is not established for this release.

## Final validation and demonstration

GitHub Actions ran `dotnet build LogReader/LogReader.sln -c Release -m:1` and `dotnet test LogReader/LogReader.sln -c Release --no-build --no-restore -m:1` on `windows-2022`; both steps passed on the tagged source. `packaging/Publish-All.ps1 -Configuration Release -Runtime win-x64` passed portable layout/ZIP, MCP stdio for both payloads, installer action fixtures, WiX build, MSI identity, and shortcut checks. WiX reported zero warnings and errors.

The portable app and MCP executable report file version `1.1.0.0` and product version `1.1.0+ad86fd37da2a5740bab3ee75d5ad6fc07ccd10df`. MSI validation reported ProductVersion `1.1.0`, ProductCode `{F51175C3-62D0-4F96-86F8-B76A15DBC941}`, and preserved UpgradeCode `{93530218-C7A8-4BC1-B4C0-8A670BA3776A}`. The portable ZIP is 99,543,443 bytes with SHA-256 `CE011B047DEB429BC7B8E11A69139686CEAC98D31DA1D7A512CD143E7EAE81BC`; the MSI is 83,820,544 bytes with SHA-256 `67D8FFE27C4DE5537BFA0AB309AB4A209F00F71637E54F7AF33C9DE1DD8D26DA`. GitHub reports identical sizes and hashes.

The packaged UI reached input idle in 714 ms; after five seconds its working set was 153,444,352 bytes and private memory 116,015,104 bytes. A representative 50-file/100-line packaged MCP measurement exited successfully with clean stderr and no partial responses; filtered and unfiltered idle tail responses were 719 protocol bytes each. These are single-sample measurements, not performance guarantees.

## Surprises & discoveries

The user paused release completion to add a further feature and consider GitHub Actions CI. The feature was committed before the release resumed. The repository had no workflows before this release; the first Windows hosted run passed. The GitHub API did not grant access to the raw Actions log archive, but the run and its build/test step conclusions are available through the run summary.

## Risks and mitigations

- Packaging regenerates ignored outputs under `LogReader/artifacts`; inspect outputs before uploading.
- The MSI may need host access for WiX ICE validation, as in 1.0.1.
- Upload only validated files and record the unsigned status.

## Deferred work

Production signing, full disposable-machine install/upgrade/rollback/uninstall lifecycle testing, and automated release publication.

## Decision log

- 2026-09-22: Use 1.1.0 per the user's release request and include both committed filtered-tail changes.
- 2026-09-22: Defer packaging, push, tag, and publication until the additional feature is defined and integrated. Explore a Windows GitHub Actions build/test workflow separately.
- 2026-09-23: Resume 1.1.0 with the committed idle-tail response feature. Add Windows hosted build/test CI; keep packaging and GitHub release publication manual for now.
- 2026-09-23: Tag the CI-validated `ad86fd3` source, package locally from that commit, and publish the verified assets as a stable latest release.

## Outcomes & retrospective

Published [WeezTail 1.1.0](https://github.com/arCarnes/LogReader/releases/tag/v1.1.0) as a stable release on 2026-09-23. The version metadata, three MCP changes, Windows CI workflow, documentation, and release plan changed. CI passed on the exact source commit used for both validated packages. The release notes explain filtered and compact tail response behavior, the client cursor reuse requirement, validation, unsigned artifacts, and deferred lifecycle coverage. GitHub asset sizes and digests match the local packages. Automated release publication remains deferred.

## Handoff history

None.
