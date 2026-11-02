# MCP tool consistency: execution plan

## Document control

- Created: 2026-11-01 (America/New_York).
- Status: Complete. All six milestones and seven outcomes implemented, validated and reviewed on 2026-11-02.
- Branch: `codex/mcp-tool-consistency`; do not switch branches or overwrite unrelated work.
- Planning baseline: `04f327f` (scope/backlog note), following `83106b2` on main. Recheck actual HEAD and working tree before implementation; documentation commits follow this baseline.
- Human overview: [McpToolConsistencyPlan.md](McpToolConsistencyPlan.md).
- Deferred scope: [McpToolConsistencyBacklog.md](McpToolConsistencyBacklog.md).
- Authority: current user instructions and repository `AGENTS.md`. The execution request authorized implementation; archived plans remain historical evidence.
- Keep Progress, Resume checkpoint, Decisions, Surprises, validation evidence, and Outcomes current. Do not mark work complete merely because code exists.

## Resume checkpoint

- Current milestone: M6 complete; no required implementation or acceptance work remains.
- Next action: none. Deferred rolling-date work requires a separate request; no push or release is authorized.
- Completed implementation: M1–M6; see milestone evidence, outcome traceability and local commits below.
- Validation: final Release solution build passed. Full service-safe solution: Core 920/920 and WPF/integration 1,496/1,496. All 31 monitor/lifecycle/handoff regressions passed three consecutive runs. Separate interactive desktop lane: 5/5 executed/pass, none skipped. Fresh artifact stdio smoke and all six local/real-UNC 1/10/100-file measurements passed.
- Active operations: none. Validation processes exited; observation fixtures explicitly released. Relevant delivery changes are recorded in the final local delivery commit. Synthetic test artifacts remain ignored and UNC run folders are retained for inspection.
- Local historical-plan cleanup is recorded separately in [PlanMaintenance2026-11-01.md](PlanMaintenance2026-11-01.md); archived instructions are historical evidence, not active work orders.

## Purpose and observable outcome

A model selects configured folders, dashboards, or files once and can obtain search evidence, counts, or monitor updates with consistent applicable matching rules. The server owns bounded multi-file coordination and exposes progress, uncertainty, errors, and recovery in a way a person can understand. Shared concepts support powerful operations; approximating manual UI interactions is not a capability restriction.

## Scope

Deliver all seven outcomes in the overview: multi-file monitoring; shared selection/filter/time semantics; lifecycle and explicit snapshot-to-monitor handoff; interpretable progress/confidence; simpler search/count choices; consistent continuation/recovery; and consistent errors. Cover Core/Infrastructure contracts and behavior, the MCP surface, relevant UI behavior, tests, packaging smoke consumers, and user/developer documentation.

## Non-goals

- No operations spanning rolling-file dates, convention-derived dated-path rollover, date-range scans, cross-date handoffs, or shared rolling-path resolver refactor.
- No atomic multi-file filesystem snapshot, persistent cross-process cursors, network listener, background daemon, arbitrary file paths, UI-owned indexes in the sidecar, or cross-account authorization redesign.
- No automatic monitoring after search/count, repeated fresh scans for completeness, broader discovery by default, unrequested extraction/grouping engine, or blanket UI feature expansion.
- No unrelated cleanup, new dependencies without a concrete need, product-version changes, tags, push, publication, or history rewriting.

## Definitions and invariants

- **Scope:** ordered configured folder/dashboard/file targets, current saved authorization, deduplicated physical files, retained provenance, and one selected rolling-file date.
- **Predicate:** applicable text/regex/case, inclusion/exclusion, and timestamp criteria. Reuse rules without forcing identical parameter lists or one universal tool.
- **Snapshot investigation:** bounded search/count traversal. Capture relative windows/reference date once. Freeze each file extent on first open and disclose drift conservatively. Continuation/replay does not widen that investigation.
- **Monitor:** explicitly requested observation over time through bounded polls. Idle means no relevant observed changes after the applicable scope was checked; no returned matches alone does not establish idle or caught-up status.
- **Handoff:** an opt-in transfer of usable per-file snapshot boundaries into monitoring. It is never an implicit response to incomplete or changed-file results.
- **Three independent facts:** work examined/remaining, evidence reliability, and output omitted. Do not collapse these into one success flag.
- All operations retain configured-ID authorization, sanitized paths/errors, bounded work/memory/text, cancellation, deterministic ordering, and useful per-file failures.
- Reauthorize on every advance/poll/replay. Never silently broaden scope on catalog edits. Reject/reconcile changes explicitly under the selected policy.
- An invocation stays bounded. A monitor observation period spans caller-driven polls; no request secretly waits or runs forever. If the API accepts a period/end time, enforce and report its terminal state.

