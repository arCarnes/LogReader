# MCP first-class log counting and aggregation — Execution Plan

This is a living document. Keep `Progress`, `Surprises & Discoveries`,
`Decision Log`, and `Outcomes & Retrospective` current throughout execution.

## Document control

- Owner: Codex implementation task started 2026-08-29.
- Source tracker: `LogReader/docs/workbench/explorations/2026-08-25-mcp-many-file-search.md` (ignored/local; never force-add).
- Tracked plan: `.agent/plans/mcp-log-count-aggregation.md`.
- Commit policy: retain the completed feature commits; add one local commit per RC review fix plus one documentation/evidence commit; do not push.

## Resume checkpoint

- Current work unit: complete.
- Next action: none; RC review fixes and refreshed release evidence are ready for review.
- Starting branch: `main`, one documentation commit ahead of `origin/main`; tracked worktree clean.

## Purpose and observable outcome

Add a discoverable `count_logs` MCP tool that evaluates up to 2,000 configured candidates in one bounded call, using internal 50-file work units. Complete stable scans return exact matching-line and occurrence totals, per-file detail, and optional dense time buckets. Deadline, file, or generation failures return explicit lower bounds without losing completed work.

## Scope

- Dedicated headless/MCP count query and result contract.
- Overall, per-file, and minute/hour/day bucket counts.
- Absolute timestamp bounds plus `today` and `last <n><m|h|d>` relative windows.
- One captured server-local clock/reference date per call.
- Deterministic bounded per-file/provenance metadata.
- Focused, full-solution, portable-artifact, stdio, desktop-regression, and 2,000-candidate validation.

## Non-goals

- WPF UI changes or changes to normal desktop search defaults.
- Changes to `search_logs` contract version 2 or its cursor behavior.
- More than 2,000 configured candidates or more than 50 files per internal work unit.
- File-by-time cross-products, arbitrary grouping/sorting, structured extraction, caches, indexes, or within-file retained-hit continuation.

## Definitions

- Matching-line count: number of evaluated lines satisfying the query and time filter.
- Occurrence count: number of literal/regex spans; one matching line can contain multiple occurrences.
- Dense bucket series: every aligned bucket overlapping the resolved inclusive range, including zero-count buckets.
- Exact count: the entire declared scope was evaluated successfully against stable/current file generations.
- Partial count: a trustworthy lower bound accompanied by stable incomplete reasons.

## Existing behavior and evidence

- `countsOnly` already computes exact page/cumulative line and occurrence counts but requires callers to traverse signed 50-file cursor pages.
- The resolver validates the 2,000-candidate logical ceiling before path probes and preserves configured order and cross-page physical-path deduplication.
- The scanner already parses timestamps for bounded searches and exposes generation/completion evidence; aggregation is not currently retained.
- The existing 2,000-candidate/21.75 MB traversal completed in 6.321 seconds cold under the 30-second deadline.

## Decisions and invariants

- Add `count_logs` instead of overloading `search_logs` result modes.
- Count calls have no cursor and internally traverse resolver pages under one request deadline.
- Relative windows are case-insensitive `today` or `last <positive integer><m|h|d>`, capped at 365 elapsed days and mutually exclusive with absolute bounds.
- Server-local time defines offset-less input, relative windows, and dated bucket boundaries; resolved bounds, offsets, and time-zone ID are returned.
- `last Nd` means `N × 24` elapsed hours; `today` means local midnight through the captured request instant.
- Bucketing requires a relative window or two absolute bounds and admits at most 1,000 dense buckets.
- Dated ranges use dated server-local buckets. Time-only ranges use minute/hour time-of-day buckets; day buckets are rejected.
- Bucket counts are global across files. Per-file-by-time output is out of scope.
- Per-file response records contain matched files plus incomplete/error files in configured order. Metadata truncation never invalidates counts.

## Open questions

- None. The approved plan is decision complete.

## Milestones / issue summary

- A. Opt-in scanner bucket accounting.
- B. One-call count orchestration and bounded partial outcomes.
- C. MCP tool/schema/status integration and documentation.
- D. Full validation, artifact smoke, scale measurement, and evidence.
- E. DST-safe minute/hour/day boundary generation.
- F. Partial lower bounds for pre-scan internal deadlines.
- G. Configured-order count records across mixed resolver outcomes.
- H. RC review validation and evidence refresh.

## Progress

## A. Opt-in scanner bucket accounting

