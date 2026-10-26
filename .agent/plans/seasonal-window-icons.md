# Seasonal window icons — Execution Plan

## Document control
Created 2026-10-21. Branch: `codex/halloween-cat-icon`. Implementation authorized by the user from the approved conversational plan.

## Resume checkpoint
2026-10-26: generated Start menu shortcut milestone implemented and validated on codex/seasonal-shortcut-icons. Final solution Release build passed with 0 warnings/errors; final service-safe suite passed 2,197/2,197; focused suite 86/86 and two final focused repeats 10/10 each. The feature commit contains this completion record. Earlier existing-pin investigation remains deferred and unproven. No desktop interaction, pin edits, version bump, push or release. No required work remains for the narrowed milestone.

## Purpose and observable outcome
Running WPF windows and the launcher deployment's generated WeezTail Shared Start menu shortcut automatically use the transparent Halloween cat during the Monday–Sunday week containing October 31, local time, and revert afterward. Adding another holiday requires an asset and catalog entry only. The shortcut catches up on the next launch if the application was closed at a date boundary.

## Scope
Application resources, annual date rules/catalog, application-owned refresh controller, Halloween asset/provenance, generated shared-launcher Start menu shortcut and per-user seasonal ICO cache, focused and integration tests. Historical milestones below retain their original evidence; the revised 2026-10-26 scope supersedes the earlier pin feasibility gate.

## Non-goals
Original window-icon milestone excluded shortcuts. The revised 2026-10-26 extension implements changes to the generated shared-deployment Start menu shortcut and persistent local seasonal ICO files. No executable/launcher icon changes, direct pinned-shortcut edits, re-pinning existing entries, Explorer restarts, system icon-cache clearing, desktop/installer/user shortcut edits, settings, background helper, version bump, push or release.

## Definitions
Halloween week begins the Monday on/before October 31 and ends the following Sunday inclusive. Seasonal selection uses local calendar dates. Highest priority wins, declaration order breaks ties.

## Existing behavior and evidence
`App.xaml` supplies a fixed `Assets/favicon.ico` Window style setter. The executable embeds the same icon. `App` owns startup/shutdown; `TaskbarRelaunchProperties` and `SharedTaskbarShortcut` maintain stable launcher identity and icons. `WpfTestHost` serializes resource/window tests. Native tests use `Category=InteractiveDesktop`.

## Decisions and invariants
Use a shared dynamic icon resource, initialized before startup UI. Refresh every minute and on activation; dispatcher-only updates; stop timer and unsubscribe on exit/startup failure. Cache decoded icons; a seasonal load failure traces and falls back to standard. Annual rules support holiday week and inclusive fixed ranges (including New Year). Use the latest approved generated transparent face, without redesign. Keep standard assets untouched.

## Open questions
No open product questions for generated Start menu shortcut switching/restoration. Existing taskbar pin support is deferred and unproven; the user explicitly superseded its earlier feasibility gate by requesting generated Start menu shortcut support first.

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

## Seasonal shortcut/taskbar extension (2026-10-26)

- State: feasibility investigation interrupted; production integration gated and unproven.
- Purpose: generated WeezTail Shared Start menu shortcuts and existing associated taskbar pins automatically follow the existing holiday calendar and revert without another release.
- Authorization: approved plan in this chat; explicit branch creation requested. Use the computer-use skill for native desktop observation. No delegation.
- Decisions: shared-deployment generated shortcut only; app-driven startup/activation/minute refresh; retain last icon while closed and catch up on next launch. Use supported Shell notifications only; leave existing pinned shortcut files untouched. Existing pins must follow automatically, without manual migration/recreation.
- First milestone: create a disposable shared deployment and pin using current behavior; keep stable launcher identity and target. Extract the bundled standard/Halloween ICO bytes to a per-user persistent icon path isolated by deployment identity, outside release directories. Update only the generated shortcut and issue targeted Shell notifications. Inject standard/holiday/standard dates without changing the system clock. Observe actual existing-pin pixels, grouping, relaunch and post-close appearance.
- Gate: visible existing pin switches both ways without recreation/direct modification and still launches/groups correctly. Test UNC and mapped-drive deployments independently where available. Do not claim support for environments not demonstrated. If the gate fails, stop and record evidence rather than implement shortcut-only seasonality.
- Conditional integration: reuse existing seasonal resolver/lifecycle; internal Shell icon coordinator, atomic content-aware ICO updates and cross-process serialization per deployment. Preserve name/target/arguments/identity. Retain valid files after expected IO/Shell failures and retry on later refreshes. No public API changes.
- Conditional validation: focused Release build and seasonal/taskbar/shortcut tests, solution Release build and service-safe tests, separate InteractiveDesktop lane. Add deterministic transition, catch-up, idempotency, isolation, concurrency and failure coverage only if integration proceeds. Desktop evidence must be actual visible pin behavior; property-store/native-window-icon assertions alone do not satisfy the gate.
- Progress/evidence: repository started clean; branch created. Read PLANS.md and existing seasonal plan in full. Read computer-use skill, guidance, confirmations and API documentation; initialized @oai/sky. No production code changed.
- Evidence: ignored scratch source is in artifacts/seasonal-shell-probe/source (Probe.csproj, Probe.cs, copied SharedTaskbarShortcut.cs). It links existing resolver/controller/relaunch/preflight code and bundled assets; the copied shortcut code has one experimental icon-path override. dotnet build artifacts/seasonal-shell-probe/source/Probe.csproj -c Release -m:1 --nologo using the user-local .NET 10 SDK passed with 0 warnings/errors. No tests or actual pin experiment ran. Desktop inventory was accessible, but opening Explorer via Computer Use was stopped by the user with physical Escape; no further UI actions taken. No generated Start menu shortcut or pin was created. Scratch probe is not production integration and is not committed.
- Next action: correct the scratch SHUpdateImage notification to use the old icon location/index (capture before updating the shortcut), assemble the disposable deployment with the existing launcher, then resume UI observation only after the user resumes Computer Use. Preserve the hard gate; do not infer success from the build.