## Existing behavior and evidence

These are source observations to recheck at execution time:

- `LogReader.Mcp/McpLogTools.cs`: search/count use typed targets; tail takes a single file ID. Count alone exposes relative windows. Search exposes samples/matchesOnly/countsOnly. Discovery pages by startIndex and asks callers to compare revisions.
- `LogReader.Infrastructure/Services/HeadlessLogQueryBackend.Resumable.cs`: search/count already share a resumable engine and retained transactions. Query fingerprints and result builders differ. Reuse this engine rather than creating a second snapshot implementation.
- `LogReader.Infrastructure/Services/ResumableLogScanner.cs`: file extents freeze on first open; generation changes and append drift carry uncertainty. Preserve the documented per-file boundary.
- `LogReader.Infrastructure/Services/HeadlessLogQueryBackend.Tail.cs` and the unfiltered tail path in `HeadlessLogQueryBackend.cs`: signed per-file cursors support resets and unfinished-line updates; filtered polls advance through nonmatches.
- `LogReader.App/Services/SearchTailMonitoringSession.cs`: UI monitor-new-matches may stop a tracker after generation/encoding/truncation changes, while other tail modes reset. Characterize and decide policy rather than changing all modes accidentally.
- `LogReader.Mcp/McpResponseProjector.cs`: invalid presentation modes set MCP isError; ordinary serialized backend rejection envelopes do not. Repair the mapping while preserving successful partial responses.
- `LineMatchState`, `RegexPatternFactory`, `TimestampParser`, and `SearchResultHelpers` already share matching/formatting logic. Extract only proven remaining duplication.
- `McpResponseJsonPolicy`, artifact smoke tests, and documentation encode exact current tool/schema/omission expectations. Include all consumers in public contract changes.

## Bounded contract decisions (resolve in M1)

Record a concrete decision and reason for each; use source/tests and engineering judgment rather than repeatedly requesting routine approval.

1. Public monitor surface: prefer an additive target-based entry point backed by shared logic, preserving existing single-file tail compatibility. Finalize naming/registration and update exact-tool-count consumers if needed.
2. Scope membership/date binding: capture ordered selection/reference date for a monitor, retain the selected physical identities, and reauthorize without silently following new convention-derived dates. Define catalog-change errors/reconciliation.
3. Initial monitor position: specify latest bounded tail versus start-now, initial scope paging, and how previously unvisited files acquire a baseline. Do not skip early appends while a large scope is initialized.
4. Poll budgets/fairness: bound examined files/lines/bytes/text, active disk/UNC work, retained state and expiry. Separate per-file quotas from total poll limits. Preserve progress across empty-match polls; avoid unbounded cursor payloads or per-file retained full indexes.
5. Same-path change policy: define stop/reset/follow behavior for rotation/truncation/encoding/unavailability, identify defaults, and preserve existing compatibility semantics where public behavior would otherwise change.
6. Snapshot-to-monitor eligibility: define transferable generation/offset/unfinished-line boundaries, retained-state ownership/expiry, eligible completed or partial traversals, and explicit rejection when safe handoff is impossible. Never silently fill missing boundaries by a new historical scan.
7. Filter/time behavior: share include/exclude and time-only filtering semantics. Freeze relative snapshot windows; explicitly define whether monitor time predicates are fixed or moving. Model missing timestamps and context-line treatment clearly.
8. Compatibility: record legacy modes, cursor/version behavior, schema migrations, and guidance changes. Do not remove public modes merely to reduce internal branches.

## Milestones and issue summary

- M1: characterize contracts and decide common semantics (medium risk).
- M2: share applicable query/filter/time preparation and result/error facts (medium risk).
- M3: implement bounded multi-file monitoring (high risk).
- M4: implement lifecycle consistency and safe explicit handoff (high risk).
- M5: simplify public choices and align continuation/recovery (medium-high risk).
- M6: complete compatibility, measurements, end-to-end validation, and documentation.

Each milestone ends with appropriate build/test validation, focused review, a local commit of relevant files, and a checkpoint update. Keep the project buildable between major edits. Earlier milestones must establish enough error/progress behavior for monitoring to be correct; M5 finishes the wider public simplification.

## Progress

- [x] M1 characterization and decisions.
- [x] M2 common semantics and evidence.
- [x] M3 bounded multi-file monitoring.
- [x] M4 lifecycle and explicit handoff.
- [x] M5 public simplification and recovery.
- [x] M6 final acceptance and delivery.

## M1 - Characterize current contracts and settle semantics