- State: complete.
- Dependencies: existing `SearchRequest`, `SearchResult`, `TimestampParser`, and bounded scan paths.
- Purpose: count matching lines and occurrences into deterministic bucket keys without retaining hits.
- Expected implementation areas: Core search models/timestamp aggregation helpers, Infrastructure `SearchService`, focused scanner tests.
- Tasks: define bounded aggregation plan/result types; reuse already-parsed timestamps; support dated local and time-of-day buckets; preserve all non-aggregation behavior.
- Acceptance criteria: totals reconcile with buckets; multi-occurrence lines count correctly; default WPF/search requests produce unchanged results; invalid/unbounded plans are rejected before scanning.
- Focused validation: Core build and `SearchServiceTests` aggregation/default-behavior filters.
- Progress/evidence: Added an immutable dated/time-of-day bucket plan to `SearchRequest`, sparse per-file line/occurrence counters to `SearchResult`, and shared accounting across streaming and indexed-range search paths. Aggregation remains null by default. Core test-project build passed with zero warnings/errors; all 95 `SearchServiceTests` passed, including new dated, time-only, multi-occurrence, overlap-validation, and unchanged desktop early-stop coverage.

## B. One-call count orchestration

- State: complete.
- Dependencies: A and existing resolver paging/security invariants.
- Purpose: expose exact or explicitly partial query-wide counts without client cursor traversal.
- Expected implementation areas: Core query contracts/interface, Infrastructure backend, backend tests.
- Tasks: resolve time once; validate query/window/buckets; loop 50-file resolver pages; preserve completed batch results on deadline; reduce totals/buckets/statistics; compact per-file/provenance metadata.
- Acceptance criteria: 2,000 candidates complete in one call; candidate 2,001 causes zero probes; deadline preserves lower bounds; failures and generation changes prevent false exactness; no paths leak.
- Focused validation: backend resolver/count/security/budget/deadline tests.
- Progress/evidence: Added a dedicated query/result contract and one-call reducer that captures one clock/reference date, traverses resolver pages in 50-file work units, retains completed ordered slots on deadline, and returns exact or explicitly partial totals. Per-file/provenance metadata is independently bounded. Tests cover exact 123- and 2,000-candidate traversals, candidate 2,001 with zero probes/searches, dense relative buckets, deadline lower bounds, caller cancellation, and provenance compaction. Focused backend/time/tool build passed with zero warnings/errors and 82/82 tests passed.

## C. MCP contract and documentation

- State: complete.
- Dependencies: B.
- Purpose: make counting discoverable and accurately documented.
- Expected implementation areas: MCP tools/stdio host, public docs, status limits, protocol tests.
- Tasks: register `count_logs`; publish schema descriptions; expose 365-day/1,000-bucket limits; document time/DST/exactness/compaction; cross-reference from `search_logs`.
- Acceptance criteria: schema is additive, `search_logs` remains v2 and unchanged, stdio serialization is protocol-clean, and portable help matches runtime behavior.
- Focused validation: MCP tool/schema/status/stdio tests and documentation assertions.
- Progress/evidence: Registered `count_logs`, added typed request mapping and schema coverage, exposed the additive limits through status, and extended the real executable protocol and portable-artifact smoke tests. Public getting-started, server, architecture, security, mainline-impact, developer, and performance documentation now covers exact/lower-bound semantics, relative windows, local/DST behavior, time-only ranges, dense bucket limits, and metadata compaction. MCP stdio protocol tests passed 3/3, and the published executable passed the real six-tool stdio smoke test.

## D. Final validation and demonstration

- State: complete.
- Dependencies: A–C.
- Tasks: full build/tests; targeted SearchService/SearchPanel WPF regressions; portable publish; real `count_logs` stdio smoke; 50/100/500/1,000/2,000 candidate unbucketed and bucketed measurements; synchronize tracker and plan.
- Acceptance criteria: no new warnings/errors or failed tests; WPF behavior unchanged; 2,000-candidate boundary completes exactly inside 30 seconds with bounded memory/response; generated artifacts remain ignored.
- Progress/evidence: `dotnet build LogReader.sln` passed with zero warnings/errors; `dotnet test LogReader.sln --no-build` passed 1,414/1,414 tests (486 Core, 928 application/integration); the focused WPF search/filter regression selection passed 164/164. The Release self-contained portable application and MCP sidecar published and validated, and the sidecar passed its real stdio smoke test. The 50/100/500/1,000/2,000-file matrix completed exactly with no stderr or process failures. Every shape contained and reconciled 2,000 known events. At the 2,000-file/250-line/21.76 MB boundary, unbucketed counts completed in 654 ms cold and 561 ms warm and minute-bucketed counting completed in 963 ms, all within 30 seconds; peak working set was 197.3 MB. Metadata compaction was disclosed without invalidating exact totals.

