# MCP many-file search deliverables 1–3 — Execution Plan

This is a living document. Keep `Progress`, `Surprises & Discoveries`,
`Decision Log`, and `Outcomes & Retrospective` current throughout execution.

## Document control

- Owner: Codex implementation task started 2026-08-26.
- Source tracker: `LogReader/docs/workbench/explorations/2026-08-25-mcp-many-file-search.md` (ignored/local; never force-add).
- Tracked plan: `.agent/plans/mcp-many-file-search-deliverables-1-3.md`.
- Commit policy: one local commit for each coherent work unit A–F; no push, amend, squash, or rebase.

## Resume checkpoint

- Current work unit: complete.
- Next action: pending follow-up milestone for aggregation and first-class count workflows, building on the completed exact `countsOnly` foundation. Within-file retained-hit continuation is lower priority and remains deferred.
- Worktree at start: clean on `main`; the tracker is ignored and the plan did not yet exist.

## Purpose and observable outcome

`search_logs` must distinguish returned samples from complete matching-line and occurrence counts, report explicitly incomplete work, harden the scan path, and traverse configured selections larger than 50 files through stable authorized pages. The observable result is a bounded v2 search response whose counts are trustworthy for its declared page/scope, whose cursor cannot be forged or replayed across process/catalog/request boundaries, and whose output remains deterministic, redacted, and protocol-clean.

## Scope

- Work units A–F from the local tracker, in order.
- Core scanner accounting and stable-generation evidence.
- MCP result modes, additive v2 contract fields, completeness reasons, statistics, and timestamp documentation.
- Single encoding resolution inside the disk lease, mixed local/UNC scheduling, ordered producer/reducer behavior, and bounded instrumentation.
- Pure configured-selection paging and process-scoped signed MCP search cursors.
- Focused, full-solution, published-artifact, stdio, security, and representative 50/100/500/1,000-file validation.

## Non-goals

- Within-file retained-hit continuation (deliverable 4).
- Aggregation, generic extraction, relative-time expressions, time buckets, caches, indexes, arbitrary physical paths, or domain-specific log knowledge.
- Raising the 50-file page/work-unit cap.

## Definitions

- Returned hit: a retained serialized matching-line sample. Existing `TotalHitCount` continues to mean this.
- Matching line: an evaluated line satisfying query and timestamp filters, regardless of retention.
- Match occurrence: one literal/regex match span; a matching line may contain several.
- Page complete: every selected file and eligible line in the current resolver page was evaluated successfully against stable generation evidence.
- Query complete: the page is complete and no resolver continuation remains.
- Exact count: a count whose entire declared scope is page/query complete; otherwise it is a lower bound or unavailable and carries stable reasons.

## Existing behavior and evidence

- `SearchService` stops scanning when `HitLimitExceeded` becomes true; hits and counts are currently conflated.
- `HeadlessLogQueryBackend` detects encoding before the disk gate and repeats detection while mapping results.
- `DashboardSelectionResolver` rejects when resolved physical paths exceed `MaxResolvedFiles`.
- `LogSearchResult.TotalHitCount` is computed from serialized retained hits and must retain that meaning.
- Backend disk/UNC gates currently acquire disk capacity before UNC capacity, allowing UNC waiters to occupy local capacity.
- Selection results and scanner batch arrays already preserve configured/input order, providing a base for an ordered reducer.

## Decisions and invariants

- Wire compatibility: additive schema version 2. Preserve `TotalHitCount` as returned-hit count and add explicit `ReturnedHitCount`, `MatchingLineCount`, and `MatchOccurrenceCount`; serialization/schema tests lock this meaning.
- Generic modes: `samples`, `matchesOnly`, and `countsOnly`. Ordinary UI requests retain stop-at-retention-limit behavior by default; only explicit count-oriented MCP requests continue evaluation.
- Completeness is conservative: cancellation, timeout, file errors, response truncation, unstable/unknown generation correlation where stability cannot be established, or remaining pages prevent falsely exact query totals.
- Resolver paging state is pure core data. Cursor encoding/signing remains infrastructure/MCP-side and contains no physical paths.
- Every page rereads the catalog, validates its revision/request fingerprint, and re-resolves configured membership.
- Cursor signing is process-scoped with an in-memory random key; restart invalidates old cursors by design.
- Serialized files remain in configured catalog order independent of work completion order.
- Diagnostics contain only bounded numeric/categorical measurements; never paths, storage roots, usernames, or log content.

## Open questions