- State: Complete. Dependencies: none.
- Read the files above plus the existing MCP guides, query/tail/scanner tests, UI monitor tests, and packaging consumers. Inspect current diff and do not treat archived plans as new authorization.
- Capture current behaviors that must remain compatible. Resolve every bounded decision above and describe the chosen public request/result shapes in this plan before implementation.
- Add meaningful characterization coverage for affected semantics, not tests mirroring new abstractions.
- Acceptance: one coherent scope/predicate/time model; explicit initial/poll/handoff/change/recovery behavior; finite resource profile; compatibility strategy; no cross-date work or implicit rescan/monitor behavior.
- Validation: build/test the narrowest affected project if tests or infrastructure change. Record baseline failures without silently accepting them as new failures.
- Evidence: Core.Tests Release build passed (SDK 10.0.403, permitted latest patch); both new Consistency_ characterization tests passed. NU1900 vulnerability metadata unavailable from nuget.org. Reviewed snapshot continuation, legacy tail resets/unfinished lines, UI monitor stop policy, MCP schema/guidance, and packaging consumers.

## M2 - Share semantics and make evidence/errors consistent

- State: Complete. Dependencies: M1.
- Share only useful Core/Infrastructure preparation: configured scope resolution, predicate validation/preparation, absolute/relative time resolution, and normalized identity used by fingerprints. Keep transport projection and UI presentation outside these rules.
- Support equivalent include/exclude and supported time-only filters across applicable operations. Define context as surrounding source evidence or predicate-filtered context explicitly; apply the same documented rule consistently.
- Expose meaningful timestamp coverage and distinguish evaluated work, evidence uncertainty, and output omission. Do not use instrumentation as a substitute for authoritative completeness.
- Map rejected calls consistently to MCP failure plus sanitized structured details; preserve useful per-file success for partial operations. Keep schema/text/structured parity.
- Touch UI behavior only where necessary for the agreed shared semantics and lifecycle; retain UI ownership/dispatcher rules.
- Acceptance: equivalent predicates select equivalent lines; snapshot windows remain frozen through continuation/replay; all-timestamp-excluded output is distinguishable from an ordinary zero; invalid calls never resemble successful zero results; partial file failures remain isolated.
- Validation: relevant query/time/matcher/backend/tool tests, then full affected project. Build/test WPF project if changed.
- Evidence: shared scanner exclusion/time-only filtering, frozen search relative windows and cumulative timestamp coverage; rejected query/read envelopes now set MCP isError while nested per-file partial success remains successful. Added predicate parity, surrounding-context, relative-window continuation/replay and structured-error regression tests. Release build passed; full Core.Tests passed 884/884. Intentional schema/failure-flag expectations updated. git diff --check passed. Source review confirmed no new dependencies, paths in output, UI coupling or fresh scan fallback.

## M3 - Bounded multi-file monitoring

- State: Complete. Dependencies: M2.
- Add target-based monitoring using shared authorized resolution, ordered physical-file deduplication/provenance, one opaque bounded process-owned scope cursor, and per-file progress/events.
- Resolve and retain only one selected rolling-file date. Bind the captured selection date/physical identity; never substitute tomorrow's convention-derived path during polling.
- Establish safe baselines for the whole supported scope, including initialization spanning multiple bounded calls. Apply the M1 initial-position policy without losing appends between initialization and first poll.
- Enforce global and per-file budgets, fair advancement, bounded retained/working memory, expiry/release and existing disk/UNC concurrency. Avoid multiplying response caps or thrashing retained indexes for every selected file.
- Return backlog/unchecked-file evidence, advanced cursors after nonmatches, changed-file events, and sanitized per-file errors. A busy/missing/slow file must not permanently starve healthy files.
- Make retries/replays/cancellation deterministic: no uncommitted cursor advance, duplicate application of committed event batches, leaked resources, or silent lost matches. Define unsupported/expired replay behavior honestly.
- Acceptance: a mixed folder/dashboard/file scope polls through one continuation with physical duplicates scanned once; no false idle while unchecked work/backlog remains; repeated bounded polls reach quiet files; healthy files progress despite isolated failures; no path exposure or scope expansion after authorization removal.
- Validation: deterministic gates/fake clock for fairness, initialization races, budget exhaustion, replay/cancellation, idle/no-match advancement, expiry/capacity, and catalog changes; real local-file integration; published stdio/schema coverage. Repeat new concurrency tests at least three times.
- Evidence: monitor_logs added with 100-file admission, 10-file default polls, 4 MiB total / 256 KiB per-file scan budgets, total line/text caps, fair persistent sweeps, start-now trailing-boundary probes (64 KiB), process-owned transaction/replay, isolated errors, release and expiry. Eight backend monitor tests passed three consecutive runs; full Core.Tests passed 892/892. Stream protocol test covers event identity, shared provenance and inputs. Fresh local publish and artifact stdio smoke passed after updating the intentional unknown-ID isError expectation. Scope revision changes reject both advance and replay; no full indexes are created. Resource review fixed accounting of abandoned file slices. Restricted vstest startup failed to connect to testhost (before any tests); stopped its diagnostic runner and reran with normal process access successfully.

