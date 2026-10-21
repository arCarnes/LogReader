# Seasonal window icons — Execution Plan

## Document control
Created 2026-10-21. Branch: `codex/halloween-cat-icon`. Implementation authorized by the user from the approved conversational plan.

## Resume checkpoint
Implementation and validation complete on `codex/halloween-cat-icon`. Asset milestone: `03352f1`; the final feature commit includes this completion record. All focused, broad and native desktop checks passed. No required implementation work remains; no push, version bump or release performed.

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
- [x] Date rules, catalog and resolver.
- [x] Controller, lifecycle and dynamic resources.
- [x] Deterministic and desktop regression tests (all passed).
- [x] Focused then broader validation.
- [x] Final review and local commits (asset commit plus final feature commit containing this record).

## Seasonal icons
- State: complete.
- Dependencies: approved master at `C:/Users/austi/.codex/generated_images/01a15ba5-395a-7fb1-86f9-bd32267433b6/exec-bc583d6f-54e2-4a36-b8a0-eea2bd1539bc.png`.
- Purpose: reusable automatic seasonal window icons.
- Expected implementation areas: App resources/lifecycle, new internal services, tests, `cat/seasonal/halloween`.
- Tasks: preserve master; export all approved sizes; add rules/catalog; integrate lifecycle/resource updates; add tests; document extension; validate.
- Acceptance criteria: Halloween all seven weekdays and boundaries; synthetic season ranges/overlaps; startup, rollover, date jumps, cache, load failure, disposal; existing/new windows; stable relaunch behavior.
- Focused validation: Release test-project build and seasonal tests, then taskbar/shortcut tests.
- Progress/evidence: approved 1254px RGBA master copied verbatim; ten PNG/ICO sizes verified with Pillow; light/dark contact sheet visually inspected (16–128px). All frames retain transparent exterior and opaque interior. Resolver/controller/tests implemented; first focused Release build running.

## Final validation and demonstration
Planned: focused Release build/tests, solution Release build, full service-safe tests, separate interactive desktop script; inspect small asset frames on light/dark backgrounds. Record actual results and exclusions here.
First focused test-project Release build passed (0 errors; 7 NU1900 warnings), using DOTNET_ROOT/PATH for the user-local SDK. Retried restore outside the sandbox; dependency restore succeeded but the vulnerability feed remains unavailable. Do not disable audit or alter dependencies to hide the warning.
Incremental Release rebuild passed (0 errors; 4 NU1900 warnings). First focused test run: 23/27 passed, four failures due to unqualified pack URIs resolving against the test-host entry assembly. Repair: qualify both standard and seasonal URIs with `WeezTail;component`, without changing WpfTestHost global ResourceAssembly. Rebuild and rerun before broader validation.
After URI repair: 26/27 focused tests passed; actual resource decoding/transparency and startup selection now pass. Window test observed null Icon before its style had been applied. Exercise actual hidden/shown windows with the existing WpfTestHost pattern before asserting icon application, then rerun; preserve the existing/new/restored identity assertions.
Hidden-window rerun still failed the same assertion (26/27 passed). Root cause: implicit base Window styles do not apply to the app's derived window classes. Decision before implementation adjustment: add an explicit `Icon="{DynamicResource AppWindowIconResource}"` to all eight XAML window roots, matching the existing explicit shared theme references. Keep the central resource/controller and the base style for ordinary Window instances. Extend regression coverage to all window classes; no workaround in test fixtures.
After explicit window references: focused Release build passed (0 errors; 4 NU1900 warnings) and `dotnet test LogReader/LogReader.Tests/LogReader.Tests.csproj -c Release --no-build --no-restore -m:1 --filter "FullyQualifiedName~Seasonal&Category!=InteractiveDesktop" --logger trx --results-directory artifacts/test-results/seasonal-focused` passed 28/28, none skipped. Latest result: `austi_GAMING_2026-10-21_02_16_22_net10.0.trx`. Covers all eight app window classes before showing, existing/new shown windows, decoded icon frames/alpha, dates, activation, cleanup and fallback.
Full `dotnet build LogReader/LogReader.sln -c Release -m:1` passed (0 errors; 8 NU1900 audit-feed warnings). Relevant existing startup/taskbar/shortcut regressions initially passed 65/67 in the sandbox: two native shortcut registration assertions failed. Retried the identical command outside the sandbox to restore Shell COM access: 67/67 passed, none skipped (`artifacts/test-results/seasonal-regression/austi_GAMING_2026-10-21_02_17_44_net10.0.trx`). No code/test changes made for this environment restriction. Full service-safe solution tests are now running outside the sandbox, explicitly excluding `Category=InteractiveDesktop`.
Native test refinement: use an off-screen visible taskbar window rather than WpfTestHost.ShowHidden (which disables ShowInTaskbar and substitutes an empty style). This exercises real window icon handles while preserving explicit taskbar relaunch metadata; keep it in InteractiveDesktop. Rebuild before executing that lane. Production and service-safe test behavior unchanged by this test-only refinement.
Full service-safe command `dotnet test LogReader/LogReader.sln -c Release --no-build --no-restore -m:1 --filter "Category!=InteractiveDesktop" --logger trx --results-directory artifacts/test-results/seasonal-service-safe` passed 2,187/2,187 (1,391 app/integration + 796 core), no failures/skips. Desktop-dependent coverage was explicitly excluded here and is executed separately below. Result files: `austi_GAMING_2026-10-21_02_17_55_net10.0.trx` and `austi_GAMING_2026-10-21_02_18_44_net10.0.trx`.
Final test-project Release rebuild after the native-only refinement passed (0 errors; 3 NU1900 warnings). `& ./.github/scripts/Invoke-InteractiveDesktopTests.ps1` then passed 5/5 actual desktop tests, none skipped, outside the sandbox. Result: `artifacts/test-results/interactive-desktop/e63be206dc904a95bb1db717d768e519/austi_GAMING_2026-10-21_02_20_33_net10.0.trx`. This includes the new native icon pixel change/restoration test with ShowInTaskbar enabled, plus existing desktop/taskbar checks. No machine clock changes or startup sleeps used. `git diff --check` passed before final staging.

