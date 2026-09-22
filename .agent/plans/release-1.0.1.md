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
- [ ] Update 1.0.1 metadata and current-release documentation; commit release preparation.
- [ ] Clean, build, test, and package the exact release commit.
- [ ] Tag and push `main` and `v1.0.1`.
- [ ] Publish and verify the GitHub release.

## Final validation and demonstration

From `LogReader/`, run `dotnet clean LogReader.sln -m:1`, `dotnet build LogReader.sln -m:1`, and `dotnet test LogReader.sln` as separate steps, then `packaging/Publish-All.ps1 -Configuration Release -Runtime win-x64`. Verify product versions, package names, MSI identity, artifact sizes, and SHA-256 values locally and through `gh release view`. Confirm the annotated tag resolves to the exact release commit and `main` is clean and synchronized.

## Surprises & discoveries

None so far.

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

Pending release completion.

## Handoff history

None.