## E. DST-safe dated bucket boundaries

- State: complete.
- Dependencies: completed milestone A.
- Purpose: emit chronological minute/hour buckets through local clock jumps and one calendar bucket per local day.
- Expected implementation areas: `CountTimeWindowResolver` and focused resolver/backend tests.
- Tasks: construct aligned local candidates, retain both ambiguous minute/hour offsets, skip invalid walls, deduplicate day boundaries, and preserve the 1,000-bucket cap.
- Acceptance criteria: fall-back minute sequences are complete and one elapsed minute wide; spring-forward gaps are skipped; repeated hour behavior remains correct.
- Focused validation: time-window resolver tests and end-to-end bucket reconciliation tests.
- Progress/evidence: Dated buckets now enumerate aligned local wall boundaries, retain both offsets for ambiguous minute/hour walls, skip invalid walls, sort by UTC instant, and emit one day boundary per local date. Oversized minute/hour spans are pre-rejected before enumeration. Focused resolver/backend validation passed 9/9 tests, including complete repeated fall-back minute sequences, one-minute elapsed durations, spring-forward gaps, repeated hours, and explicit-offset end-to-end assignment. Restore/build reported only existing NU1900 vulnerability-feed availability warnings.

## F. Pre-scan deadline lower bounds

- State: complete.
- Dependencies: existing count accumulator/envelope reducer.
- Purpose: return a valid zero or accumulated lower bound whenever the internal deadline expires, including before scanning.
- Expected implementation areas: count orchestration cancellation classification and backend tests.
- Tasks: require actual linked-deadline cancellation, centralize internal deadline reduction, and preserve caller/backend cancellation behavior.
- Acceptance criteria: gate/catalog/scan deadline paths return partial count results; caller cancellation and shutdown return no result.
- Focused validation: gate contention, blocked catalog, existing partial scan, caller cancellation, and shutdown tests.
- Progress/evidence: Internal deadline classification now requires the linked deadline source to be cancelled while caller and backend tokens remain active. The outer orchestration converts gate, catalog, and resolver deadline cancellation into the same non-null partial count envelope used for scan deadlines; caller cancellation and backend shutdown retain null-result errors. Focused validation passed 5/5 deadline/cancellation/shutdown tests, including zero lower bounds before selection and accumulated lower bounds during scanning. Builds reported only existing NU1900 vulnerability-feed availability warnings.

## G. Configured-order count records

- State: complete.
- Dependencies: resolver paging and count metadata reducer.
- Purpose: preserve logical configured order when selection errors and successful scans are interleaved.
- Expected implementation areas: JSON-ignored resolver ordering metadata, count accumulator, resolver/backend tests.
- Tasks: assign absolute stable-file indexes, retain the earliest index through path deduplication, and order response-budget admission by that index.
- Acceptance criteria: mixed success/error records and truncated prefixes remain in configured order across pages and aliases without wire changes.
- Focused validation: resolver ordering metadata plus backend full/prefix order tests.
- Progress/evidence: `ConfiguredLogSelectionResult` now carries an immutable JSON-ignored stable-file-index map populated with absolute logical indexes during resolver paging. Count file entries retain that index and are sorted globally before response-budget prefix admission; physical duplicates preserve the earliest emitted file. Focused validation passed 33/33 resolver/order tests. Coverage includes interleaved matched/error/matched records, a 50-file page boundary, a later physical-path alias, constrained metadata prefix ordering, accurate record totals, and absence of ordering metadata from JSON. Builds reported only existing NU1900 vulnerability-feed availability warnings.

## H. RC review validation and evidence

- State: complete.
- Dependencies: E–G.
- Purpose: restore release-candidate confidence after review fixes.
- Tasks: full and UI-focused validation, portable publish/stdio smoke, 2,000-file count measurement, documentation/tracker synchronization, and final audit.
- Acceptance criteria: all validation passes, generated artifacts remain ignored, tracked worktree is clean, and no push occurs.
- Progress/evidence: Focused resolver/scanner/backend/tool validation passed 155/155 tests and targeted WPF search/filter regressions passed 164/164. `dotnet build LogReader.sln --no-restore` succeeded with zero errors and only three existing NU1900 vulnerability-feed availability warnings. The first full run passed Core 493/493 but hit one transient polling timeout in an unrelated WPF test; the isolated retry passed immediately and the second full run passed 1,421/1,421 tests (493 Core, 928 application). The Release portable application/sidecar published, validated, and passed the real six-tool stdio smoke. The 2,000-file/250-line boundary remained exact and complete: unbucketed count completed in 557/545 ms cold/warm, minute-bucketed count in 887 ms, peak working set was 241.1 MB, stderr was clean, and the process exited successfully.

