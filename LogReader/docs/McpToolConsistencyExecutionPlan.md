# MCP tool consistency: execution plan

## Document control

- Created: 2026-11-01 (America/New_York).
- Status: Planned. Implementation has not started.
- Branch: `codex/mcp-tool-consistency`; do not switch branches or overwrite unrelated work.
- Planning baseline: `04f327f` (scope/backlog note), following `83106b2` on main. Recheck actual HEAD and working tree before implementation; documentation commits follow this baseline.
- Human overview: [McpToolConsistencyPlan.md](McpToolConsistencyPlan.md).
- Deferred scope: [McpToolConsistencyBacklog.md](McpToolConsistencyBacklog.md).
- Authority: current user instructions and repository `AGENTS.md`. This document plans work; creating it does not execute runtime changes.
- Keep Progress, Resume checkpoint, Decisions, Surprises, validation evidence, and Outcomes current. Do not mark work complete merely because code exists.

## Resume checkpoint

- Current milestone: M1, not started.
- Next action: inspect current source, characterize the public contracts, and resolve the bounded contract decisions below before changing public behavior.
- Completed implementation: none for this project.
- Validation: planning-document review only; no runtime builds/tests run for this planning change.
- Active operations/blockers: none.
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

- [ ] M1 characterization and decisions.
- [ ] M2 common semantics and evidence.
- [ ] M3 bounded multi-file monitoring.
- [ ] M4 lifecycle and explicit handoff.
- [ ] M5 public simplification and recovery.
- [ ] M6 final acceptance and delivery.

## M1 - Characterize current contracts and settle semantics

- State: Not started. Dependencies: none.
- Read the files above plus the existing MCP guides, query/tail/scanner tests, UI monitor tests, and packaging consumers. Inspect current diff and do not treat archived plans as new authorization.
- Capture current behaviors that must remain compatible. Resolve every bounded decision above and describe the chosen public request/result shapes in this plan before implementation.
- Add meaningful characterization coverage for affected semantics, not tests mirroring new abstractions.
- Acceptance: one coherent scope/predicate/time model; explicit initial/poll/handoff/change/recovery behavior; finite resource profile; compatibility strategy; no cross-date work or implicit rescan/monitor behavior.
- Validation: build/test the narrowest affected project if tests or infrastructure change. Record baseline failures without silently accepting them as new failures.
- Evidence: pending.

## M2 - Share semantics and make evidence/errors consistent

- State: Not started. Dependencies: M1.
- Share only useful Core/Infrastructure preparation: configured scope resolution, predicate validation/preparation, absolute/relative time resolution, and normalized identity used by fingerprints. Keep transport projection and UI presentation outside these rules.
- Support equivalent include/exclude and supported time-only filters across applicable operations. Define context as surrounding source evidence or predicate-filtered context explicitly; apply the same documented rule consistently.
- Expose meaningful timestamp coverage and distinguish evaluated work, evidence uncertainty, and output omission. Do not use instrumentation as a substitute for authoritative completeness.
- Map rejected calls consistently to MCP failure plus sanitized structured details; preserve useful per-file success for partial operations. Keep schema/text/structured parity.
- Touch UI behavior only where necessary for the agreed shared semantics and lifecycle; retain UI ownership/dispatcher rules.
- Acceptance: equivalent predicates select equivalent lines; snapshot windows remain frozen through continuation/replay; all-timestamp-excluded output is distinguishable from an ordinary zero; invalid calls never resemble successful zero results; partial file failures remain isolated.
- Validation: relevant query/time/matcher/backend/tool tests, then full affected project. Build/test WPF project if changed.
- Evidence: pending.

## M3 - Bounded multi-file monitoring

- State: Not started. Dependencies: M2.
- Add target-based monitoring using shared authorized resolution, ordered physical-file deduplication/provenance, one opaque bounded process-owned scope cursor, and per-file progress/events.
- Resolve and retain only one selected rolling-file date. Bind the captured selection date/physical identity; never substitute tomorrow's convention-derived path during polling.
- Establish safe baselines for the whole supported scope, including initialization spanning multiple bounded calls. Apply the M1 initial-position policy without losing appends between initialization and first poll.
- Enforce global and per-file budgets, fair advancement, bounded retained/working memory, expiry/release and existing disk/UNC concurrency. Avoid multiplying response caps or thrashing retained indexes for every selected file.
- Return backlog/unchecked-file evidence, advanced cursors after nonmatches, changed-file events, and sanitized per-file errors. A busy/missing/slow file must not permanently starve healthy files.
- Make retries/replays/cancellation deterministic: no uncommitted cursor advance, duplicate application of committed event batches, leaked resources, or silent lost matches. Define unsupported/expired replay behavior honestly.
- Acceptance: a mixed folder/dashboard/file scope polls through one continuation with physical duplicates scanned once; no false idle while unchecked work/backlog remains; repeated bounded polls reach quiet files; healthy files progress despite isolated failures; no path exposure or scope expansion after authorization removal.
- Validation: deterministic gates/fake clock for fairness, initialization races, budget exhaustion, replay/cancellation, idle/no-match advancement, expiry/capacity, and catalog changes; real local-file integration; published stdio/schema coverage. Repeat new concurrency tests at least three times.
- Evidence: pending.