## M4 - Lifecycle consistency and explicit snapshot-to-monitor handoff

- State: Complete. Dependencies: M3.
- Apply the selected same-path lifecycle policy through shared events/state transitions where useful. Preserve caller-specific presentation and scheduling; do not route MCP through WPF sessions or indexes.
- Handle file replacement, truncation, deletion/reappearance, encoding changes, and growth/removal of an unfinished matching line with attributable file/generation/line identity.
- Implement opt-in handoff from a supported snapshot to monitoring using actual per-file boundaries. Support interrupted/partial input only when continuity can be established; otherwise return a clear, bounded error without a fresh scan fallback.
- Keep handoff source state available for a documented bounded lifetime, including terminal snapshot handling. Distinguish transfer/replay/release semantics and avoid consuming the source inconsistently on retries.
- Acceptance: controlled appends before/during/after handoff are delivered according to the selected boundary without unexplained gaps/duplicates; no file is treated as monitored unless it has a safe baseline; completed snapshot alone starts no observer; same-date replacements and terminal period conditions are explicit; cross-date following is rejected/deferred.
- Validation: generation/truncation/encoding/unavailability/unfinished-line cases in backend and affected UI mode; handoff race, expiry, retry, cancellation and scope-change tests; WpfTestHost for all async WPF state observations; repeat deterministic concurrency coverage at least three times.
- Evidence: opt-in enableHandoff on search/count retains source checkpoints without starting observers; explicit monitor handoff copies fully traversed verified boundaries and frozen date/predicate/window. Unsafe partial sources reject without scanning. Source remains reusable until idle expiry; each source handoff creates an independent observer, while monitor cursor retries replay batches. New optional observation duration terminates polling explicitly. Last-line byte/CRLF/match evidence survives scanner buffer release. Twelve lifecycle/handoff tests passed three consecutive runs; Core.Tests 905/905 passed. WPF monitor tests 6/6 passed three times, including new WpfTestHost truncation-stop/no-rescan regression; no UI scheduling change was needed. Full service-safe LogReader.Tests passed 1496/1496 (InteractiveDesktop excluded). Real stdio tests now call seven tools and require structured rejection flags. Review extended predicate memory accounting and preserved source ownership through cancellation.

## M5 - Public choices, continuation, and recovery

- State: Complete. Dependencies: M4.
- Prefer search evidence plus optional context, count totals/buckets, and explicit monitor updates. Preserve compatible legacy modes/aliases through the chosen migration; do not create a second engine for compatibility.
- Align progress/completeness names and semantics with versioned compatibility where needed. Keep cumulative versus page values unambiguous and preserve compact omission/reconstruction guarantees.
- Make discovery revision checks, query continuation, monitor cursor reuse, expiry/restart, stale authorization, and replay recovery understandable. Discovery must not silently combine incompatible revisions.
- Update server instructions/descriptions/examples so search can stop with sufficient evidence; exact count completes its existing supported traversal; terminal uncertainty is reported; neither a changed file nor a missing cursor triggers an automatic fresh search.
- Resolve contradictory recovery guidance: an unavailable cursor may explain how to request a new investigation, but must not instruct endless automatic restarts. Monitoring begins only for an explicit observation request.
- Acceptance: representative calls have one clear preferred operation; old supported clients retain documented behavior; invalid/partial/idle/caught-up/terminal states are distinguishable; guidance contains no search-to-monitor escalation or certainty-chasing loop.
- Validation: full tool schema/input boundary/output parity, backend compatibility, continuation/replay and stdio tests; update exact tool-count packaging fixtures to match the chosen surface.
- Evidence: expectedCatalogRevision enforces discovery consistency while retaining optional legacy checks. All tools project structured failures consistently; presentation rejection now has structured/text parity. Shared continuation recovery marks invalid/stale/expired tokens nonretryable and forbids automatic restarts. Search evidence/context and count totals are preferred; legacy modes remain. Monitor schemas describe progress, identity, terminal state, period, budgets and independent timestamp-evaluation status. Fixed monitor caps appear in server_status. Core.Tests Release build and 909/909 tests passed; WPF/test build and 4/4 real stdio tests passed. Fresh local publish plus PowerShell 7 artifact smoke passed with discovered output-schema validation, structured/text parity, all seven tools and monitor rejection checks. Review corrected inherited source-text encoding damage in three descriptions; git diff --check passed.