- None currently blocking. Exact incomplete-reason names and the minimal reducer API will be finalized with tests during units B and D.

## Milestones / issue summary

- A: scanner accounting primitives — complete and committed (`4b88a87`).
- B: MCP count/mode/completeness contract — complete and committed (`ac69bea`); completes deliverable 1.
- C: encoding and disk-lease cleanup — complete and committed (`6825dfc`).
- D: scheduling/reducer/instrumentation seam — complete and committed (`24257d5`); completes deliverable 2.
- E: pure configured-selection paging — complete and committed (`bd7b35f`).
- F: signed search cursor/MCP integration — complete and committed (`209e199`); completes deliverable 3.
- G: stable cursor reference date — complete, validation passed; RC review fix 1.
- H: explicit 2,000-candidate/cursor capacity — complete and committed (`0e85c12`); RC review fix 2.
- I: bounded provenance metadata — complete and committed (`663ad08`); RC review fix 3.

## Progress

- [x] 2026-08-26: Read repository `AGENTS.md`, global `PLANS.md`, the full local tracker, validation guidance, and initial scanner/backend/resolver/wire code.
- [x] 2026-08-26: Verified the starting worktree is clean and no competing execution plan exists.
- [x] 2026-08-26: Work unit A implemented; focused build succeeded with 0 warnings/errors and 92/92 `SearchServiceTests` passed.
- [x] 2026-08-26: Work unit A committed locally as `4b88a87` (`feat: add complete scanner match accounting`).
- [x] 2026-08-26: Work unit B implemented; focused Core build succeeded, 149 scanner/backend/tool tests passed, and 3/3 MCP stdio protocol tests passed.
- [x] 2026-08-26: Work unit B committed locally as `ac69bea` (`feat: expose trustworthy MCP search counts`).
- [x] 2026-08-26: Work unit C implemented; Core build succeeded with 0 warnings/errors and 163/163 focused scanner/backend/encoding tests passed.
- [x] 2026-08-26: Work unit C committed locally as `6825dfc` (`refactor: resolve search encoding inside disk lease`).
- [x] 2026-08-26: Work unit D implemented; build succeeded, 161/161 focused scheduler/scanner/backend/tool tests passed, and the measurement script parsed without errors.
- [x] 2026-08-26: Work unit D committed locally as `24257d5` (`perf: harden mixed-target MCP scan scheduling`).
- [x] 2026-08-26: Work unit E implemented; Core build succeeded with 0 warnings/errors and 91/91 focused resolver/backend/configured-selection tests passed.
- [x] 2026-08-26: Work unit E committed locally as `bd7b35f` (`feat: page configured log selections`).
- [x] 2026-08-26: Work unit F implemented; builds succeeded, 193/193 focused tests and 3/3 MCP stdio protocol tests passed, and the paged measurement script parsed without errors.
- [x] 2026-08-26: Work unit F committed locally as `209e199` (`feat: add signed MCP search pagination`).
- [x] 2026-08-26: Full solution build passed with 0 warnings/errors; full test run passed 1,389/1,389 tests (461 Core, 928 WPF).
- [x] 2026-08-26: Developer Guide focused MCP command passed 75/75 tests; restore emitted NU1900 warnings because vulnerability-service metadata was unreachable.
- [x] 2026-08-26: Portable publish and published `WeezTail.Mcp.exe` stdio artifact smoke passed.
- [x] 2026-08-26: 50/100/500/1,000-file cold/warm cursor measurements completed with exact final coverage, no search failures/skips/incomplete reasons, bounded response/cursor state, clean stderr, and successful cancellation release.
- [x] 2026-08-26: Validation harness, smoke-test correction, scale evidence, and documentation committed as `820ab6c` (`docs: record MCP search scale validation`). Final worktree audit was clean; commits A–F are sequential; the local tracker remains ignored and untracked.
- [x] 2026-08-26: Work unit G implemented; cursor payload version 2 carries the first page's reference-date day number, continuation pages do not consult the live clock, and docs describe midnight stability. Focused build passed with NU1900 feed-availability warnings and 4/4 cursor tests passed.
- [ ] Work unit G committed.
- [x] 2026-08-26: Work unit G committed locally as `a07f6a2` (`fix: freeze MCP search cursor reference date`).
- [x] 2026-08-26: Work unit H implemented. Logical expansion rejects candidate 2,001 before path probes/scans; status/tool/docs expose the 2,000-candidate ceiling; cursor encoding validates its payload and encoded length. Focused build passed with NU1900 feed warnings and 38/38 resolver/cursor/backend/status tests passed.
- [x] 2026-08-26: Work unit H committed locally as `0e85c12` (`fix: bound MCP search cursor capacity`).
- [x] 2026-08-26: Work unit I implemented. Search/read/tail and resolved-file-error responses share a deterministic 25% provenance-string budget, return complete configured-order prefixes with additive total/truncated fields, and preserve exact search counts when only provenance is compacted. Focused Core build passed with NU1900 feed warnings and 67/67 backend/tool tests passed, including maximum-length tree paths.
- [x] 2026-08-26: Work unit I committed locally as `663ad08` (`fix: bound MCP provenance metadata`).
- [x] 2026-08-26: RC review fixes passed full solution, focused MCP, portable-artifact, stdio-smoke, and 50/100/500/1,000/2,000-file validation. Documentation and repository audit are complete; generated reports and the synchronized tracker remain ignored.

