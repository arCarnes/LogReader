# WeezTail 1.0.1 — Execution Plan

## Document control

Release requested 2026-09-22. Owner: Codex. Living plan for the 1.0.1 patch release.

## Resume checkpoint

Preparation begins from synchronized, clean `main` at `e81d1e7`. The latest stable release is `v1.0.0`.

## Purpose and observable outcome

Publish WeezTail 1.0.1 from `main` as the stable GitHub release with validated self-contained Windows x64 portable ZIP and MSI artifacts. Product metadata, release tag, source commit, local artifacts, and uploaded assets must agree.

## Scope

- Update centralized product version metadata and current-release documentation to 1.0.1.
- Validate with clean, build, and full solution test flow.
- Build and validate portable ZIP and MSI with `packaging/Publish-All.ps1`.
- Create and push annotated tag `v1.0.1` from the release-preparation commit.
- Publish and verify the stable GitHub release and both assets.
- Record release evidence, limitations, and outcome in this plan.

## Non-goals

- No unrelated runtime changes or dependency upgrades.
- No changes to existing tags or published assets.
- No signing or disposable-machine lifecycle work unless provided by repository tooling.

## Definitions

- Product root: `LogReader/`.
- Release artifacts: `LogReader/artifacts/publish/WeezTail-1.0.1-portable-win-x64.zip` and `LogReader/artifacts/installer/WeezTail.Setup.msi`.
- Exact release commit: the version-preparation commit tagged by `v1.0.1`.

## Existing behavior and evidence

- `LogReader/Directory.Build.props` centralizes product version fields at 1.0.0.
- `LogReader/docs/DeveloperGuide.md` identifies 1.0.0 as the current release line.
- `v1.0.0` is the latest stable release; `main` contains subsequent MCP response compaction and excerpt-layout changes.
- `LogReader/packaging/Publish-All.ps1` validates portable packaging and delegates MSI identity, shortcut, installer-action, and MCP checks.

## Decisions and invariants

- Use exact version `1.0.1` in all product metadata, portable package name, tag, and release title.
- Preserve MSI UpgradeCode and existing packaging safeguards.
- Publish a stable latest release from the exact validated release commit.
- State unsigned status and deferred real-machine lifecycle coverage accurately in release notes.
- Upload only locally validated artifacts; verify public sizes and SHA-256 digests.

## Open questions

None.

## Milestones / issue summary

1. Prepare and commit 1.0.1 metadata and release documentation.
2. Run clean, build, test, and package validation on that source.
3. Tag and push `main` and `v1.0.1`.
4. Publish and verify the GitHub release and assets.

## Progress

- [x] Inspect release procedure, current version, repository state, and prior GitHub release.
- [x] Update 1.0.1 metadata and current-release documentation; commit release preparation (`c1ffe2d`).
- [x] Clean, build, test, and package the exact release source at `c8d3ab27e8917ef788f88ff5bf14f880cfe50eaa`.
- [x] Tag and push `main` and annotated `v1.0.1` at the validated release commit.
- [x] Publish and verify the GitHub release and both assets.

Validation: `dotnet clean LogReader.sln -m:1` passed with zero warnings/errors; `dotnet build LogReader.sln -m:1 /p:NuGetAudit=false` passed with zero warnings/errors; `dotnet test LogReader.sln --no-build --no-restore` passed all 1,463 tests (530 core, 933 WPF). `Publish-All.ps1 -Configuration Release -Runtime win-x64` passed portable layout/ZIP, MCP stdio, installer action safety fixtures, WiX/ICE, MSI identity and shortcut checks. The sandboxed WiX attempt could not access Windows Installer; rerunning the same command with host access passed.

## Final validation and demonstration

From `LogReader/`, run `dotnet clean LogReader.sln -m:1`, `dotnet build LogReader.sln -m:1`, and `dotnet test LogReader.sln` as separate steps, then `packaging/Publish-All.ps1 -Configuration Release -Runtime win-x64`. Verify product versions, package names, MSI identity, artifact sizes, and SHA-256 values locally and through `gh release view`. Confirm the annotated tag resolves to the exact release commit and `main` is clean and synchronized.

## Surprises & discoveries

The sandbox could not access the Windows Installer service required by WiX ICE validation. The same packaging command passed with host access, consistent with the 1.0.0 release run.

## Risks and mitigations

- Release publication is public; publish only final validated artifacts.
- MSI packaging may remain unsigned and full real-machine lifecycle coverage may remain deferred; state these limits accurately.
- Packaging removes and regenerates ignored paths under `LogReader/artifacts`; inspect those paths before publication.

## Deferred work

- Disposable Windows install, upgrade, rollback, and uninstall lifecycle testing remains deferred unless packaging provides it.
- Production code signing remains deferred; artifacts are unsigned.

## Decision log

- 2026-09-22: use 1.0.1 per the user's explicit release request and publish from `main`.

## Outcomes & retrospective

Published [WeezTail 1.0.1](https://github.com/arCarnes/LogReader/releases/tag/v1.0.1) as a stable latest release on 2026-09-22. The annotated `v1.0.1` tag peels to `c8d3ab27e8917ef788f88ff5bf14f880cfe50eaa`; remote `main` was synchronized to that commit at publication. The portable app and MCP binaries report file version `1.0.1.0` and product version `1.0.1+c8d3ab27e8917ef788f88ff5bf14f880cfe50eaa`. MSI validation reported ProductVersion `1.0.1`, ProductCode `{8329AFAD-4381-4870-8CC3-A54696F61C81}`, and preserved UpgradeCode `{93530218-C7A8-4BC1-B4C0-8A670BA3776A}`.

The portable ZIP is 99,530,016 bytes with SHA-256 `34CCEA91EF35C77ACA6EA69B79293D771B0EF006918F57332E54B3D63EEDDA2A`. The MSI is 83,800,064 bytes with SHA-256 `CD5F3A5CF31453D364EF03DF5287621FB641765D70F71BEFAC7845A181AF40A0`. GitHub reports the same uploaded sizes and digests for both assets. The release notes identify both artifacts as unsigned and defer full disposable-machine install, upgrade, rollback, and uninstall lifecycle testing. No runtime source changes were made for this release beyond the already-reviewed MCP work; version metadata and release documentation were updated.

## Handoff history

None.