## M6 - Final validation, measurements, and documentation

- State: Complete. Dependencies: M5.
- Update server guide, getting-started material, security/architecture/performance documentation, in-app help where affected, artifact smoke, and measurement harness. Explain selected-date scope and snapshot versus observation boundaries in plain language.
- Run end-to-end fixtures: discover scope; search enough evidence; count an exact stable fixture; continue bounded snapshot without including new appends in already-frozen extents; report terminal drift honestly; explicitly monitor a multi-file scope; hand off safely; stop/release observation.
- Measure representative 1/10/100-file scopes and a supported-cap boundary, idle and active/no-match/matching polls, a hot-file/quiet-file mix, duplicate paths, local/controlled UNC contention, response bytes, actual files checked/backlog, latency, memory and cancellation/drain. Use explicit readiness signals. Real UNC measurements require an available share; report simulated versus real evidence separately.
- Compare unchanged single-file behavior and legacy query modes; verify limits remain bounded. Report measured values, not assumed token savings or an atomic snapshot guarantee.
- Review security-sensitive scope/cursor/path/network changes; verify no paths, credentials, labels-as-instructions, or log text leak into diagnostics. Keep stdout protocol-only.
- Acceptance: all seven outcomes have traceable code/tests/docs and actual passing required checks; no cross-date expansion; no automatic rescans/monitoring; no running owned work or uncommitted relevant changes remain. Update Outcomes and record any externally blocked checks precisely rather than claiming completion.
- Evidence: final Release solution build passed with `-p:UseSharedCompilation=false`; full service-safe solution tests passed 2,416/2,416 (920 Core + 1,496 WPF/integration). All 31 monitor/lifecycle/handoff regressions passed three times after the final initial-missing-file fix. Separate interactive desktop lane executed/passed 5/5 with no skips. Final tool schema/recovery clarification passed 117 tests. Fresh sidecar artifact smoke passed all seven tools, schemas, structured/text parity, request failures, protocol-only stdout and clean stdin exit.
- Measurements: six actual stdio runs (local and real UNC, each 1/10/100 files), exact stable counts, physical alias/provenance deduplication, idle/nonmatch/match rounds, replay, hot/quiet fairness, frozen-extent continuation with terminal drift, explicit handoff/release and clean process exit all passed. The final Infrastructure artifact hash is `8AF10D4ADE69C34F9C88812FD8ECAA61A706EE4634ACD88B849FF9999F95DDAB`. [Measurements](McpToolConsistencyMeasurements.md) and [JSON evidence](McpToolConsistencyMeasurements.json) record timings, memory, bytes, query comparisons, hashes and raw report locations. Actual UNC testing stayed within dedicated children of the supplied authorized root; no parent/sibling enumeration occurred.
- Review: corrected alias-page cap admission (300 aliases / 100 physical files), append/half-UTF16 backlog, split CRLF, BOM-less UTF16, prefetched-hit handoff rejection and invalid date/filter handling. Missing initial files now explicitly stop without claiming a retained boundary or retryability; healthy files continue. Public docs, security notes and in-app help agree with the tested surface. No dependency/version changes, cross-date traversal, resolver consolidation, implicit fresh scans, hidden observation or UI service dependencies were introduced.
- Validation limits: NuGet audit metadata was unreachable (`NU1900`), not a build/test failure. Transport cancellation probes explicitly do not prove heavy-work admission; gated backend tests prove rollback/drain. Filesystem deadlines remain cooperative and measurements are single synthetic runs, not percentile guarantees. There are no unavailable required validation lanes.

## Validation commands and reliability

Run from repository root; confirm the SDK from `global.json`. Build before testing its output. Infer additional focused filters from the tests actually changed.

```powershell
dotnet build LogReader/LogReader.Core.Tests/LogReader.Core.Tests.csproj -c Release -m:1
dotnet test LogReader/LogReader.Core.Tests/LogReader.Core.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~Mcp|FullyQualifiedName~HeadlessLogQueryBackend|FullyQualifiedName~ResumableQuery|FullyQualifiedName~QueryContinuationStore|FullyQualifiedName~CountTimeWindowResolver"
dotnet build LogReader/LogReader.Tests/LogReader.Tests.csproj -c Release -m:1
dotnet test LogReader/LogReader.Tests/LogReader.Tests.csproj -c Release --no-build --no-restore --filter "(FullyQualifiedName~SearchTailMonitoringSession|FullyQualifiedName~SearchPanelViewModel|FullyQualifiedName~FilterPanel|FullyQualifiedName~McpStdioProtocol)&Category!=InteractiveDesktop"
dotnet build LogReader/LogReader.sln -c Release -m:1
dotnet test LogReader/LogReader.sln -c Release --no-build --no-restore -m:1 --filter "Category!=InteractiveDesktop"
```