## A — Scanner accounting primitives

- State: complete.
- Dependencies: none.
- Purpose: decouple evaluation and counting from retained samples without changing default UI work.
- Expected implementation areas: core search request/result contracts, `ISearchService`, `SearchService`, `SearchServiceTests`.
- Tasks: add matching-line/occurrence counts, explicit continue-after-retention option, cancellation/completion metadata, stable-generation/evaluated-line evidence, and tests for caps, literals/regex, timestamp bounds, cancellation, growth/replacement/instability.
- Acceptance criteria: capped count-oriented scans finish stable scope and retain bounded samples; default requests still stop at cap; counters reconcile; incomplete evidence is preserved.
- Focused validation: build Core tests project; filtered `SearchServiceTests`.
- Progress/evidence: Added opt-in continue-after-retention behavior; matching-line and occurrence counters; explicit evaluation-complete/cancelled evidence; literal, regex, timestamp, compatibility, cancellation, growth, replacement, and repeated-instability assertions. `dotnet build LogReader\LogReader.Core.Tests\LogReader.Core.Tests.csproj --no-restore -m:1` succeeded; `dotnet test ... --no-build --no-restore --filter "FullyQualifiedName~SearchServiceTests"` passed 92/92.

## B — MCP count, mode, and completeness contract

- State: complete.
- Dependencies: A.
- Purpose: expose trustworthy additive v2 search semantics within the current 50-file page.
- Expected implementation areas: query DTOs, backend mapping/validation, MCP tool schema, serialization/protocol tests, MCP docs.
- Tasks: modes; returned/matching-line/occurrence and per-file counts; selected/scanned/skipped/failed/remaining/matched-file statistics; completeness/exactness/reasons; compatibility/schema version; timestamp syntax docs; bounded/redacted partial errors.
- Acceptance criteria: no timeout/cancellation/failure/truncation/mutation can produce falsely exact totals; `TotalHitCount` meaning remains locked.
- Focused validation: `HeadlessLogQueryBackendTests`, `McpLogToolsTests`, MCP stdio protocol tests.
- Progress/evidence: Added `samples`, `matchesOnly`, and `countsOnly`; additive search contract version 2 fields; legacy/explicit returned hits; matching-line/occurrence and per-file counts; selected/scanned/skipped/failed/remaining/matched-file statistics; page/query exactness/completion/reasons; conservative file-mutation and response-truncation handling; schema/serialization/backend tests; timestamp syntax and compatibility documentation. Core build: 0 warnings/errors. Filtered Core tests: 149/149. WPF test-project build: 0 warnings/errors. `McpStdioProtocolTests`: 3/3.

## C — Encoding and disk-lease cleanup

- State: complete.
- Dependencies: B.
- Purpose: perform one encoding resolution per file inside its bounded disk operation and reuse it for scan/mapping/context.
- Expected implementation areas: backend search pipeline, scanner batch seam, encoding fakes/tests.
- Tasks: remove pre-gate/repeated probes; carry resolved encoding; sanitize missing/locked/invalid/changing-file failures.
- Acceptance criteria: one bounded probe per attempted file; no physical path or unstable exception details cross the wire.
- Focused validation: backend encoding/error/cancellation tests plus build.
- Progress/evidence: Added a bounded encoding-resolver batch API; automatic resolution now occurs after operation admission, the resolved value is carried on internal scan results, and backend mapping/context reuse it without an automatic re-probe. A UTF-16 context test proves one automatic resolution across scan/result/context. Existing missing, locked, fallback/invalid-byte detection, cancellation, and changed-file coverage passed in the 163-test focused run.