## Surprises & discoveries
Existing `cat/` includes the normal icon master/exports and a Pillow export precedent. Preserve them. Release validation avoids the Debug target that stops running application instances.
WpfTestHost.ShowHidden deliberately suppresses taskbar visibility and implicit styling. All application windows now bind the shared icon explicitly and are covered before showing; the native lane uses an off-screen real taskbar window. The original painting's title/artist remain unidentified, recorded in asset provenance.
Initial focused build (including an escalated retry) could not find SDK 10.0.402 because PATH selects the machine .NET 8 installation. Prior plans identify `C:\Users\austi\.dotnet`; rerun with DOTNET_ROOT and PATH set to that user-local SDK. Restore reported NU1900 vulnerability-feed access warnings; review/retry restore outside the sandbox after build completion.

## Risks and mitigations
Windows Shell caching may preserve pinned icons: explicitly outside acceptance. Original painting attribution is unknown: document accurately. Shared WPF state: restore resources and close windows in finally. Timer ownership: deterministic disposal tests and failed-startup coverage.

## Deferred work
Christmas asset/catalog entry; shortcut/pin seasonality; release/version publication.

## Decision log
2026-10-21: user selected running windows only and latest generated transparent-face preview. Approved reusable internal catalog/rules, minute/activation refresh, no UI/configuration.
2026-10-21: use an ImageSource resource conversion for the standard ICO (same conversion as the original style setter), retaining multi-frame decoding. Seasonal images use cached frozen BitmapFrames. February 29 week rules skip non-leap years; date ranges compare actual month/day values. Native tests compare window icon pixels and verify stable relaunch metadata; manual pinned-taskbar presentation remains a separate observation.
2026-10-21: qualify pack URIs to the WeezTail assembly so resource loading is independent of the entry assembly; focused integration coverage exposed this requirement.

## Outcomes & retrospective
Observable outcome: the approved Halloween face appears automatically for October 26–November 1 in 2026 (and the correct holiday week annually), updates already-open/new windows, and restores afterward. Other seasons need an embedded asset and catalog entry only. Local-date range rules, priorities, image cache/fallback, startup/activation/minute refresh and deterministic disposal are implemented without launcher/shortcut/version changes.

Changed components: internal date rules/catalog/resolver and WPF controller; App resource/startup/exit integration; one shared-resource reference in each of eight window roots; two regression-test classes; Halloween master, ten PNG frames, ICO/export script and provenance/extension guide; this living plan. The master is byte-identical to the approved generated preview and the embedded ICO matches its source export.

Validation outcome: 28 focused seasonal tests, 67 existing relevant startup/taskbar/shortcut tests, 2,187 full service-safe tests and 5 separate native desktop tests all passed; solution Release build passed. Focused integration checks exposed entry-assembly URI ambiguity and implicit-style assumptions, which were fixed rather than bypassed. Existing Shell COM regressions required execution outside the sandbox.

Remaining limits: NuGet vulnerability-feed access produced NU1900 warnings even after an external restore retry, so vulnerability audit freshness is unverified. Windows Shell may retain stable shortcut icons for pinned groups; no pin/cache manipulation or manual visual pinned-group acceptance is claimed. Source painting attribution/rights remain unidentified as documented. Christmas and release publication are deferred by scope.

## Handoff history
2026-10-21: implementation begun by primary agent; no delegation.
2026-10-21: completed implementation and all planned automated validation; final local feature commit prepared after review. No external publication.