Final acceptance commands used (all output stayed local; each build/publish finished before consuming its output):

```powershell
dotnet build LogReader/LogReader.sln -c Release -m:1 --no-restore -p:UseSharedCompilation=false
dotnet test LogReader/LogReader.Core.Tests/LogReader.Core.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~Monitor_|FullyQualifiedName~MonitorLifecycle_|FullyQualifiedName~Handoff_"
# Repeat the focused filter three times, then run the full service-safe solution.
dotnet test LogReader/LogReader.sln -c Release --no-build --no-restore -m:1 --filter "Category!=InteractiveDesktop"
pwsh -NoProfile -File .github/scripts/Invoke-InteractiveDesktopTests.ps1 -Configuration Release
dotnet publish LogReader/LogReader.Mcp/LogReader.Mcp.csproj -c Release --no-restore -p:UseSharedCompilation=false -o LogReader/artifacts/mcp-consistency-smoke
pwsh -NoProfile -File LogReader/packaging/scripts/Test-McpStdioArtifact.ps1 -ExecutablePath LogReader/artifacts/mcp-consistency-smoke/WeezTail.Mcp.exe
pwsh -NoProfile -File LogReader/packaging/scripts/Measure-McpLogServer.ps1 -ExecutablePath LogReader/artifacts/mcp-consistency-smoke/WeezTail.Mcp.exe -FileCount 100 -LinesPerFile 250 -IncludeMonitorConsistency -IncludeStatistics
```

Measurements ran separately for `-FileCount 1`, `10` and `100`, then the same three cases with `-LogDirectoryRoot <authorized existing test root>` for real UNC. The raw report links in the JSON summary identify the exact six final runs; supplied network paths are omitted from committed evidence. Desktop TRX evidence remains in ignored `artifacts/test-results/interactive-desktop/a3b9e7db770a45339364648ca5910bd9/`.

- Test new suites by their actual names too; the example filters above are not exhaustive future coverage. Run full affected-project suites before final solution checks.
- Repeat focused concurrency cases at least three times using explicit readiness/gates, bounded waits, finally releases, thread-safe doubles, and draining before fixture disposal. Do not fix races by sleeping longer, weakening assertions, or disabling more parallelism.
- Async WPF setup/assertions/cleanup all belong inside WpfTestHost. Separate real desktop coverage. Service-safe runs explicitly exclude InteractiveDesktop; disclose it. If required UI behavior changes, run the real desktop lane separately via `.github/scripts/Invoke-InteractiveDesktopTests.ps1`; unavailable desktop or zero tests is a failed/unavailable check, not a pass or silent skip.
- Follow existing publish/smoke invocation parameters in DeveloperGuide and `Test-McpStdioArtifact.ps1`; validate a newly published sidecar, not a stale installed binary. Publish only local validation artifacts; do not deploy or publish a release.
- Inspect failures and fix causes. Preserve useful baseline/external-failure evidence and do not repeat broad testing after a passing gate without a new reason.
- Documentation-only maintenance requires link/content/whitespace checks, not runtime tests.

## Risks and mitigations

- Scope cursors can hide memory growth or starvation: explicit accounting, fair progress, capacity errors and deterministic stress coverage.
- Init/handoff can lose events: capture real per-file boundaries, preserve unfinished-line state and test appends at controlled boundary gates.
- Public simplification can break clients: explicit version/compatibility plan, legacy entry-point tests, discovered-schema validation and packaged stdio smoke.
- Shared abstractions can couple UI and server: share pure rules/state facts in Core/Infrastructure; retain process-local resources and scheduling.
- Stronger tools can over-search: finite snapshot boundaries, honest terminal caveats, sufficient-evidence guidance, explicit observation intent and no hidden fallback scan.

## Deferred work

See [McpToolConsistencyBacklog.md](McpToolConsistencyBacklog.md). Do not implement deferred work as a prerequisite or silently broaden this pass. In particular, replacing a generation at the selected path is different from following another dated path.

## Selected contracts (M1)