### Extension decision log

2026-10-26: user selected app-created shortcut only, app-driven updates, mandatory existing-pin support, and Shell refresh only. This supersedes the original plan's deferred shortcut investigation but does not relax any pin-editing boundary.

2026-10-26: user clarified the launcher deployment is the mainline distribution. Treat WeezTail.Launcher.exe -> current.json -> releases/<version>/WeezTail.exe as the primary acceptance path, using the existing per-deployment identity and generated WeezTail Shared shortcut. MSI and direct standalone deployments are not required for this extension. Desktop remains unavailable for automation while the user is using the computer; background preparation is allowed, but the mandatory visible-pin gate remains unexecuted.

### Extension outcomes and retrospective

Feasibility experiment not executed: Computer Use stopped by physical Escape before fixture deployment/pinning. Branch codex/seasonal-shortcut-icons exists; production code remains unchanged. Only the scratch harness build passed. No release, push, version change, or implementation commit. Original window-icon behavior and historical validation evidence above remain unchanged.

## Generated Start menu shortcut milestone (2026-10-26 revised scope)

- State: complete; implementation, focused validation, broad validation and documentation passed.
- Purpose: the mainline launcher deployment's generated WeezTail Shared Start menu shortcut uses the same holiday icon as running windows and automatically restores its original launcher icon after the holiday.
- Authority and scope change: user explicitly asked to start with generated Start menu shortcut switching/restoration. This supersedes the earlier requirement that existing pins pass first; taskbar pin support remains unproven and deferred. Do not automate the user's desktop.
- Design decision before implementation: use immutable, content-hashed seasonal ICO files under %LocalAppData%/WeezTail/ShellIcons/<deployment identity>/, outside versioned releases, and change only the generated shortcut's icon reference. Standard dates restore its existing launcher icon reference. Changing the .lnk icon path and notifying that shortcut avoids replacing icon bytes at a path the Shell may cache. Assets are bundled, with no network fetches or executable changes.
- Lifecycle: one optional callback from the existing SeasonalWindowIconController; same effective icon selection, startup/activation/minute refresh, failure fallback and disposal. Construct a per-deployment coordinator alongside production startup UI wiring; the factory uses its registration status for the existing taskbar relaunch fallback without registering the standard icon over the seasonal one.
- Writes and failure handling: bound per-user/deployment cross-process serialization (nonblocking mutex acquisition), atomic ICO and link replacement, no writes for unchanged content/metadata, preserve unrelated or invalid links, retain old valid files on expected failures and retry later. If a seasonal asset cannot load, register the standard launcher icon for that refresh and retry seasonal loading later. No new dependencies/public APIs.
- Acceptance: normal -> holiday -> normal, startup inside a holiday, restart after a holiday, repeated unchanged refreshes, cached files surviving release promotion, resource/file/registration failures and recovery, deployment isolation and deterministic lock contention. Existing shortcut target/arguments/name/identity remain intact; factory/lifecycle regressions pass.
- Validation: temporary Programs/local-cache fixtures only. Build Release test project, focused seasonal/shortcut/taskbar service-safe tests, solution Release build and full Category!=InteractiveDesktop tests. Explicitly exclude the interactive lane because the user is using the desktop; do not claim visual Explorer or taskbar-pin verification. Inspect diff and create a relevant local commit after validation. No push/release/version bump.
- Completion: all acceptance criteria for the narrowed generated-shortcut milestone are satisfied by actual COM shortcut round trips and bundled-byte assertions in isolated temporary fixtures. Final review/diff check passed; stage only feature files and this plan for the local commit. No required subsequent implementation work; desktop/pin investigations remain optional deferred work.

