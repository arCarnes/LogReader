# UNC performance execution plan

Created: 2026-11-02. Status: Planning package only. Runtime baseline: `9228d9c`. Authority: current user instructions, repository `AGENTS.md`, [design](Design.md) and [validation contract](ValidationAndMeasurements.md).

## Resume checkpoint

- Current phase: U0, **Not started**.
- Next action: inspect actual HEAD, working tree and applicable instructions; reconcile this package with current source, then establish U0 fixtures and baselines.
- Runtime changes completed under this package: none.
- Runtime validation performed under this package: none. Earlier investigation results are historical evidence, not acceptance for future changes.
- Owned implementation operations/resources: none.
- Commit evidence: planning-package commit is discoverable with `git log -- LogReader/docs/plans/unc-performance`; record implementation hashes in the ledger below after each commit.
- Deferred/externally unavailable checks: none assessed yet. During execution, record the exact unavailable lane and continue independent work; do not claim final acceptance until required checks are resolved.

Update this checkpoint before stopping a later execution turn. Record current phase, completed steps, next concrete action, modified files, validation commands/results, active processes/tasks/leases, failures and unresolved decisions. Reconcile documentation with actual commits; never infer completion from a checkbox alone.

## Work and commit protocol

Execute U0 -> U1 -> U2 -> U3 -> U4 -> U5. Keep changes buildable between phases. Build the narrow affected test project before testing its output, repeat changed concurrency coverage three times, then run the affected full project suite. U5 covers the full solution and separate desktop lane.

Stage only relevant implementation/tests/docs and create a local commit after each coherent completed unit, following repository instructions. A phase can contain multiple such commits. Record their hashes and evidence; do not amend, squash, switch branches, push, tag, publish or change versions. Leave unrelated edits untouched.