1. Add `monitor_logs` to the six existing tools. One opaque process-owned cursor selects retained scope and predicates; later polls need only that cursor and bounded poll controls. `release=true` explicitly ends observation. Existing single-file tail modes remain compatible.
2. Resolve/deduplicate configured targets once using the captured reference date. Retain physical paths privately. Every poll/replay rereads the catalog; any revision change rejects the cursor before disk work. No dated-path following or automatic reconciliation.
3. Default initial position is start-now, with no historical text. Capture real file boundaries for the entire admitted scope before returning a cursor. Initialization is all-or-nothing under the request deadline; a failed initialization promises no observation. Bound admission to 100 physical files. Capture an unfinished trailing line using a bounded backward probe; reject an unsafe boundary rather than skip it. Events use generation plus byte offset identity (line numbers only where known), avoiding full-file indexes merely to establish start-now.
4. Reuse continuation-store admission, serialization, transactional clone/commit and one-step replay. Bound files per poll (default 10), total lines/bytes/text and per-file bytes. Advance round-robin even on empty matches/errors. Retain checkpoints rather than full indexes. Existing process disk/UNC gates apply. Fifteen-minute idle expiry and configured session/memory capacities apply. Caller cancellation rolls back the whole poll.
5. New monitoring stops an individual file on same-path generation, truncation or encoding change, with an attributable terminal event. Temporary unavailability after a baseline retains its boundary and retries on subsequent bounded polls. An initial missing/unreadable file stops explicitly because no safe boundary exists. This matches UI monitor-new-matches; legacy tail reset behavior remains unchanged. No implicit replacement history scan.
6. Explicit `handoffCursor` comes from retained snapshot state, including clean terminal snapshots. Transfer only when every selected file has an initialized, exhausted, verified boundary, all prefetched hits are committed and selection is exhausted. Verified append drift after the frozen extent is eligible; unvisited/failed/unsafe or query-cap-prefetched state is rejected without fallback scans. Source state is copied, not consumed, for the same bounded lifetime. Handoff does not refresh the captured date/window. Retry the resulting monitor cursor for poll replay; an initial cursorless call is a new observation.
7. Shared text predicate supports inclusion plus optional exclusion using the same regex/case rules, or time-only queries. Search gains frozen relative windows; monitors freeze windows at creation rather than slide them. Missing/incompatible timestamps are excluded and counted separately. Context is surrounding source evidence, so may fail the hit predicate. Empty query without time criteria remains rejected for snapshot search/count; unfiltered explicit monitoring is valid.
8. Keep existing result modes/defaults and schema versions; add fields/one tool. Prefer samples with optional context for evidence and count for totals. Preserve compact reconstruction and legacy tail cursors. Errors with no usable result set MCP isError; useful partial file results stay successful with structured errors. Discovery gains an optional expected revision. Recovery never silently starts a new investigation.

## Decision log

- 2026-11-01: utility and understandable scope/time/evidence govern design; UI parity is useful guidance rather than a capability ceiling.
- 2026-11-01: all operations in this pass stay within one selected rolling-file date. Rolling resolver consolidation remains deferred as a whole.
- 2026-11-01: search/count stay bounded as-of investigations; monitoring and handoff are explicit; terminal uncertainty does not authorize repeated fresh scans.
- 2026-11-01: create and review plan documents now; execute runtime work only under the subsequent execution request.
- 2026-11-02: physical admission cap is 100 files (unresolved entries consume slots); configured aliases merge across resolver pages within the existing 2,000-candidate ceiling. Reject an oversized scope before capturing baselines.
- 2026-11-02: preserve actual BOM evidence and byte-offset event identities; bounded initialization commits only once every admitted file has a baseline or an explicit stopped error. Fixed time windows and selected-date binding survive handoff.
- 2026-11-02: user supplied an authorized real UNC test root. Test only exact-root access and dedicated synthetic run descendants; preserve parent/sibling privacy. Separate actual network evidence from deterministic simulated contention.

## Surprises and discoveries

- Planning cleanup found completed historical plans plus plans with outstanding manual/guest/runtime-isolation work. Archive scoped completion evidence; preserve unresolved work separately.
- SDK compiler-server startup was slow under restricted process access; UseSharedCompilation=false gives deterministic short builds. Vstest later failed before testhost connection; normal local process access passed the same tests, with no timeout increases.
- Start-now uses generation/byte-offset identity rather than building line indexes for 100 files. Initialization returns only after all files are baselined or explicitly stopped; earlier-file appends during initialization are retained by their captured boundaries.

- Final review corrected physical-cap admission across alias pages, pending observed-growth/UTF-16 backlog, split-CRLF state, BOM-less UTF-16 boundaries, committed-evidence handoff eligibility, and structured invalid-date/filter errors. New regressions exercise each.
- A final WPF run exposed an unrelated dashboard test using Immediate dispatch while member refresh mutated UI collections. The focused correction runs its setup/assertions/cleanup on WpfTestHost and drains shutdown with existing fail-closed host lifetime; no production dashboard behavior changed. Its original mixed line endings are preserved; whitespace checking uses cr-at-eol explicitly for that mixed file.