### Revised-scope outcomes and retrospective

Observable outcome: the generated launcher Start menu shortcut follows the existing effective seasonal icon at startup, activation and minute refresh; it restores the original launcher icon after the holiday and catches up on the next launch. Cached bundled icons survive release-folder changes. Immutable SHA-256-named files and idempotent registration avoid unchanged writes. Expected failures preserve working files and retry, using the launcher icon when seasonal cache preparation fails. Cross-process updates are serialized per user/deployment across logon sessions. No launcher/executable/identity/arguments changes or public API/dependency changes.

Components changed: new internal SeasonalShortcutIconCoordinator; optional existing-controller callback; App startup composition; SharedTaskbarShortcut icon-path/idempotency support; new focused regression class, controller callback test and real-change lock test refinement; installation/asset documentation; this living plan. Earlier scratch pin probe remains ignored under artifacts and is not delivered.

Validation: final dotnet build LogReader/LogReader.sln -c Release -m:1 --nologo --no-restore passed (0 warnings/errors). Final dotnet test LogReader/LogReader.sln -c Release --no-build --no-restore -m:1 --filter Category!=InteractiveDesktop --logger trx --results-directory artifacts/test-results/seasonal-shortcut-final passed 1,401 app/integration + 796 core = 2,197/2,197, no skips. Focused seasonal/shortcut/taskbar tests passed 86/86. Two final focused repeats (SeasonalShortcutIconTests plus Controller_ShortcutUsesEffectiveIcon) passed 10/10 each under artifacts/test-results/seasonal-shortcut-repeat-1 and -2. Tests requiring Shell COM ran with host access after restricted runs demonstrated the known sandbox limitation. All new real shortcuts/cache assets lived in temporary fixtures. git diff --check passed.

Remaining risks/deferred work: InteractiveDesktop lane and visual Explorer/taskbar verification intentionally not run while the user uses the machine. Existing taskbar pin updates remain unproven and outside acceptance. Oversized Shell icon paths fall back to the launcher rather than store truncated references. Cached content-hashed assets are retained; no cleanup/migration/pin-editing facility added. No version bump, tag, push or release. Retrospective: long Unicode fixtures exposed native Shell MAX_PATH truncation, fixed with full-bit compact hashes plus explicit fallback rather than weakening tests.

### Revised-scope validation discoveries

2026-10-26: initial Release test-project build passed, 0 warnings/errors. Focused sandbox run passed 76/85 with 9 Shell registration failures, including existing registration tests. Host-access rerun passed 78/85: baseline registration works, but seven new seasonal tests exposed actual IShellLink icon-path truncation at 259 characters despite a larger read buffer. Decision before repair: preserve all SHA-256 bits using shorter URL-safe base64 cache directory/file names instead of hexadecimal IDs; reject remaining MAX_PATH-length icon locations and fall back to the launcher. Add explicit long-path fallback coverage. This is a product defect found by the long Unicode temporary-path fixtures, not a timeout/test adjustment. No desktop automation occurred.

2026-10-26: repaired cache names to URL-safe base64 SHA-256 (full 256-bit identities/content hashes) and added an explicit Shell MAX_PATH fallback regression. Release rebuild passed with 0 warnings/errors. Focused host-access suite passed 86/86, none skipped: dotnet test LogReader/LogReader.Tests/LogReader.Tests.csproj -c Release --no-build --no-restore -m:1 --filter '(FullyQualifiedName~Seasonal|FullyQualifiedName~SharedTaskbarShortcut|FullyQualifiedName~TaskbarRelaunch)&Category!=InteractiveDesktop' --logger trx --results-directory artifacts/test-results/seasonal-shortcut-focused-fixed. Actual COM links and cache files were created only in temporary fixtures; no desktop interaction. Broad solution Release build is next, followed by service-safe solution tests and repeated focused concurrency coverage.

2026-10-26: first full solution Release build passed with 0 warnings/errors; service-safe tests passed 2,197/2,197 (1,401 app/integration and 796 core), no skips. Final review refined the per-user/deployment mutex into the Global namespace so updates also serialize across logon sessions sharing the user's profile. Final solution Release rebuild passed with 0 warnings/errors. Final service-safe solution rerun and two focused regression repeats are running against that final output. Desktop-dependent tests are intentionally excluded; no Explorer/taskbar visual acceptance is claimed.

2026-10-26 final evidence: final service-safe rerun passed 2,197/2,197 (TRX files under artifacts/test-results/seasonal-shortcut-final); both final focused repeats passed 10/10. New tests verify standard/seasonal/restored metadata, real bundled ICO bytes, idempotent timestamps, repair/retry, cache path limits, deployment isolation and deterministic lock contention with owned work drained in finally. Final review complete; local feature commit prepared. No UI automation was resumed.