## D — Scheduling, reducer, and instrumentation seam

- State: complete.
- Dependencies: C.
- Purpose: avoid UNC/local head-of-line blocking, preserve ordered bounded reduction, and expose safe measurements.
- Expected implementation areas: backend operation admission, adaptive policy/scheduler reuse, search batch/reducer abstraction, diagnostics and measurement script/docs.
- Tasks: schedule by local/root/host policy; acquire UNC before shared disk capacity or equivalent non-blocking admission; ordered production/reduction; sample early-stop vs exact full-page scan; numeric stats.
- Acceptance criteria: deterministic catalog order, gates/cancellation always release, local work is not starved by UNC waiters, statistics reveal no paths/content.
- Focused validation: adaptive concurrency, cancellation, deterministic order, local/UNC/mixed backend tests and measurement smoke.
- Progress/evidence: UNC admission now waits for the UNC gate before consuming shared disk capacity; bounded search producers use existing root/host interleaving and write into catalog-indexed result slots for ordered reduction; count modes still evaluate every selected file while the seam can later stop sample production without changing reducer order. Added safe per-query bytes/elapsed/files/gate statistics and measurement report schema 2 response/count/completion fields. Core build: 0 warnings/errors. Focused tests: 161/161. PowerShell parser: no measurement-script errors.

## E — Pure configured-selection paging

- State: complete.
- Dependencies: D.
- Purpose: return stable bounded configured pages instead of rejecting expansions over 50 files.
- Expected implementation areas: configured-selection contracts/resolver and resolver tests.
- Tasks: continuation state, exact boundaries, global duplicate elimination, recursive/nested traversal, date candidates, errors/changed candidates, cancellation.
- Acceptance criteria: deterministic pages cover authorized deduplicated configured files exactly once; no cursor/MCP SDK types in core.
- Focused validation: exhaustive `DashboardSelectionResolverTests` plus build.
- Progress/evidence: Core continuation state contains next stable-file index and sorted SHA-256 physical-path identities, never cursor bytes or MCP SDK types. Logical expansion is authorization-validated in configured order without path probes; each page processes at most its file-limit candidates, advances across errors/duplicates, and re-deduplicates globally. Added exact-50, 123-file, cross-page duplicate, probe bound, cancellation, and invalid-continuation tests; existing nested targets, topology, missing/date candidates, candidate changes/escape, and serialization-redaction tests remain green. Backend preserves reject-on-overflow until F. Build: 0 warnings/errors. Focused tests: 91/91.

## F — Signed search cursor and MCP integration

- State: complete.
- Dependencies: E.
- Purpose: expose secure search paging and page/query completeness.
- Expected implementation areas: cursor codec, backend, DTO/tool schema, stdio/security/docs/tests, measurement script.
- Tasks: versioned HMAC cursor bound to process key, catalog revision, normalized request fingerprint, targets/options/date offset, and resolver state; reauthorization; safe stale/tampered/mismatched/malformed/prior-process rejection.
- Acceptance criteria: >50 selections traverse without skip/duplicate; page totals exact only for complete pages and query totals exact only after all pages; no within-file continuation.
- Focused validation: cursor unit/security, backend, tool-schema, serialization, stdio protocol, docs, 100/500/1,000-file traversal.
- Progress/evidence: Added a versioned HMAC-SHA256 process-scoped search cursor bound to catalog revision, normalized full request and target fingerprints, date offset, resolver state, and cumulative count/completion state. Every page rereads/revalidates catalog membership. Result contract distinguishes page/cumulative counts and page/query completeness, carries `unvisited_pages`, and exposes `nextCursor`. A real backend traversal covered 105 files in `[50, 50, 5]` pages with no skips/duplicates and exact final cumulative counts. Tampered, malformed, query/target/date-mismatched, stale-catalog, and different-process-key cursors fail with stable errors. Tool schema/docs/stdio and measurement harness are updated; within-file continuation remains absent. Builds: 0 warnings/errors. Focused tests: 193/193. Stdio: 3/3. Script parse: clean.

## Final validation and demonstration

- `dotnet build LogReader.sln`
- `dotnet test LogReader.sln --no-build`
- Focused MCP command from Developer Guide.
- Portable MCP publish and `packaging/scripts/Test-McpStdioArtifact.ps1`.
- Representative 50/100/500/1,000/2,000-file cold/warm traversals recording latency, selected/scanned/skipped/failed/remaining, bytes evaluated, response size, private memory, gate concurrency, cancellation, cursor coverage, and partial/incomplete state.
- `git diff --check`, staged diff checks at each commit, final status/log audit, and confirmation that tracker was neither staged nor committed.

