# WeezTail 1.0.0 — Execution Plan

## Document control

Release requested 2026-09-16. Owner: Codex. Living plan for the stable 1.0.0 release.

## Resume checkpoint

Release preparation is beginning from `main` at `90f7448`. The working tree is clean and `origin/main` is synchronized.

## Purpose and observable outcome

Publish WeezTail 1.0.0 from `main` as the stable GitHub release with validated self-contained Windows x64 portable ZIP and MSI artifacts. The version metadata, release tag, source commit, local artifacts, and uploaded assets must agree.

## Scope

- Update centralized product version metadata and current-release documentation to 1.0.0.
- Validate the release source with the documented clean, build, and test flow.
- Build and validate the portable ZIP and MSI with `packaging/Publish-All.ps1`.
- Create and push annotated tag `v1.0.0`.
- Publish and verify the stable GitHub release and both artifacts.
- Record release evidence, limitations, and outcome in this plan.

## Non-goals

- No unrelated runtime changes or dependency upgrades.
- No changes to existing release tags or published assets.
- No signing or disposable-machine lifecycle work unless already provided by the repository tooling.

## Definitions

- Product root: `LogReader/`.
- Release artifacts: `LogReader/artifacts/publish/WeezTail-1.0.0-portable-win-x64.zip` and `LogReader/artifacts/installer/WeezTail.Setup.msi`.
- Exact release commit: the version-preparation commit tagged by `v1.0.0`.

## Existing behavior and evidence

- `Directory.Build.props` is the centralized source for four product version fields and currently contains `0.17.1`.
- `docs/DeveloperGuide.md` identifies `0.17.1` as the current release line.
- `Publish-All.ps1` builds and validates the official portable and MSI artifacts, including MCP, installer-action, MSI identity, and shortcut checks.
- `v0.17.1` is the latest stable GitHub release; `main` contains the subsequent scrollbar synchronization fix.

## Decisions and invariants

- Use exact version `1.0.0` for all product metadata, package names, tag, and release title.
- Preserve the existing MSI UpgradeCode and packaging safeguards.
- Publish a stable latest release from the exact validated release commit.
- State unsigned status and any deferred real-machine lifecycle coverage accurately in the release notes.
- Upload only artifacts that pass local validation and whose public sizes and SHA-256 digests can be verified.

## Open questions

None.

## Milestones / issue summary

1. Prepare and commit 1.0.0 metadata and release documentation.
2. Run clean, build, test, and package validation on the exact release source.
3. Tag and push `main` and `v1.0.0`.
4. Publish and verify the GitHub release and artifacts.

## Progress

- [x] Inspect release procedure, current version, repository state, and GitHub authentication.
- [x] Update 1.0.0 metadata and release documentation.
- [x] Commit the release preparation changes as `f386920`.
- [x] Clean, build, test, and package the exact release commit.
- [x] Tag and push `main` and `v1.0.0`.
- [x] Publish and verify the GitHub release.

## Final validation and demonstration

From the product root, run `dotnet clean LogReader.sln -m:1`, `dotnet build LogReader.sln -m:1`, and `dotnet test LogReader.sln` as separate steps. Run `packaging/Publish-All.ps1 -Configuration Release -Runtime win-x64`. Verify product versions, package names, MSI identity, artifact sizes, and SHA-256 values locally and through `gh release view`. Confirm the annotated tag resolves to the exact release commit and that the working tree is clean.

## Surprises & discoveries

- The first restricted packaging attempt reached WiX but could not access the Windows Installer service, producing ICE01–ICE105 errors. The identical authorized host-access rerun passed all packaging checks.
- The annotated local tag object is `89c7fbf77cfa88ad756267a38db5412c3769c564` and peels to the exact release commit `f386920c60001efbb6a1e87ce1e8b40c0cd6c824`.

## Risks and mitigations

- Release publication is public; upload only final validated artifacts.
- MSI packaging may remain unsigned and full real-machine lifecycle coverage may remain deferred; state those limitations rather than overstating fixture coverage.
- Packaging removes and regenerates ignored paths under `LogReader/artifacts`; inspect and verify those paths before publication.

## Deferred work

- Disposable Windows install, upgrade, rollback, and uninstall lifecycle testing remains deferred.
- Production code signing remains deferred; the published artifacts are unsigned.

## Decision log

- 2026-09-16: use 1.0.0 per the user's explicit release request and publish from `main`.

## Outcomes & retrospective

Published [WeezTail 1.0.0](https://github.com/arCarnes/LogReader/releases/tag/v1.0.0) as the latest stable GitHub release from `f386920c60001efbb6a1e87ce1e8b40c0cd6c824`. The release is non-draft and non-prerelease, and the remote `main` branch and annotated `v1.0.0` tag are synchronized.

The full solution build passed with zero warnings and zero errors. All 1,426 tests passed: 493 core tests and 933 WPF tests. `Publish-All.ps1` passed portable layout and ZIP validation, both MCP stdio smoke tests, native installer action inspection and safety fixtures, WiX/ICE, MSI identity, and shortcut validation.

The portable ZIP is 96,434,924 bytes with SHA-256 `CC76FB56115B6B44F91BA4D6DACE7E9984FC2AD559CAFFEF31DDF39BBAEACA5E`. The MSI is 83,767,296 bytes with SHA-256 `5BADB5EAE981B790DFA3977D9C0D4E00E58DA4B284FA21CE294BBD3B011C1B20`. GitHub reports matching uploaded sizes and digests for both assets. Both portable executables report FileVersion `1.0.0.0` and ProductVersion `1.0.0+f386920c60001efbb6a1e87ce1e8b40c0cd6c824`. MSI validation reported ProductVersion `1.0.0`, ProductCode `{8814550F-CB44-4E88-8FEE-0FA908AB2545}`, and the preserved UpgradeCode `{93530218-C7A8-4BC1-B4C0-8A670BA3776A}`.

The release notes state the unsigned status and deferred real-machine lifecycle coverage. No runtime source changed during release preparation beyond the previously validated scrollbar fix; this follow-up only records immutable release evidence.

## Handoff history

None.