## Outcomes and retrospective

All six milestones and seven outcomes are delivered. Search/count remain bounded as-of investigations; continuing them advances their original extents and frozen windows. Terminal uncertainty is reported without fresh scans. Monitoring requires explicit intent, stays on one selected rolling-file date and advances only through bounded caller polls. Existing tail modes, query modes and the headless/UI boundary remain compatible.

| Outcome | Implementation evidence | Validation and documentation |
| --- | --- | --- |
| Multi-file monitoring | `HeadlessLogQueryBackend.Monitor.cs`, `LogMonitorContracts.cs`: configured targets, deduplicated paths/provenance, fair slices and one process-owned cursor | `McpMonitorTests`: alias pages, 101-file rejection, fair hot/quiet progress and controlled UNC gates; six measured local/UNC scopes; server guide |
| Shared scope/filter/time semantics | `PreparedLineFilter.cs`, backend predicate/resumable preparation and `SearchRequest`: include/exclude, regex/case, fixed relative windows, timestamp coverage | `McpConsistencyPredicateTests`, exclusion-only monitor and frozen-time handoff cases; server guide and architecture |
| Lifecycle and explicit handoff | backend monitor/resumable and `QueryContinuationStore`: start-now baseline, stopped changes, opt-in copied snapshot checkpoints, period/release | `McpMonitorLifecycleTests`: split CRLF, partial UTF16, truncation/replacement/encoding stop, safe/unsafe handoff, midnight selected-date binding; real stdio drift/handoff |
| Understandable progress and uncertainty | checked/unchecked/backlog/stopped counts, scope rounds, independent timestamp evaluation, partial/uncertain snapshot facts | no-match/append-at-validation/half-unit regressions; measured round bytes/events; guide, schemas and in-app help |
| Simpler search/count choices | `McpLogTools.cs` and response policy: search evidence/context versus count totals, retained legacy modes | public compatibility/schema tests, exact stable 1/10/100 counts and legacy tail measurements; getting-started guide |
| Consistent continuation/recovery | `QueryContinuationStore`, backend recovery and expected discovery revision: transaction/replay, reauthorization, capacity/expiry and explicit restart intent | cancellation/concurrent replay/catalog/expiry tests, discovery recovery suite and stdio replay; guide |
| Actionable errors | backend recovery, monitor file errors and response projector: sanitized structured errors, MCP isError, isolated usable partial results | `McpConsistencyRecoveryTests`, schema/protocol tests, initial-missing-file healthy progress; security model |

Security review confirmed configured-ID authorization on creation and every advance/replay/export, signed process-local cursors, bounded retained state, sanitized diagnostics and no network listener or raw path input to MCP. Two disk / one UNC shared gates remain; deterministic contention tests and actual UNC statistics agree. No deferred rolling-date or resolver work was executed.

Two existing test-fixture failures surfaced during full acceptance and were corrected at their causes: the dashboard-order fixture now owns all UI work and shutdown on WpfTestHost; the native icon fixture uses an explicit standard icon baseline independently of the current season. Native assertions, required lanes, waits and test parallelism were preserved. Passing full and desktop reruns followed both fixes.

Local delivery commits (no push, tags, version changes or history rewriting):

| Milestone/change | Commit |
| --- | --- |
| M1 contracts/characterization | `41a1e79` |
| M2 shared predicates/time/errors | `3ea4652` |
| M3 bounded multi-file monitor | `29795de` |
| M4 lifecycle/handoff | `7c13e42` |
| M5 discovery/choices/recovery | `9ab9dc1` |
| WPF fixture ownership/drain correction | `cce9dd6` |
| Calendar-independent desktop fixture | `bc6892d` |
| M6 boundary review, harness, measurements and final docs | This final delivery commit |

Remaining limits are explicit: per-file captures are not an atomic multi-file snapshot; in-place rewrite detection is conservative; filesystem operations cannot promise hard latency deadlines; cursors are process-local and expire; one-run synthetic latency/memory values do not establish general performance percentiles. NuGet vulnerability metadata could not be fetched. Required runtime validation itself is complete.

## Handoff history

- 2026-11-01: initial plan prepared on `codex/mcp-tool-consistency`; next implementation action is M1.
- 2026-11-02: final acceptance complete, evidence and resume checkpoint refreshed; all delivery work recorded locally. No owned operations remain.