## Final validation and demonstration

- Run narrow scanner tests, then backend/MCP tests after each milestone.
- Run `dotnet build LogReader.sln` and `dotnet test LogReader.sln --no-build`.
- Publish the portable package and smoke its real stdio executable.
- Extend and run the measurement harness through the 2,000-candidate boundary.

## Surprises & discoveries

- The two production search paths already converge on shared match-accounting helpers, so bucket accumulation required no duplicate matching logic and remains opt-in at the request boundary.
- Count-specific partial batch reduction was added as a separate interface path, preserving ordinary search cancellation behavior while retaining completed file slots after an internal deadline.
- Count serialized response size is driven primarily by matched/incomplete per-file records: the 2,000-file fixture returned all 2,000 matched-file records in a 1,462,207-byte response while compacting provenance strings and preserving exact totals. The configured 200,000-character limit intentionally budgets response string content, not JSON properties/framing; the 2,000-candidate ceiling independently bounds record count.
- Chronological boundary enumeration is simpler to verify than incremental wall-clock stepping at fall-back: sorting all valid aligned candidates by instant naturally retains both repeated offset-qualified minute/hour sequences while skipping invalid spring-forward walls.

## Risks and mitigations

- Desktop regression: aggregation remains opt-in and existing batch/cancellation APIs keep their behavior; run targeted and full WPF tests.
- DST/time ambiguity: use injected server-local `TimeZoneInfo`, retain offsets in dated bucket keys, and distinguish time-of-day buckets.
- Deadline data loss: use a count-specific batch outcome that retains completed ordered slots rather than changing existing search cancellation semantics.
- Response growth: cap dense buckets at 1,000 and compact complete per-file/provenance records under explicit budgets.
- False exactness: reuse existing evaluation and generation evidence; metadata truncation is explanatory only.

## Deferred work

- Full-catalog continuation beyond 2,000 candidates.
- Within-file retained-hit continuation.
- File-by-time matrices, arbitrary aggregation/grouping/sorting, structured extraction, caches, and indexes.

## Decision log

- 2026-08-29: Use a dedicated `count_logs` tool with one-call bounded traversal because counting is the primary operational workflow and should not require cursor reconciliation.
- 2026-08-29: Include overall, per-file, and optional time-bucket counts; keep the public output generic and return both line and occurrence totals.
- 2026-08-29: Use server-local time and explicit resolved bounds; support time-only buckets separately rather than inventing dates.
- 2026-08-29: Return matched plus incomplete/error file records and compact metadata independently of exact numeric results.
- 2026-08-30: Reopened the completed count plan for three RC review fixes: chronological DST buckets, pre-scan deadline lower bounds, and configured-order mixed file records. Public count/search contracts remain unchanged.

## Outcomes & retrospective

- `count_logs` is now a first-class six-tool MCP operation that counts a complete supported query scope without exposing search cursors. Exact results include matching-line and occurrence totals everywhere, optional bounded dense buckets, resolved local-time bounds, and deterministic matched/incomplete file detail. Partial deadlines and unstable/error files retain completed lower bounds and stable reasons.
- Desktop behavior stayed unchanged: aggregation is opt-in, no WPF surface was added, and the focused plus full application suites passed.
- The 2,000-candidate boundary is comfortably below the deadline on the measured local 21.76 MB fixture. Response metadata remains independently bounded; retained-hit continuation and broader grouping remain deferred.
- RC review fixes now guarantee complete chronological DST minute/hour series, partial lower bounds even when the deadline expires before selection, and configured-order metadata prefixes across interleaved selection errors and physical aliases without changing either public contract.

## Handoff history

- 2026-08-29: Execution plan initialized from the approved decision-complete specification; resume at milestone A.
- 2026-08-29: Milestone A complete; Core test-project build and 95/95 scanner tests passed. Resume at milestone B.
- 2026-08-29: Milestone B and MCP runtime integration complete; 82/82 focused backend/time/tool tests and 3/3 executable stdio protocol tests passed. Resume at milestone C documentation/artifact work.
- 2026-08-29: Milestones C and D complete. Full build/tests, focused WPF regressions, portable publish, six-tool stdio smoke, and the five-shape scale matrix passed. Implementation is ready for review; no push was performed.
- 2026-08-30: RC review identified three actionable defects; resume at milestone E and retain separate fix commits.
- 2026-08-30: Milestones E–H complete. Focused/full/UI validation, portable publish, real stdio smoke, and the 2,000-file measurement passed; no push was performed.