## M4 - Lifecycle consistency and explicit snapshot-to-monitor handoff

- State: Not started. Dependencies: M3.
- Apply the selected same-path lifecycle policy through shared events/state transitions where useful. Preserve caller-specific presentation and scheduling; do not route MCP through WPF sessions or indexes.
- Handle file replacement, truncation, deletion/reappearance, encoding changes, and growth/removal of an unfinished matching line with attributable file/generation/line identity.
- Implement opt-in handoff from a supported snapshot to monitoring using actual per-file boundaries. Support interrupted/partial input only when continuity can be established; otherwise return a clear, bounded error without a fresh scan fallback.
- Keep handoff source state available for a documented bounded lifetime, including terminal snapshot handling. Distinguish transfer/replay/release semantics and avoid consuming the source inconsistently on retries.
- Acceptance: controlled appends before/during/after handoff are delivered according to the selected boundary without unexplained gaps/duplicates; no file is treated as monitored unless it has a safe baseline; completed snapshot alone starts no observer; same-date replacements and terminal period conditions are explicit; cross-date following is rejected/deferred.
- Validation: generation/truncation/encoding/unavailability/unfinished-line cases in backend and affected UI mode; handoff race, expiry, retry, cancellation and scope-change tests; WpfTestHost for all async WPF state observations; repeat deterministic concurrency coverage at least three times.
- Evidence: pending.

## M5 - Public choices, continuation, and recovery

- State: Not started. Dependencies: M4.
- Prefer search evidence plus optional context, count totals/buckets, and explicit monitor updates. Preserve compatible legacy modes/aliases through the chosen migration; do not create a second engine for compatibility.
- Align progress/completeness names and semantics with versioned compatibility where needed. Keep cumulative versus page values unambiguous and preserve compact omission/reconstruction guarantees.
- Make discovery revision checks, query continuation, monitor cursor reuse, expiry/restart, stale authorization, and replay recovery understandable. Discovery must not silently combine incompatible revisions.
- Update server instructions/descriptions/examples so search can stop with sufficient evidence; exact count completes its existing supported traversal; terminal uncertainty is reported; neither a changed file nor a missing cursor triggers an automatic fresh search.
- Resolve contradictory recovery guidance: an unavailable cursor may explain how to request a new investigation, but must not instruct endless automatic restarts. Monitoring begins only for an explicit observation request.
- Acceptance: representative calls have one clear preferred operation; old supported clients retain documented behavior; invalid/partial/idle/caught-up/terminal states are distinguishable; guidance contains no search-to-monitor escalation or certainty-chasing loop.
- Validation: full tool schema/input boundary/output parity, backend compatibility, continuation/replay and stdio tests; update exact tool-count packaging fixtures to match the chosen surface.
- Evidence: pending.

## M6 - Final validation, measurements, and documentation

- State: Not started. Dependencies: M5.
- Update server guide, getting-started material, security/architecture/performance documentation, in-app help where affected, artifact smoke, and measurement harness. Explain selected-date scope and snapshot versus observation boundaries in plain language.
- Run end-to-end fixtures: discover scope; search enough evidence; count an exact stable fixture; continue bounded snapshot without including new appends in already-frozen extents; report terminal drift honestly; explicitly monitor a multi-file scope; hand off safely; stop/release observation.
- Measure representative 1/10/100-file scopes and a supported-cap boundary, idle and active/no-match/matching polls, a hot-file/quiet-file mix, duplicate paths, local/controlled UNC contention, response bytes, actual files checked/backlog, latency, memory and cancellation/drain. Use explicit readiness signals. Real UNC measurements require an available share; report simulated versus real evidence separately.
- Compare unchanged single-file behavior and legacy query modes; verify limits remain bounded. Report measured values, not assumed token savings or an atomic snapshot guarantee.
- Review security-sensitive scope/cursor/path/network changes; verify no paths, credentials, labels-as-instructions, or log text leak into diagnostics. Keep stdout protocol-only.
- Acceptance: all seven outcomes have traceable code/tests/docs and actual passing required checks; no cross-date expansion; no automatic rescans/monitoring; no running owned work or uncommitted relevant changes remain. Update Outcomes and record any externally blocked checks precisely rather than claiming completion.
- Evidence: pending.

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

## Decision log

- 2026-11-01: utility and understandable scope/time/evidence govern design; UI parity is useful guidance rather than a capability ceiling.
- 2026-11-01: all operations in this pass stay within one selected rolling-file date. Rolling resolver consolidation remains deferred as a whole.
- 2026-11-01: search/count stay bounded as-of investigations; monitoring and handoff are explicit; terminal uncertainty does not authorize repeated fresh scans.
- 2026-11-01: create and review plan documents now; execute runtime work only under the subsequent execution request.

## Surprises and discoveries

- Planning cleanup found completed historical plans plus plans with outstanding manual/guest/runtime-isolation work. Archive scoped completion evidence; preserve unresolved work separately.
- Add implementation discoveries here with their effect on decisions, scope, and acceptance.

## Outcomes and retrospective

Planning documents created. No runtime implementation or runtime validation has occurred under this plan. Replace this section with actual delivered behavior, commit/evidence references, validation outcomes and remaining risks at completion.

## Handoff history

- 2026-11-01: initial plan prepared on `codex/mcp-tool-consistency`; next implementation action is M1.
