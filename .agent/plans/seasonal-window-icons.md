# Seasonal window icons — Execution Plan

## Document control
Created 2026-10-21. Branch: `codex/halloween-cat-icon`. Implementation authorized by the user from the approved conversational plan.

## Resume checkpoint
Beginning implementation. Repository clean; existing icons and taskbar registration inspected. Next: prepare approved Halloween asset, implement resolver/controller, validate and commit.

## Purpose and observable outcome
Running WPF windows automatically use the transparent Halloween cat during the Monday–Sunday week containing October 31, local time, and revert afterward. Adding another holiday requires an asset and catalog entry only.

## Scope
Application resources, annual date rules/catalog, application-owned refresh controller, Halloween asset/provenance, focused and integration tests.

## Non-goals
No shortcut/executable/launcher icon changes, Shell pin manipulation, settings, external configuration, network access, version bump, push or release.

## Definitions
Halloween week begins the Monday on/before October 31 and ends the following Sunday inclusive. Seasonal selection uses local calendar dates. Highest priority wins, declaration order breaks ties.

## Existing behavior and evidence
`App.xaml` supplies a fixed `Assets/favicon.ico` Window style setter. The executable embeds the same icon. `App` owns startup/shutdown; `TaskbarRelaunchProperties` and `SharedTaskbarShortcut` maintain stable launcher identity and icons. `WpfTestHost` serializes resource/window tests. Native tests use `Category=InteractiveDesktop`.

## Decisions and invariants
Use a shared dynamic icon resource, initialized before startup UI. Refresh every minute and on activation; dispatcher-only updates; stop timer and unsubscribe on exit/startup failure. Cache decoded icons; a seasonal load failure traces and falls back to standard. Annual rules support holiday week and inclusive fixed ranges (including New Year). Use the latest approved generated transparent face, without redesign. Keep standard assets untouched.

## Open questions
None. Windows may retain the stable shortcut icon for pinned groups; changing pins is outside scope.

## Milestones / issue summary
1. Asset preparation/provenance.
2. Resolver and WPF integration with deterministic tests.
3. Focused/full validation, desktop checks, documentation and local commits.

## Progress
- [x] Asset exports and visual QA.
- [ ] Date rules, catalog and resolver.
- [ ] Controller, lifecycle and dynamic resources.
- [ ] Deterministic and desktop regression tests.
- [ ] Focused then broader validation.
- [ ] Final review and local commits.

## Seasonal icons
- State: in progress.
- Dependencies: approved master at `C:/Users/austi/.codex/generated_images/01a15ba5-395a-7fb1-86f9-bd32267433b6/exec-bc583d6f-54e2-4a36-b8a0-eea2bd1539bc.png`.
- Purpose: reusable automatic seasonal window icons.
- Expected implementation areas: App resources/lifecycle, new internal services, tests, `cat/seasonal/halloween`.
- Tasks: preserve master; export all approved sizes; add rules/catalog; integrate lifecycle/resource updates; add tests; document extension; validate.
- Acceptance criteria: Halloween all seven weekdays and boundaries; synthetic season ranges/overlaps; startup, rollover, date jumps, cache, load failure, disposal; existing/new windows; stable relaunch behavior.
- Focused validation: Release test-project build and seasonal tests, then taskbar/shortcut tests.
- Progress/evidence: approved 1254px RGBA master copied verbatim; ten PNG/ICO sizes verified with Pillow; light/dark contact sheet visually inspected (16–128px). All frames retain transparent exterior and opaque interior. Resolver/controller/tests implemented; first focused Release build running.

## Final validation and demonstration
Planned: focused Release build/tests, solution Release build, full service-safe tests, separate interactive desktop script; inspect small asset frames on light/dark backgrounds. Record actual results and exclusions here.

## Surprises & discoveries
Existing `cat/` includes the normal icon master/exports and a Pillow export precedent. Preserve them. Release validation avoids the Debug target that stops running application instances.
Initial focused build (including an escalated retry) could not find SDK 10.0.402 because PATH selects the machine .NET 8 installation. Prior plans identify `C:\Users\austi\.dotnet`; rerun with DOTNET_ROOT and PATH set to that user-local SDK. Restore reported NU1900 vulnerability-feed access warnings; review/retry restore outside the sandbox after build completion.

## Risks and mitigations
Windows Shell caching may preserve pinned icons: explicitly outside acceptance. Original painting attribution is unknown: document accurately. Shared WPF state: restore resources and close windows in finally. Timer ownership: deterministic disposal tests and failed-startup coverage.

## Deferred work
Christmas asset/catalog entry; shortcut/pin seasonality; release/version publication.

## Decision log
2026-10-21: user selected running windows only and latest generated transparent-face preview. Approved reusable internal catalog/rules, minute/activation refresh, no UI/configuration.

## Outcomes & retrospective
Pending implementation and validation.

## Handoff history
2026-10-21: implementation begun by primary agent; no delegation.