Use the [candidate rule](ValidationAndMeasurements.md#candidate-decision-rule) for each optimization. Rejected candidates return their own runtime changes to the preceding accepted behavior using focused edits; do not reset or discard other work. Commit retained regression/measurement improvements and the rejection evidence as appropriate. Subsequent independent phases may continue. Missing required validation means incomplete, not rejected or complete.

## U0 - baseline and characterization

State: **Not started**. Dependency: none.

- Recheck observed behavior and recent UTF-8/line-continuity fixes against the design. Record actual baseline commit, SDK, configuration and effective limits.
- Add a maintained, opt-in measurement entry point under `LogReader/packaging/scripts`, with any small supporting harness under existing test infrastructure. Match project style and existing dependencies. It must run on a clean checkout without the ignored investigation harness or fixture names. Keep private internal API access within existing test-friend assemblies.
- Implement the fixture and reporting protocol in the validation document: isolated local writable fixtures, bounded read-only UNC manifest, normalized report labels, exact counts and invariant checks, seven alternating baseline/candidate pairs. Preserve the baseline executable/output before editing runtime code. Report no executed samples as a failure.
- Add readiness-gated characterization where current tests lack first-scan lifecycle, admission ownership, full-buffer lifetime or ordered monitor-budget evidence. Include split UTF-8 and existing unfinished-line behavior.
- Baseline measurements: local and allowed UNC snapshot/index/search/idle workflows, 1/10/100-file scopes when available, UI preview response and isolated component costs. Large-file/lifecycle writes use local fixtures only.
- Acceptance: reproducible commands and fixture recipe checked in; baseline report records exact observed totals, storage, effective limits and limitations. No application optimization yet. A missing remote fixture is recorded as an evidence gap rather than silently replaced by a smaller successful test.
- Validation: V-Core and V-UI for changed test infrastructure, three repetitions of new concurrency characterization, relevant full project suites. New measurement code must build before use. See validation document for commands.
- Suggested commit: `test: establish UNC performance baselines`.

Evidence/commit: pending.

## U1 - first interactive quantum

State: **Not started**. Dependency: U0.

- Touchpoints: `FileSession.BuildIndexOffUiAsync`, interactive reader tests, dashboard loading/responsiveness and scheduler tests.
- Advance a resumable newly created build within its initial scheduler lease; guard unknown-identity fallback explicitly. Keep suspension before every incomplete lease release, bounded scanning, preview yield, final index transfer and tail-registration ordering.
- Do not retain handles across queued turns or change reader public interfaces. Cover restart/auto-encoding paths and failures during create/advance/suspend/dispose.
- Acceptance: deterministic evidence proves initial progress without an avoidable reopen, real preemption still suspends/revalidates, no index/task/lease leak, unchanged line content and current generation behavior. Compare interactive component and actual dashboard completion/preview metrics with U0.
- Validation: V-Core interactive/encoding/index tests; V-UI dashboard/session/scheduler tests; changed concurrency tests three times; both affected project suites before phase acceptance.
- Suggested commit: `perf: scan the first interactive index quantum before suspension`.

Evidence/commit: pending.

## U2 - four-worker UI policy trial

State: **Not started**. Dependency: U1 accepted or rejected with evidence.

- Touchpoints: `AdaptiveParallelismPolicy`, `AdaptiveParallelismPolicyTests`, dashboard scheduler and search/filter batch tests.
- Trial four per UNC host and four per share for dashboard/search/filter. Preserve global 24/32, local two, unknown one and the separate single-background policy. Update intentional cap assertions without weakening behavioral assertions.
- Cover duplicate/case/extended UNC classifications, topology bounds, preview reservation, cancellation and mixed local/UNC queues with fake paths. Do not access additional real shares.
- Acceptance: aggregate and topology limits hold; benchmark the two-worker preceding accepted behavior against four at 1/10/100 files. Apply the documented improvement/regression rule, including UI preview latency. If rejected, retain previous policy and record why; do not silently substitute eight workers or a new setting.
- Validation: V-Core policy/search/filter tests; V-UI scheduler/dashboard/filter/search tests; three repeated concurrency runs and full affected suites.
- Suggested commit: `perf: increase bounded UI UNC concurrency` if accepted; otherwise record the trial result in a documentation/test commit.

Evidence/commit: pending.

## U3 - topology-aware MCP admission

State: **Not started**. Dependency: U2 accepted or rejected with evidence.

- Touchpoints: backend operation/probe/lease helpers, Core path classification if extracted, backend lifetime tests and resumable query tests.
- Implement the design's backend-owned coordinator: atomic global/host/share admission, oldest eligible waiter, cancellation-safe idempotent lease and bounded group registry. Keep effective global default two; host/share caps are at most two. Do not change heavy-request limits or two-file search/count lookahead.
- Migrate all disk entry points, including the detached existence-probe completion path, metrics and shutdown. A cancelled caller must not release capacity or backend ownership before its synchronous operation actually returns.
- Acceptance: independent topology keys are observed under fake concurrent readiness gates; explicit global cap one remains serial; blocked group waiters do not reserve global permits; live operations, metrics and registry ownership agree after failure/cancellation/disposal. Existing cursor/schema/content tests remain valid.
- Validation: V-Core backend, resumable, monitor, continuation, admission and limits coverage three times for concurrency tests, then full Core suite. Run stdio smoke against a freshly built/published local artifact if backend composition/lifetime changed. Use existing artifact scripts without writing remote fixtures.
- Suggested commit: `perf: share topology-aware admission across MCP disk operations`.

Evidence/commit: pending.

## U4 - two-slot monitor preparation

State: **Not started**. Dependency: U3 accepted or rejected with evidence. If U3 is rejected, U4 may proceed only within the preceding accepted admission limits; do not claim overlap that those limits prohibit.

- Touchpoints: monitor initialization/polling, internal prepared-file ownership, scanner interfaces only if necessary, monitor/lifecycle tests and working-memory accounting tests.
- Implement at most two concurrent baseline preparations and two-slot poll lookahead as specified in the design. Keep actual line evaluation and shared-budget/state reduction ordered and single-owned. Handoff reuses its existing boundaries.
- Preserve file deadlines across preparation and consumption. Preparation never commits a checkpoint or counts a slot as checked. Dispose/drain/discard unconsumed work on every early exit; never replenish a slot while its previous operation still owns resources.
- Acceptance: out-of-order preparation produces stable ordered output and exact aggregate caps; scope sweep/idle/backlog are truthful; cancelled/expired/budget-limited calls cannot skip unseen files or publish late data; replay performs zero new I/O; initialization and handoff preserve continuity. Live buffer count fits the unchanged working reservation.
- Compare complete idle sweeps and initialization against U3's accepted state, and compare active local polls for throughput and correctness. Include snapshot/handoff regressions even though their scheduling is unchanged.
- Validation: V-Core monitor/backend/scanner/resumable/continuation/limits coverage; new concurrency tests three times; full Core suite; real stdio smoke. V-UI affected integration/stdio and monitoring tests, then affected full suite.
- Suggested commit: `perf: prepare bounded monitor files concurrently`.

Evidence/commit: pending.

## U5 - integrated acceptance and delivery

State: **Not started**. Dependency: all prior phases assessed with required evidence.

- Run final full solution Release build and service-safe tests, followed by the separate real interactive desktop lane. Repeat all newly introduced concurrency regressions three times after the last runtime change. Tests must actually execute.
- Compare the U0 baseline and final candidate using the documented paired protocol. Include UNC end-to-end snapshot/search/count/read/initialization/idle calls, actual dashboard open/preview behavior, local active matching/nonmatching/lifecycle cases, response parity/bytes and retained resource counts. Do not write to remote logs to create activity.
- Reconcile exact counts, events, cursor boundaries, error behavior, global/per-group peaks, memory limits, disposal/draining and process exits. Report local mutation coverage separately from actual read-only UNC coverage and fake topology tests.
- Update affected product/developer/security/performance docs only for accepted changes; no product capability or timeout guarantee may be inferred from component timings. Preserve protocol-only stdout and sanitized diagnostics.
- Review relevant diffs and security-sensitive path/network ownership. Record candidate decisions and limitations; leave deferred work excluded. Stage/commit only relevant delivery changes.
- Acceptance: required checks passed, performance decisions justified, no running owned operations, relevant changes committed, checkpoint and outcome ledger complete. Any unavailable required desktop/UNC lane leaves U5 incomplete.
- Suggested commit: `docs: record UNC performance acceptance and outcomes`.

Evidence/commit: pending.

## Decisions, surprises and outcomes ledger

| Item | Current decision/evidence |
| --- | --- |
| UI concurrency | Trial four host/share workers; retention governed by measurement rule. |
| MCP concurrency | Existing global default two retained; topology caps two and preparation lookahead two. |
| Monitor strategy | Parallel preparation only; ordered line evaluation and reduction. |
| Recent baseline changes | Split UTF-8 continuity fixes exist at `9228d9c`; historical measurements are not current-head acceptance. |
| Remote mutation | Excluded; active mutation scenarios use local files and fake UNC tests. |
| U0-U5 outcomes | Pending. No optimization is implemented or accepted by this planning package. |

Append each material discovery with date, evidence, affected phase and resolution. Routine implementation details can be resolved within the design; public behavior changes or new deferred features require a separate scoped decision.