Final evidence: `dotnet build LogReader.sln` succeeded with 0 warnings/errors. `dotnet test LogReader.sln --no-build` passed 1,389/1,389 tests. The focused Developer Guide filter passed 75/75 tests (with NU1900 vulnerability-feed availability warnings during restore). `Publish-Portable.ps1` validated the portable directory and its real stdio MCP executable. Scale runs held input near 21.5 MB and completed in 1/2/10/20 pages; cold/warm search latency was 427/108, 678/147, 1,792/270, and 3,404/429 ms. Maximum page responses were 58,825/64,965/102,303/148,841 bytes; maximum cursors were 0/3,178/21,848/45,174 characters. All final queries selected and searched 50/100/500/1,000 files with zero skipped, failed, or remaining files and no incomplete reasons. Peak disk concurrency was 2, cancellation release was 1.7–4.5 ms, and final private memory was 52.5–83.5 MB.

RC review evidence: the full build succeeded with three NU1900 warnings because the vulnerability metadata feed was unavailable and no errors. A normal-filesystem full solution run passed 1,397/1,397 tests (469 Core and 928 WPF); the initial sandboxed WPF run's cache-access/timing failures disappeared on that clean rerun. The focused Developer Guide filter passed 81/81 tests. Portable publish and the real stdio artifact smoke passed. The refreshed scale matrix held input near 21.5 MB and completed in 1/2/10/20/40 pages; cold/warm search latency was 416/120, 708/162, 1,795/246, 3,384/336, and 6,321/646 ms. Maximum page responses were 64,589/70,813/108,153/154,689/248,229 bytes, and maximum cursors were 0/3,220/21,891/45,216/91,886 characters. All final queries selected and searched 50/100/500/1,000/2,000 candidates with zero skipped, failed, or remaining files, exact final counts, no incomplete reasons, clean stderr, and 1.5–4.4 ms cancellation release.

## Surprises & discoveries

- The existing `TotalHitCount` is explicitly the sum of serialized hit arrays, making preservation as returned-hit count the safest compatibility choice.
- The scanner already records generation correlation and evaluated-through-line internally, but cancellation is represented only by an early return and must become explicit completion evidence.
- A retained-hit cap is detected only when the next matching line is evaluated. Default scans therefore expose a trustworthy lower bound that can exceed retained hits by one, while count-oriented scans continue to the end.
- The MCP SDK schema exporter cannot represent an optional positional `ImmutableArray<T>` default. New per-file arrays are initialized record properties instead, preserving the wire shape and deterministic empty arrays.
- Append-only growth can preserve file identity; exactness therefore also consumes explicit during/after-scan size/write-time mutation evidence rather than relying only on generation-token correlation.
- Indexed context acquisition normalizes an already-resolved manual encoding without probing file content; the single automatic probe remains owned by the bounded search operation.
- The existing adaptive scheduler's interleaved work order plus indexed result slots provides the producer/ordered-reducer boundary without changing serialized catalog order.
- Per-query gate metrics use an `AsyncLocal` metrics object shared by that request's worker tasks, avoiding path-bearing diagnostics and cross-request peak contamination.
- Stable-file logical expansion can safely exceed the old 500-entry exploration bound because catalog validation already caps files/memberships at 50,000/100,000; only one page of path candidates is probed per call.
- Cross-page deduplication state stores sorted SHA-256 identities of normalized paths, not physical paths. Empty pages can legitimately occur when a page consists only of already-seen duplicates or file errors and must still return continuation.
- A 128-bit prefix of SHA-256 path identity keeps the signed cursor bounded for the 1,000-file release gate while retaining negligible collision probability; cursor payloads accept at most 2,000 identities and 100,000 characters.
- Cumulative failure reasons exclude transient `unvisited_pages`; that reason is added to intermediate responses only, allowing the final page to become query-exact when every page itself completed exactly.
- The first portable artifact smoke exposed an obsolete schema-version-1 assertion in its packaging test; updating it to the v2 envelope made the full portable publish and standalone smoke pass.
- The scale harness still capped its input parameter at 50 despite already paging internally. Raising only the harness range to 1,000 and recording maximum page/cursor sizes enabled the release matrix without changing the product's 50-file work-unit cap.
- At the 2,000-candidate boundary, the final signed cursor reached 91,886 characters and remained below the codec's 100,000-character decoder limit; the maximum serialized page reached 248,229 bytes because the 200,000-character product limit budgets retained content strings, not JSON framing or cursor bytes.

## Risks and mitigations

- Contract growth could exceed response bounds: omit hits/context in count modes and retain existing character budgets.
- Full counts can consume the deadline: exactness remains conservative and cancellation/deadline reasons are explicit.
- Paging duplicates can arise from multiple configured IDs/targets resolving to one path: continuation state must preserve global seen identity across pages without exposing paths.
- Cursor replay or privilege drift: process-scoped signature plus catalog/request binding and full reauthorization on every page.
- Tests may depend on UI stop-at-cap behavior: new evaluation mode defaults to stop and gets explicit compatibility tests.

## Pending follow-up work

- Aggregation and first-class count workflows are the next planned search milestone. Preserve exact/incomplete count semantics while adding compact overall, per-file, and time-bucket views that do not require retaining raw hits.
- Define the smallest useful contract and validation plan before implementation, including reconciliation between aggregate totals and the existing exact counting path.

## Deferred work

- Deliverable 4 within-file hit continuation and generation/line-position cursor state.
- Relative windows beyond those required for the first aggregation slice, generic extraction, caches, and indexing.

## Decision log

- 2026-08-26: Use additive schema version 2 and preserve `TotalHitCount` as returned serialized hits; new explicit fields carry exact counts.
- 2026-08-26: Keep full-evaluation opt-in at the core request level so desktop UI behavior remains bounded by default.
- 2026-08-26: Keep pure resolver state separate from opaque signed cursor encoding.
- 2026-08-26: Treat scanner `IsEvaluationComplete` and generation correlation as independent evidence; MCP exactness requires both successful full evaluation and stable/current generation evidence.
- 2026-08-26: `samples` preserves early stopping; `matchesOnly` suppresses context and completes page counting; `countsOnly` retains no hits and completes page counting. Any response/sample truncation conservatively prevents exactness.
- 2026-08-28: Promote aggregation and first-class count workflows from deferred work to the next pending search milestone. Keep within-file retained-hit continuation deferred behind it because trustworthy compact counts answer the higher-value operational question without enumerating every raw hit.

## Outcomes & retrospective

- Deliverables 1–3 and the three RC review fixes are implemented as nine reviewable code commits. MCP search now separates retained samples from exact counts, reports conservative completion evidence, performs encoding and scheduling work under bounded leases, securely traverses up to 2,000 configured candidates in 50-file pages with signed process/catalog/request/date-bound cursors, and bounds explanatory provenance independently of exact counts.
- The complete 2,000-candidate local RC run used 40 deterministic 50-file pages, evaluated the full 21.75 MB data set, returned exact cumulative results with no skip/failure/remaining state, and stayed within the declared deadline and cursor bound.
- No dependencies or public desktop behavior were changed. Aggregation/count workflows are now the next pending milestone; deliverable 4 within-file retained-hit continuation remains intentionally deferred behind them.

## Handoff history

- 2026-08-26: Work unit A complete and validated; resume at work unit B contract/backend/schema work.
- 2026-08-26: Work unit B complete and validated; resume at work unit C encoding lease ownership.
- 2026-08-26: Work unit C complete and validated; resume at work unit D admission, reducer, and statistics.
- 2026-08-26: Work unit D complete and validated; resume at work unit E pure resolver paging.
- 2026-08-26: Work unit E complete and validated; resume at work unit F signed cursor and MCP integration.
- 2026-08-26: Work unit F complete and focused validation passed; resume at final solution/artifact/scale validation.
- 2026-08-26: Final solution, artifact, and 50/100/500/1,000-file scale validation passed; resume only for final documentation commit and repository audit.
- 2026-08-26: Validation evidence committed as `820ab6c`; final audit confirmed a clean worktree and that the ignored tracker was never staged or committed. Plan complete.
- 2026-08-26: RC fixes G–I and final validation complete. Full solution, focused MCP, published stdio artifact, and the expanded 2,000-candidate boundary passed; the final evidence update is ready for its local commit. Full-catalog continuation, within-file retained-hit continuation, aggregation, and extraction remain deferred.
- 2026-08-28: Backlog priority updated after product review: aggregation and first-class count workflows are pending next; full-catalog and within-file retained-hit continuation remain deferred.
