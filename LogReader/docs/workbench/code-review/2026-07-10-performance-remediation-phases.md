# WeezTail performance remediation phases

Status: Complete
Scope: Follow-up work from the 2026-07-10 multi-agent review. The numbered remediation work starts at Phase 2; baseline work and Phase 1 quick wins remain excluded, while a newly identified stabilization prelude now precedes the remaining numbered work.

## Correctness-first operating policy

- Treat commit `c9c3c61` (`Rebrand app to WeezTail`) as the interaction baseline. Later work may correct incorrect or unpredictable behavior, but performance work must otherwise preserve that baseline's visible behavior.
- Prioritize correct file-generation handling, stable selection/copy/navigation semantics, leak prevention, and bounded background work over reducing ordinary allocations.
- Target machines have at least 16 GiB of memory. Keeping a defined heavy workload below 1 GiB of total process memory is a soft engineering goal, not a product guarantee.
- Structural optimization work must do at least one of the following: prevent unbounded growth, reduce measured retained or peak memory by roughly 100 MiB, or improve a representative user action by at least 50 ms and 2x. Smaller changes are acceptable only when they are local, low-complexity, and behavior-transparent.
- Never silently omit matches, invalidate selected-item identity, or discard active results to meet a memory target. Any future cap must be explicit, user-visible, and applied early enough to bound peak acquisition rather than trimming only after allocation.
- Preserve the rebrand-era limits of 10,000 displayed search hits per file and 8,192 retained characters per search line unless a separate correctness review changes them.
- Keep filter line-number sets complete. Do not introduce a silent filter cap because incomplete include/exclude scopes can change later search semantics.
- Preserve later correctness or behavior-transparent fixes, including bounded regex/highlight caches, tail-registration catch-up, regex-timeout handling, dashboard progress throttling, and test-isolation fixes.
- A configurable result-memory ceiling is **Deferred by decision**. The Phase 4 measurements calibrate a future default and UX but do not justify a runtime ceiling or setting in the current stabilization work.

## Tracking conventions

- Set a phase to `Not started`, `In progress`, `Blocked`, `Complete`, or `Deferred by decision`; add a short dated note under its tracking block.
- Check a sub-step only after its code and focused tests are complete.
- Keep each coherent sub-step in its own local commit. Do not combine unrelated phases.
- Every runtime-affecting sub-step runs `dotnet build LogReader.sln` and the narrowest affected tests; complete phases also run `dotnet test LogReader.sln`.
- All work remains local and fully offline. Do not add network, telemetry, cloud, or external-service dependencies.

## Next-phase ordering

1. **Complete (2026-07-16):** Close the two post-rebrand regression gaps in the stabilization prelude below, using separate runtime commits.
2. **Complete (2026-07-16):** Implement Phase 8A's correctness-first live file-generation fix and behavior-neutral snapshot-correlation foundation.
3. **Complete (2026-07-17):** Implement Phase 7A durable dashboard mutations, followed by the smaller Phase 7B and 7C persistence/import slices.
4. Request product input before implementing Phase 8B stale-result navigation behavior.
5. Reconsider Phase 5 index construction and the remaining Phase 6 UI work only after representative measurements meet the correctness-first threshold.
6. Keep indexed disk search, result-memory ceilings, file-backed rows, oversized-line caps, virtualization, and the unscheduled Phase 8C portability items deferred under their existing decision gates.

Phase 8A now precedes Phase 7 because equal-size/larger replacement handling and rotation/append ordering affect ordinary live tail correctness, independent of whether users navigate retained search results. Phase 8A must not introduce a warning or block navigation. Phase 8B remains **Deferred by decision**; its priority and UX depend on how often users navigate retained results after daily rollover.

## Stabilization prelude - Close post-rebrand regression gaps

Status: Complete
Primary goals: Correctness, preserved post-rebrand behavior, resilient file opening

### Tracking

- [x] Regex-timeout pause survives every filter snapshot boundary.
- [x] Optional line-index timestamp evidence cannot make a readable file fail to open or tail.
- [x] Focused tests added for both regression gaps.
- [x] Warning-free solution build and focused tests complete after each runtime commit.
- [x] Full solution test suite passes after both prelude commits.
- [x] Two separate local commits created.
- Notes:
  - 2026-07-15: The tail regex-timeout correction pauses evaluation by clearing the active tail matcher while retaining the visible filter and a paused status. `FilterSnapshot` currently records a missing active matcher as `LastEvaluatedLine = 0`, and restoration recreates the matcher from the retained request. Closing/reopening a tab or restoring a scope can therefore silently resume evaluation from the new end of file while skipping the timed-out and intervening lines.
  - 2026-07-15: Stable line-index timestamps were retained when indexed disk search was removed, but their handle metadata queries remain on the critical build/update path. The timestamp is optional evidence and currently has no runtime consumer; an expected metadata-query failure must yield an unknown/default timestamp rather than fail an otherwise readable log.
  - 2026-07-16: Commit `17032da` added explicit paused-tail state to `FilterSnapshot`, preserved the evaluator position and canonical paused status through clone/recent/scope restoration, captured the latest live all-open-tab snapshot at scope and close boundaries, and kept appended-line reads disabled until explicit reapply. The 26 focused session, recent-tab, all-open-tab, and dashboard scope tests passed, followed by a warning-free solution build.
  - 2026-07-16: Commit `024c19e` made handle timestamp capture instance-injectable and best-effort for `IOException`, `UnauthorizedAccessException`, and `NotSupportedException` only. Unavailable or mismatched evidence clears to `default`; file-open/read, cancellation, disposal, and unexpected failures still propagate. The 60 focused line-index/encoding tests and 17 tab-load/tail tests passed, followed by a warning-free solution build.
  - 2026-07-16: Final prelude validation passed all 231 core tests and all 814 app tests with `dotnet test LogReader.sln --no-build` after the warning-free build.

### Sub-steps

1. Preserve a paused tail filter exactly.
   - [x] Add explicit paused state to `LogFilterSession.FilterSnapshot`; do not infer it from a null matcher or a zero line number.
   - [x] Preserve the state through capture, clone, recent-tab storage, scope storage, and restoration.
   - [x] After restoration, the paused filter must not read or evaluate appended lines until the user reapplies or edits it.
   - [x] Preserve the existing paused status text and retained viewport; explicit reapply must still rebuild state and resume.
   - [x] Add timeout -> capture -> clone -> restore -> append tests, plus close/reopen and scope-restore coverage.

2. Make stable timestamp evidence best-effort.
   - [x] Keep stable timestamp metadata when a handle query succeeds.
   - [x] Convert expected metadata failures to an unknown/default timestamp without suppressing file-open, read, decode, cancellation, or disposal errors.
   - [x] Add an internal failure-injection seam and prove initial construction, no-change polling, append updates, and truncation rebuilds continue when only metadata capture fails.

3. Validate and commit independently.
   - [x] Run the narrow filter/session snapshot tests, then `dotnet build LogReader.sln` for the pause-state commit.
   - [x] Run the narrow line-index/load tests, then `dotnet build LogReader.sln` for the timestamp-evidence commit.
   - [x] After both commits, run the full solution test suite and record the totals.

User-visible difference: a filter that says it is paused remains paused across tab or scope restoration, and a readable file no longer fails solely because optional timestamp metadata is unavailable.

## Phase 2 - Compact filter pipeline and off-dispatcher scanning

Review findings: S01, S02  
Status: Complete
Primary goals: Search speed, UI responsiveness, memory

### Tracking

- [x] Design accepted: filter matching returns compact ordered line numbers rather than display `SearchHit` graphs.
- [x] Tests added/updated.
- [x] Implementation complete.
- [x] Focused validation complete.
- [x] Full solution validation complete.
- [x] Commit created.
- [x] Restore one prepared matcher per multi-file filter operation without changing invalid-regex behavior.
- Notes:
  - 2026-07-11: Added a filter-specific result contract containing ordered unique line numbers, file error state, timestamp metadata, and hit-limit state. `FilterPanelViewModel` consumes only those fields; snapshot search retains line text, first-match offsets, and full match spans unchanged.
  - 2026-07-11: Current-tab and multi-file filter scans now enter bounded background work before opening/reading/matching files. Adaptive per-volume and UNC concurrency gates remain in place, while existing session checks continue to reject cancelled or stale UI results.
  - 2026-07-11: Literal filtering performs one `IndexOf` per line and regex filtering uses `Regex.IsMatch`; the scan directly emits sorted unique line numbers, removing filter-path span graphs and the UI-side distinct/sort pass. No pre-Phase-2 numeric benchmark artifact was available, so the allocation/latency comparison is structural rather than a claimed measured percentage.
  - 2026-07-11: Focused service tests (58) and filter-panel tests (29) passed. Full solution validation passed (213 core tests and 780 app tests); an unrelated dashboard test timed out on the first run, passed immediately in isolation, and the complete no-build rerun passed.
  - 2026-07-14: Commit `3dc5980` prepares one immutable literal or regex matcher per multi-file filter operation and shares it across the existing bounded workers. Matcher preparation remains off-dispatcher. Invalid regex preparation is represented once and returned as the same per-file error rather than failing the operation. The warning-free solution build and all 62 `SearchServiceTests` passed.

### Sub-steps

1. Map the current contracts from `ISearchService` through `SearchService`, `FilterPanelViewModel`, and `LogFilterSession`.
   - [x] Document which consumers require line number, first match, full spans, or retained text.
   - [x] Define an internal filter result type containing sorted unique line numbers and per-file error/timestamp metadata.
   - [x] Confirm snapshot search remains behaviorally unchanged.

2. Implement compact filter matching.
   - [x] Add a filter-specific service method or request/result path.
   - [x] Use literal first-match detection and `Regex.IsMatch` for filter-only evaluation.
   - [x] Preserve timestamp range behavior, include/exclude scope behavior, errors, cancellation, and tail compatibility.
   - [x] Remove filter-path match-span allocation and unnecessary distinct/sort passes.
   - [x] Prepare one immutable matcher per multi-file operation and share it across bounded workers.
   - [x] Fan a preparation failure out as the same per-file error without throwing the whole operation.

3. Move scan/match execution off the WPF dispatcher.
   - [x] Identify every current-tab and multi-file call path that can begin on the UI synchronization context.
   - [x] Run the full read/parse/match loop on bounded background work, preserving existing adaptive parallelism limits.
   - [x] Marshal only UI mutations, progress updates, and final results to the dispatcher.
   - [x] Verify cancellation stops queued work and stale results cannot apply.

4. Validate.
   - [x] Add tests for broad literal and regex filters, timestamp-only filters, cancellation, and errors.
   - [x] Add a dispatcher-affinity regression test proving matching does not execute on the UI thread.
   - [x] Run build, focused tests, full test suite, and local allocation/latency comparison.

Tradeoff to record: the filter path no longer carries every match span because it does not render those spans today.

## Phase 3 - Adaptive indexed sparse search

Review findings: S04, S25  
Status: Deferred by decision
Primary goals: Search speed, UI responsiveness

### Tracking

- [x] Density threshold and fallback behavior documented.
- [x] Tests added/updated.
- [x] Implementation complete.
- [x] Focused validation complete.
- [x] Full solution validation complete.
- [x] Commit created.
- [x] Remove the adaptive indexed runtime path and restore sequential disk snapshot search.
- Notes:
  - 2026-07-11: Disk searches with include-only scopes can now use the open tab's leased `LineIndex`. Inputs are normalized to ordered unique in-range lines and coalesced into contiguous batches of at most 512 lines. The existing adaptive per-volume/global file concurrency remains in effect.
  - 2026-07-11: Indexed reads are limited to scopes at or below 2% density with at most 64 batches. Exclude scopes, denser scopes, highly fragmented scopes, missing indexes, short/stale index reads, and content-version changes fall back to the existing sequential scan. Empty include scopes return an empty result without reading the file. File length and last-write metadata are checked against the leased index before and after indexed reads so a queued index reset cannot hide truncation or replacements with changed metadata. Timestamps are captured from the scanned file handle before and after construction; unstable builds, missing timestamps, and append updates whose existing prefix metadata no longer matches are ineligible for sparse reads. A replacement that deliberately preserves both size and last-write timestamp still depends on the existing content-version detection because timestamp metadata is not a file-identity token.
  - 2026-07-11: A local 20,000-line comparison measured a 1% scope at 1.0 ms indexed versus 4.0 ms sequential. The 9.9% and 25% cases selected sequential fallback; measured adaptive-wrapper times were 3.6 ms versus 3.8 ms and 7.3 ms versus 5.3 ms respectively. The cutoff intentionally stays well below the observed crossover, and the fragmented-scope cap avoids excessive indexed segment reads.
  - 2026-07-11: Repeat-query literal candidate caching was not implemented. The 1% indexed-scope measurement above serves as the candidate-read performance proxy, while a 100,000-line `List<int>` candidate set allocated 400,064 bytes before dictionary, query, and version metadata. Display search is capped, so retained hits are not a complete reusable candidate set; making them complete would require an additional globally budgeted cache that conflicts with the still-pending Phase 4 memory work. Regex searches remain uncached.
  - 2026-07-11: Focused search/index tests (89), search-panel tests (70), and the physical index-snapshot regression test passed. The full build passed with no warnings; all 224 core tests passed. The app suite continued to show unrelated dashboard/lifecycle timing flakes: the failing lifecycle test passed in isolation, while the other 780 app tests passed together.
  - 2026-07-12: The dashboard/lifecycle timing flakes were traced to overlapping fire-and-forget member refreshes, test view models surviving beyond their tests, mutable tail-service state being read without synchronization, and non-WPF tests resolving through ambient WPF dispatcher state. Member refreshes are now serialized and awaited at dashboard/tab batch and direct-open boundaries; metadata-triggered refreshes use the same queue. Main/search view-model fixtures dispose every created view model, tail-service assertions read locked snapshots, and the shared test factory uses an immediate test dispatcher. Refresh drains retain their prior background-error semantics so they cannot mask primary operations, while dashboard cleanup is guaranteed if suppression teardown fails. A dedicated WPF regression test verifies queued member mutations remain on the dispatcher. Five repeated `MainViewModelTests` runs, three initial repeated full app-suite runs, and two post-review full app-suite runs passed. The final warning-free solution build and test run passed 224 core plus 789 app tests. Implementation commits: `6aeae00`, `61b3308`, `a5ce396`, and `05f9ea3`.
  - 2026-07-11: An independent regression review found a stale-index acceptance race and unnecessary index leases for ineligible targets. File snapshot validation now occurs while the lease is held, and per-file eligibility is checked before acquiring a lease.
  - 2026-07-11: Implementation commits: `a7575ff` (`Add adaptive indexed sparse search`), `099131d` (`Harden indexed search snapshot fallback`), `5fd4b5f` (`Tie index metadata to scanned file handle`), and `feb7300` (`Reject unstable indexes for sparse search`).
  - 2026-07-14: The 2026-07-11 indexed-search implementation notes above are retained as experiment history, not current runtime behavior. Commit `49cf229` removed both indexed-search members from `ISearchService`, the adaptive planner/batching/callback path, and runtime eligibility wiring. Disk snapshot searches again use the ordinary sequential `SearchFilesAsync` path. Line-index leases and range reads remain for viewport and tail processing, and stable index timestamp metadata remains as optional correctness evidence. The warning-free solution build, 82 focused core search/index tests, and 16 focused app tests passed.
  - 2026-07-14: Indexed disk search is **Deferred by decision** until it can read through one stable file handle or file identity, fall back to sequential search after indexed I/O errors, and demonstrate a material win on a realistic workload under the correctness-first threshold.
  - 2026-07-15: The recorded 1% case saved roughly 3 ms, which does not meet the 50 ms absolute action threshold even though the isolated ratio was favorable. Do not reopen this phase based on microbenchmark ratio alone; require realistic action-level evidence in addition to the stable-handle and fallback requirements. Timestamp evidence is retained only on a best-effort basis under the stabilization prelude.

### Sub-steps

1. Define the adaptive strategy.
   - [x] Measure sequential scan versus `LineIndex` reads for sparse, medium, and dense include scopes.
   - [x] Select a deterministic density/fragmentation threshold.
   - [x] Keep sequential scanning for exclude scopes and cases with no usable line index.

2. Implement indexed include-only search.
   - [x] Coalesce adjacent allowed lines into bounded contiguous batches.
   - [x] Read batches through existing off-UI line-index access.
   - [x] Preserve line ordering, match limits, timestamp filters, cancellation, and file-error handling.
   - [x] Fall back safely when the index/session is unavailable or changes during the operation.

3. Evaluate repeat-query candidate reuse separately.
   - [x] Benchmark content-versioned literal candidate reuse against its memory cost.
   - [x] Implement only if it improves measured workloads within a documented global budget.
   - [x] Keep regex searches outside any literal-candidate cache.

4. Validate.
   - [x] Add equivalence tests comparing indexed and sequential results.
   - [x] Test sparse, dense, unsorted, empty, and stale scopes.
   - [x] Run build, focused tests, full test suite, and density benchmark comparison.

5. Correctness-first stabilization.
   - [x] Restore sequential disk snapshot search for include and exclude scopes.
   - [x] Remove the indexed-search public contract and unused runtime-only wrappers.
   - [x] Retain line-index functionality required by viewport and tail behavior.
   - [ ] Reconsider indexed disk search only after stable-handle/identity, I/O fallback, and material-win requirements are met. **Deferred by decision.**

Tradeoff to record: sparse filtered searches may take longer on the sequential path, but a search cannot combine indexed reads from different file generations.

## Phase 4 - Bound search and highlighting memory

Review findings: S06, S18, S20  
Status: Deferred by decision
Primary goals: Correctness, predictable interaction, bounded memory

### Tracking

- [x] Result-retention decision boundaries documented.
- [x] Tests added/updated.
- [x] Correctness-first stabilization implementation complete.
- [x] Focused validation complete.
- [x] Full solution validation complete.
- [x] Commit created.
- [x] Restore indefinite per-scope result retention and remove the inactive eviction policy.
- [x] Consume the activated scope snapshot so active UI state does not retain a hidden duplicate.
- [x] Record representative retained-memory measurements.
- [ ] Add a reproducible opt-in Release diagnostic harness before revisiting clone allocation. **Deferred by decision.**
- [ ] Implement a configurable result-memory ceiling. **Deferred by decision.**
- Notes:
  - 2026-07-14: Highlight regex caching was already capped at 128 valid or invalid patterns by commit `603ca2c`; capacity eviction recompiles a reused pattern safely.
  - 2026-07-14: Commit `51f367f` introduced a 20,000-hit/16 MiB inactive-scope LRU and shared captured result data by convention. That experiment is superseded by the correctness-first stabilization below; its earlier budget measurements are retained only as history.
  - 2026-07-14: The initial result-row experiment used one access-order cache per search panel, capped at 256 rows across all files. A release-mode local probe performed two complete passes over 10,000 result rows; the passes allocated 7,793,960 and 7,760,000 managed bytes while retained row presentation objects stayed at 256. These measurements describe the reverted experiment, not the current implementation.
  - 2026-07-14: Regression review found that recreating rows changed WPF item identity. In a 400-hit reproduction, row 350 remained selected while Copy Selected Lines returned no text because list enumeration recreated a different row object. Commit `9b66c7f` reverted the row-cache experiment, restoring stable row behavior, and commit `ca8c601` added focused selection/copy coverage beyond the former cache window.
  - 2026-07-14: Commit `dca77fc` semantically undid the inactive retention portion of `51f367f`: it removed automatic count/byte eviction, eviction status, retention LRU, and mutable-data sharing by convention; restored defensive result-state cloning and indefinite per-scope restoration; and made activation clone and then consume the stored snapshot. A failed clone leaves the stored snapshot available. Existing stale-context checks, the 10,000-hit-per-file cap, the 8,192-character retained-text cap, stable row identity, selection, and snapshot Copy behavior remain unchanged. The warning-free solution build and 20 focused state/search/copy tests passed.
  - 2026-07-14: Commit `9c9aa1a` extends the stable-row interaction regression across two capped-size result groups: it selects flat row 11,000 from 12,000 visible hits and verifies snapshot Copy returns the selected retained text. All 18 `SearchWorkspaceViewTests` passed.
  - 2026-07-14: A deterministic Release-mode synthetic probe used eight 12,000-line UTF-8 files with roughly 190 characters per line. Snapshot search retained 80,000 capped hits, broad filtering retained 96,000 line numbers, and four independently populated scopes were switched 40 times. Forced-GC managed live data was 47.7 MiB for raw search results, 69.4 MiB for active stable result models, and 91.9 MiB after traversing all 80,000 result rows. Four retained scopes used 191.5 MiB, stayed at 191.5 MiB after all switches, fell to 138.7 MiB after clearing the active scope while three inactive scopes intentionally remained, and fell to 1.4 MiB after disposal. Two runs reproduced each managed figure within 0.1 MiB.
  - 2026-07-14: The same 40 switches allocated 4,070.1 MiB cumulatively through defensive capture/restore cloning without increasing retained memory. This is a future behavior-transparent optimization candidate if interaction timings meet the material threshold; it is not evidence of a leak and does not justify weakening ownership today. Process private bytes were not used as retained-state evidence because the GC keeps reserved segments after collection.
  - 2026-07-14: Do not implement file-backed result rows, line-number Copy lookups, another row LRU, a runtime result ceiling, or a setting in this pass. A future ceiling remains **Deferred by decision** and must preserve active results, report omitted or evicted data, and bound acquisition peak rather than trimming only after allocation.
  - 2026-07-15: The one-off memory probe is useful calibration but is not a maintained benchmark harness. Before optimizing the 4,070.1 MiB cumulative clone traffic, add an opt-in Release diagnostic that reports retained managed bytes, cumulative allocations, and p50/p95 scope-switch time without CI pass/fail assertions on exact byte counts. Proceed only if the candidate prevents unbounded growth, saves roughly 100 MiB, or improves scope switching by at least 50 ms and 2x. Prefer immutable search-specific snapshots if justified; do not weaken the generic `WorkspaceScopedStateStore` defensive-clone contract as a shortcut.

### Sub-steps

1. Preserve stable workspace results.
   - [x] Remove automatic inactive-scope count/byte eviction and its user-visible rerun status.
   - [x] Restore defensive cloning at result-state ownership boundaries.
   - [x] Consume the stored snapshot after successful activation and recapture only when leaving or disposing.
   - [x] Restore results indefinitely across repeated scope switches, subject to existing stale-context rules.
   - [x] Preserve the 10,000-hit-per-file and 8,192-character retained-text limits.

2. Bound result-row and highlight caches.
   - [x] Bound highlight regex caching and invalidate or evict invalid entries.
   - [x] Preserve stable result-row identity for selection, scrolling, navigation commands, and Copy.
   - [ ] Replace result rows with a different representation. **Deferred by decision; no row LRU or file-backed lookup design is approved.**

3. Validate.
   - [x] Restore scopes above the former hit and byte limits and repeat scope switches.
   - [x] Verify activated snapshots are consumed and the latest state is recaptured.
   - [x] Preserve selection and Copy beyond 400 and 10,000 rows.
   - [x] Exercise multi-file search, broad filtering, full result traversal, repeated scope switches, Clear, and disposal while recording managed live data and allocations.
   - [x] Run build, focused tests, full test suite, and memory comparison.

4. Gate any future clone-allocation work.
   - [ ] Add the opt-in Release diagnostic harness and record its fixture definition. **Deferred by decision.**
   - [ ] Measure retained bytes, cumulative allocations, and p50/p95 scope-switch latency separately.
   - [ ] Consider immutable internal search snapshots only if the material threshold is met and ownership remains explicit.
   - [ ] Preserve stable row identity, retained Copy text, active results, and generic scoped-state cloning in every candidate design.

Tradeoff to record: inactive scope results can grow with the number and size of user-created scope states, within the existing per-file acquisition/text caps. The active scope no longer has a hidden stored duplicate, repeated switching does not grow retained memory, and disposal releases the managed graph; defensive cloning currently trades substantial transient allocation for clear ownership and predictable behavior.

## Phase 5 - Harden initial indexing and oversized-line behavior

Review findings: S07, S09  
Status: Deferred by decision
Primary goals: Correct indexing, bounded peak memory, UI responsiveness

### Tracking

- [ ] Oversized/binary-line policy documented.
- [ ] Tests added/updated.
- [ ] Implementation complete.
- [ ] Focused validation complete.
- [ ] Full solution validation complete.
- [ ] Commit created.
- [ ] Representative index-build measurement meets the material optimization threshold before structural redesign.
- [ ] Product approval recorded for any match-omitting, truncating, or capped oversized-line behavior. **Behavior gate.**
- Notes:
  - 2026-07-14: Oversized-line caps are an explicit product behavior gate. They can omit matches, change copied text, or make viewport content differ from the file. Do not implement a cap until the user-visible status, search/Copy semantics, and interaction tests are approved; this stabilization pass makes no oversized-line behavior change.
  - 2026-07-15: Do not assume that replacing the in-memory `MappedLineOffsets` build list is worthwhile. Measure a representative high-line-count index build first and proceed only if the change prevents unbounded growth or saves roughly 100 MiB of peak managed memory. A behavior-transparent cleanup that reliably removes failed/cancelled temporary artifacts may proceed independently if it remains local.

### Sub-steps

1. Establish whether index construction crosses the material threshold.
   - [ ] Define a representative high-line-count fixture and an opt-in Release measurement command.
   - [ ] Record peak live managed memory, cumulative allocations, elapsed build time, mapped-offset size, and cancellation cleanup behavior.
   - [ ] Stop after measurement if the candidate cannot prevent unbounded growth or save roughly 100 MiB on the defined workload.

2. Stream index offsets during construction only if justified.
   - [ ] Design a chunked or spill-to-temp-file builder for `MappedLineOffsets` without changing offset semantics.
   - [ ] Preserve BOM/newline behavior and cancellation cleanup.
   - [ ] Ensure failed/cancelled builds remove temporary artifacts.
   - [ ] Retain only bounded append overflow after freezing.

3. Define oversized-line handling.
   - [ ] Obtain product approval for any cap, rejection, truncation, or match-omission behavior.
   - [ ] If approved, add a configurable maximum decoded-line size with the approved user-visible status and semantics.
   - [ ] If approved, implement chunked literal search where feasible.
   - [ ] If approved, implement the chosen regex behavior for oversized lines rather than allocating file-sized strings.
   - [ ] If approved, implement the chosen viewport behavior for lines that cannot be materialized in full.

4. Validate.
   - [ ] Add very-large line, binary-like/no-newline, UTF-8, UTF-16, CR/LF/CRLF, cancellation, and cleanup tests.
   - [ ] Add interaction tests covering displayed content, result counts, navigation, selection, and Copy before enabling a cap.
   - [ ] Re-run the high-line-count peak-memory measurement only if the streamed builder was justified and implemented.
   - [ ] Run build, focused tests, full test suite, and memory comparison.

Tradeoff to record: capped or skipped oversized lines can omit matches and alter visible/copied content; reporting alone is insufficient without explicit product approval and interaction coverage.

## Phase 6 - Scale dashboard and ad hoc UI operations

Review findings: S05, S17, S19, S21  
Status: Deferred by decision
Primary goals: Predictable UI responsiveness, bounded background work

### Tracking

- [x] Background member-refresh interaction behavior documented.
- [x] Member-refresh scheduler and generation tests added/updated.
- [x] Bounded member-refresh scheduler implementation complete.
- [x] Member-refresh focused validation complete.
- [x] Full solution validation complete.
- [x] Member-refresh stabilization commit created.
- [x] Dashboard-load progress publication throttled and tested.
- [ ] Representative measurement justifies remaining targeting or tree-filter work. **Deferred by decision.**
- [ ] Product approval recorded before changing nested-list scrolling or virtualization behavior. **Behavior gate.**
- Notes:
  - 2026-07-14: Commit `549ba6f` replaced the unbounded task/closure chain with an internal scheduler that permits at most one running and one pending batch. Targeted pending requests merge by file ID with latest-path wins, a pending full refresh subsumes targeted work, each batch exposes one shared completion task, and work starts immediately without debounce.
  - 2026-07-14: Suppression teardown waits only for the batch containing its own request. Direct file opening no longer waits for a global refresh drain, so a tab becomes usable while unrelated metadata probes converge asynchronously. Foreground dashboard refresh commands remain outside the background notification scheduler and can run concurrently with a slow probe.
  - 2026-07-14: Dashboard activation now registers monotonic full and per-file targeted commit generations. Stale same-ID targeted work and older full work are discarded; disjoint targeted IDs remain independent; a current ordinary full refresh preserves member objects superseded by newer targeted work; and active modifiers promote targeted requests to full generations. Cancellation is checked after probes and immediately before UI mutations. Shutdown rejects new work and cancels both running and pending batches.
  - 2026-07-14: The warning-free solution build and 63 focused scheduler, generation, suppression, immediate-open, dispatcher, and targeted-member tests passed. The immediate-open race blocks an unrelated full metadata refresh, proves `OpenFilePathAsync` still completes, then verifies member metadata converges after release.
  - 2026-07-14: Final boundedness review found that generation metadata could retain an unbounded sequence of ad-hoc file IDs even though those IDs never affect dashboard members. Commit `9492031` filters scheduler and activation requests to current dashboard membership, skips probes for untracked IDs, and prunes no-longer-tracked generation keys. The warning-free build and 80 focused dashboard/scheduler/coordinator/open tests passed.
  - 2026-07-14: Virtualization remains an explicit product behavior gate. Recycling or bounding nested dashboard lists can change scrolling, keyboard focus, selection, drag/drop, and context-menu behavior; no virtualization change is part of the current stabilization pass.
  - 2026-07-15: Commit `6928141` already throttled dashboard-load progress publication by elapsed time or meaningful count increments and added focused coverage; the unchecked progress item below was stale and is now corrected.
  - 2026-07-15: The bounded scheduler removes the unbounded-work correctness risk. Remaining targeted refresh and tree-filter work is performance-only and is **Deferred by decision** until a representative dashboard/ad hoc workload demonstrates at least a 50 ms and 2x user-action improvement. Do not change member object identity, selection, expansion, drag/drop, focus, or refresh timing merely to finish this phase.

### Sub-steps

1. Bound background member-refresh work and commits.
   - [x] Allow at most one running and one mergeable pending batch, starting immediately without debounce.
   - [x] Merge targeted IDs with latest-path wins and let a pending full refresh subsume targeted work.
   - [x] Share one completion task per batch and keep suppression teardown waiting for its exact batch.
   - [x] Remove the global refresh drain from direct file opening while retaining asynchronous metadata convergence.
   - [x] Reject new work and cancel pending/running work on shutdown; check cancellation after probes and before UI mutation.
   - [x] Keep foreground dashboard refreshes independent from the background scheduler.
   - [x] Guard full and targeted UI commits with monotonic generations, including modifier promotion and disjoint-ID races.

2. Measure and, only if justified, make remaining member refresh operations targeted.
   - [ ] Measure add, remove, copy, reorder, and cross-dashboard move with representative dashboard sizes and slow-path probes.
   - [ ] Stop after measurement if full refresh does not cross the material action threshold. **Deferred by decision.**
   - [ ] Identify the exact groups/files affected by add, remove, copy, reorder, and cross-dashboard move.
   - [ ] Reuse existing targeted refresh paths or add narrow equivalents.
   - [ ] Update reorder/move presentation locally without changing existing member identity, selection, modifier, or expansion behavior.
   - [ ] Reserve full refresh for import, recovery, and global display-setting changes.

3. Fix virtualization only after product approval.
   - [ ] Obtain approval for scrolling, focus, selection, and nested-list behavior changes.
   - [ ] Replace ad hoc `ItemsControl` with a recycling virtualized list.
   - [ ] Verify keyboard navigation, selection, drag/drop, context menu, and styling.
   - [ ] Decide whether nested dashboard lists receive bounded viewports or are flattened into a single virtualized row model.
   - [ ] Implement the selected dashboard-list strategy in a separate coherent commit if structural.

4. Reduce dispatcher work.
   - [x] Throttle dashboard-load progress updates by elapsed time or meaningful count increments.
   - [ ] Measure tree-filter dispatcher time on a representative hierarchy before changing its threading model.
   - [ ] Compute tree-filter matches/expansion from a snapshot off-thread.
   - [ ] Apply one batched UI mutation guarded by a generation token.

5. Validate.
   - [x] Add scheduler tests for 10,000 notifications, ID/path merging, full dominance, failure recovery, and shutdown cancellation.
   - [x] Add reversed-completion generation tests for full/targeted ordering, same and disjoint IDs, and modifiers.
   - [x] Prove direct opening completes while an unrelated metadata probe is blocked and suppression still waits for its own batch.
   - [x] Add dashboard-load progress-throttling tests.
   - [ ] Add remaining tests for selection/modifier state, targeted operations, and stale tree-filter results only if the measured work proceeds.
   - [ ] If measured work proceeds, manually exercise the affected large dashboard/ad hoc workloads.
   - [ ] If runtime work proceeds, run focused tests and `dotnet build LogReader.sln` after each commit, then the full test suite and relevant responsiveness comparison at slice completion.

Tradeoff to record: metadata may visibly converge just after a newly opened tab becomes usable; generation guards make that convergence predictable. A bounded nested list changes scrolling behavior, while flattening requires more presentation-state management, so either virtualization design requires explicit approval and interaction tests.

## Phase 7 - Make persistence and imports transactional

Review findings: S08, S15, S16, S22  
Status: Complete
Primary goals: Correctness, durable user intent, predictable failure behavior

### Tracking

- [x] Failure model documented.
- [x] One application-level dashboard coordinator, or an equivalent repository transaction plus UI commit-generation guard, selected.
- [x] Phase 7A coordinated dashboard mutations persist once and commit presentation only after success.
- [x] Phase 7B single-dashboard mutations are persistence-first.
- [x] Phase 7C catalog/import side effects are recoverable and invalid input fails before side effects.
- [x] Fault-injection, concurrency, and success-equivalence tests added/updated.
- [x] Focused validation complete.
- [x] Full solution validation complete.
- [x] Separate coherent commits created for 7A, 7B, and 7C.
- Notes:
  - 2026-07-15: The required persistence primitives already exist. `ILogGroupRepository.ReplaceAllAsync` validates and replaces a complete group snapshot, `JsonLogGroupRepository.ReplaceAllAsync` uses it, and `JsonStore` writes a temporary file before replacing the destination. Phase 7 must reuse and harden those foundations rather than introduce another batch contract by default.
  - 2026-07-15: Current cross-dashboard file moves mutate both live models and then issue two independent group updates. Group move/up/down operations similarly persist multiple siblings separately, and several single-dashboard operations mutate live `FileIds` before their save completes. Failures can therefore leave the UI and persisted state split or persist a removal without the matching addition.
  - 2026-07-15: A naive `GetAllAsync` followed later by `ReplaceAllAsync` is atomic only at the final file write, not across the read-modify-write interval. It can lose a concurrent mutation. Phase 7A must serialize participating dashboard mutations through an application-level coordinator or move planning inside a true repository transaction; the latter still needs a UI commit-generation guard, and its store lock must be released before awaiting dispatcher work.
  - 2026-07-15: Normal successful behavior is the compatibility contract. Preserve current ordering, full/targeted refresh decisions, notifications, expansion, selection, modifiers, and view-model identity wherever the baseline currently preserves them. On persistence failure, the intended visible behavior is no structural change plus the existing friendly error path.
  - 2026-07-17: Commit `3282dc1` added one application-level dashboard mutation coordinator. Cross-dashboard membership moves and group move/reorder commands now write one validated group snapshot and hold the coordinator through the matching presentation commit; metadata refresh and file probes remain outside the coordinator.
  - 2026-07-17: Commit `6041453` made add, remove, copy, same-dashboard reorder, delete scope exit, inline rename persistence, and recovery-time file-ID repair persistence-first. Existing group/member view-model objects are retained where they were retained before. Adding a file also now preserves pre-existing membership IDs whose catalog rows are temporarily unavailable instead of silently dropping them.
  - 2026-07-17: Follow-up commit `0a399dc` keeps inline rename's matching view-model name commit inside the shared mutation boundary and covers an overlapping rename/group-move completion order.
  - 2026-07-17: Commit `78e18ac` made batch catalog registration report the exact entries it created, added one-write conditional cleanup, and covered import, add-by-path, copy-by-path, and recovery rollback. Cleanup rechecks committed groups, open tabs, and concurrent open registration before deleting only entries created by the failed operation. Cleanup or stored-view rollback failures are surfaced with both error contexts rather than silently ignored; follow-up commit `36aeab1` proves the combined-error cleanup boundary.
  - 2026-07-17: Stored-view promotion now keeps a uniquely named prior copy while the pending import is promoted. A failed dashboard replacement restores the prior stored view and the retryable `.importing` file; successful replacement removes the backup best-effort. Null group elements/collections, undefined group kinds, missing IDs/names, cycles, duplicate membership, and syntactically invalid paths fail validation before catalog or group writes.
  - 2026-07-17: Successful command ordering, selection, expansion, notifications, refresh timing after commit, and import presentation remain unchanged. User-visible differences are failure-only: coordinated mutations no longer partially appear or persist, invalid imports fail earlier with controlled errors, and failed path-based operations no longer leave unused catalog entries. The warning-free solution build and full no-build test run passed all 256 core tests and all 875 app tests.

### Sub-steps

1. Establish one dashboard mutation boundary.
   - [x] Choose either one shared application-level mutation coordinator spanning planning, persistence, and the matching UI commit, or a true repository read-modify-write operation paired with an equivalent UI commit-generation guard. Document how the choice covers inline rename, tree, membership, import, and recovery writers that can overlap.
   - [x] Plan mutations on deep-cloned `LogGroup` models. Never pass a live mutable view-model model graph into an asynchronous save.
   - [x] Validate the complete planned snapshot before writing it once with the existing replacement path.
   - [x] Apply the planned state to live view models only after persistence succeeds; do not use mutate-first plus rollback as the normal design.
   - [x] Hold the application-level coordinator through the matching dispatcher/live structural commit, or enforce the equivalent commit generation, so an older persisted operation cannot apply its UI state after a newer operation.
   - [x] Never hold the repository's file/store lock while awaiting dispatcher work.
   - [x] Release the structural mutation boundary before slow file-existence probes or metadata convergence so an unrelated UNC probe cannot stall later persistence commands.

2. Phase 7A - Make coordinated dashboard mutations durable.
   - [x] Replace the two-write cross-dashboard file move with one validated group snapshot replacement.
   - [x] Replace multi-write group move, move-up, and move-down persistence with one validated snapshot replacement.
   - [x] Preserve current successful ordering, tree rebuild/expansion behavior, member selection, modifier state, notifications, and refresh behavior.
   - [x] For membership moves, retain existing group/member view-model identity and apply the committed `FileIds` only after success.
   - [x] Add failure tests proving persisted groups and live models remain unchanged when replacement fails.
   - [x] Add overlapping/reversed command tests proving the serialization boundary cannot lose a newer mutation.

3. Phase 7B - Make remaining group mutations persistence-first.
   - [x] Build pending clones for add, remove, copy, and same-dashboard file reorder; save before mutating live `FileIds` or raising structural notifications.
   - [x] Apply the same rule to recovery-time dashboard ID repair and any other writer found by an exhaustive repository-call audit.
   - [x] Preserve inline rename's existing persistence-first editor behavior as the reference contract.
   - [x] Keep normal refresh timing and object identity unchanged unless a separate measured Phase 6 decision approves a change.

4. Phase 7C - Make catalog/import side effects recoverable.
   - [x] Make batch catalog registration report which entries were created by the current operation.
   - [x] On a failed group save, remove only entries created by that operation that remain unreferenced; re-check references so rollback cannot delete an entry concurrently adopted by an open tab or another committed group.
   - [x] Cover imports, add-by-path, copy-by-path, and recovery repair rather than fixing import-only orphan growth.
   - [x] Use one batch cleanup write; if deterministic rollback cannot be guaranteed, use a small local recovery journal instead of silent best-effort deletion.
   - [x] Define the stored-view promotion state machine so a failed dashboard replacement cannot silently overwrite the prior stored copy or strand an unrecoverable pending file.
   - [x] Reject null group elements, null membership collections, invalid/undefined enum values, missing IDs/names, cycles, duplicate membership, and invalid paths as controlled `InvalidDataException` failures before catalog or group writes.
   - [x] Defer arbitrary collection/string size limits and unrelated settings/font/color policy until explicit compatibility limits are selected; do not mix those product decisions into transactional correctness.

5. Validate.
   - [x] Inject failure before the group write, during snapshot replacement, during catalog cleanup, and during stored-view promotion; verify the documented recoverable state after each boundary.
   - [x] Verify one group-snapshot persistence operation for each coordinated mutation and exact success equivalence for ordering, selection, expansion, member objects, modifiers, notifications, and refresh/probe counts.
   - [x] Verify a failed attempt can be retried successfully without duplicate membership, lost ordering, or orphaned catalog growth.
   - [x] Add malformed/null-element/undefined-enum tests; add size-limit tests only if a later explicit limit is approved.
   - [x] Run the narrow repository/dashboard/import tests and `dotnet build LogReader.sln` after each runtime commit; run `dotnet test LogReader.sln` when Phase 7 completes.

Tradeoff to record: dashboard mutations become deliberately serialized at their commit boundary, so their persistence work cannot partially interleave or overwrite a newer committed state. Strict FIFO fairness is not a product contract unless deliberately implemented. Normal successful presentation remains unchanged, while failures become all-or-nothing from the user's perspective. Cross-file import/catalog recovery may require a small journal because two JSON stores cannot be replaced atomically by one filesystem operation.

## Phase 8A - Establish live file-generation correctness

Review findings: S23, S24, S26, S27, S28  
Status: Complete
Primary goals: Correct live tailing, single-generation snapshot results, behavior-neutral staleness evidence

### Tracking

- [x] Live index replacement/rotation contract documented and tested.
- [x] Rotation and append commits share one ordering/generation boundary.
- [x] Search generation evidence comes from the same handle used for each scan.
- [x] Behavior-neutral per-file result-generation correlation implemented.
- [x] Rotation-during-search, close/reopen, and partial multi-file staleness tests added.
- [x] Focused validation complete.
- [x] Full solution validation complete.
- [x] Separate coherent commits created for live indexing and result correlation.
- Notes:
  - 2026-07-15: `UpdateIndexAsync` returns the existing index when file size is unchanged without proving that the path still names the same file. A larger replacement can be processed as an append if append and rotation notifications race. `OnFileRotated` and `OnLinesAppended` currently do not share one ordering boundary. This affects ordinary live tail correctness and therefore precedes Phase 7; it is not conditional on the later stale-navigation UX.
  - 2026-07-15: `SearchContentVersion` is useful but insufficient as a file-generation identity by itself. It is session-local, can restart after tab close/reopen, and ordinary `FileSearchResultState` does not carry it. The monitorable-result path currently samples the tab version after a disk search, so a rotation during a blocked scan can associate old-generation results with a newer version.
  - 2026-07-15: Pre/post tab-version sampling cannot prove which file generation a disk scan read. Generation evidence must be captured from the same handle used by the scan, then correlated with the current path/session generation before results commit. Detected instability requires a bounded retry and then a per-file error. A stable scan whose filesystem cannot supply durable identity remains a valid retained snapshot but is classified internally as unknown; a stable result superseded before or after commit is classified internally stale.
  - 2026-07-15: Same-handle evidence and the existing rotation/replacement signals cannot prove the absence of every concurrent in-place rewrite without stronger locking or full-content hashing. Phase 8A guarantees deterministic handling for detectable rollover, replacement, truncation, and generation changes; document and test the remaining identity limits instead of claiming mathematical snapshot isolation.
  - 2026-07-16: Commit `7f18d5e` made live indexing generation-safe. Index build/update/read paths use same-handle generation evidence when available, append publication is transactional, detectable equal-size/larger replacements rebuild instead of extending prior offsets, and rotation/append work shares an ordered generation boundary while preserving normal append, viewport, and tail-registration catch-up behavior.
  - 2026-07-16: Commit `fe780b6` added the behavior-neutral snapshot-correlation foundation. Sequential per-file search and filter scans retain generation evidence from the handle actually read, make at most two scan attempts when instability is detected, and return a per-file error after repeated instability. Timestamp-only drift and unavailable durable identity remain readable with `Unknown` correlation rather than becoming failures.
  - 2026-07-16: Result evidence now survives defensive clones, active and inactive scope state, restoration, and monitored-tail additions. Later content/version/encoding changes mark only the affected retained file internally stale; Clear, scope replacement, and disposal detach its tracking. Copy and navigation still use the captured result text, path, and line number, and Phase 8A adds no stale warning or navigation block.
  - 2026-07-16: Filter commits now carry exact evaluated boundaries and generation/encoding evidence, reject short or incompatible catch-up, and publish current/all-open state transactionally after final active-snapshot revalidation. Filter invalidation, Clear, replacement, and scope changes cannot republish or restore a superseded filter. Search-within-filter rejects a per-file result instead of applying line numbers to detectably different content.
  - 2026-07-16: Tail range search advances only through the lines actually returned; a short read leaves the unread suffix pending instead of skipping it. Failed or incomplete disk result sets do not offer monitoring, and the first monitored hit after a zero-hit snapshot receives the same generation tracking as an ordinary result.
  - 2026-07-16: Final validation completed with a warning-free `dotnet build LogReader.sln`; `dotnet test LogReader.sln --no-build` passed all 251 core tests and all 855 app tests. Focused range, filter transaction, rollover, restoration, monitoring, selection/Copy, Clear, and disposal tests also passed. No push or pull request was performed.
  - 2026-07-16: The remaining evidence limit is deliberate: when durable identity is unavailable, or the same file identity is rewritten in place without a provable truncation/generation transition, the app can classify the snapshot only as unknown. Phase 8B remains **Deferred by decision** and is still responsible for any warning-versus-blocking navigation UX; this phase does not add file-backed rows, line-number Copy lookups, a result ceiling, or another row LRU.

### Sub-steps

1. Make live index updates generation-safe.
   - [x] Define the identity/generation evidence available from the open handle, tail rotation probe, line-index reset, tab instance, and content-version counter; document what each signal can and cannot prove.
   - [x] Serialize rotation reset/reload and append index updates through one ordering boundary, or add a generation guard that makes stale append work unable to commit after a replacement.
   - [x] Prevent equal-size replacement from reusing old offsets and prevent larger replacement from extending an index built for a prior generation.
   - [x] Preserve the normal append fast path, existing viewport behavior, and tail-registration catch-up.
   - [x] Treat best-effort timestamps as supporting evidence only, never as the sole file identity or a reason to fail a readable file.

2. Correlate each snapshot result with the handle actually scanned.
   - [x] Capture an internal file-generation token from the same handle used for sequential search; do not reopen the path merely to manufacture identity evidence.
   - [x] Validate that token against the current path/session generation before UI commit. If detectable evidence says the scan itself crossed generations, retry it with a defined bound and then return a per-file error.
   - [x] If the scan remained stable but durable identity is unavailable, retain the captured snapshot with internal unknown-generation state; if the stable scan was superseded before commit, retain it with internal stale state.
   - [x] Never stamp completed results by sampling only after the scan, and never knowingly combine rows from detectably different generations into one per-file result.
   - [x] Carry per-file generation evidence through ordinary result state, defensive clones, inactive scope storage, and restoration without changing retained text or row identity.
   - [x] After a valid result commits, let a later generation change mark only that file's retained result internally stale.
   - [x] Treat a closed/reopened tab or unavailable durable identity conservatively as unknown; use stale only when available evidence shows that the captured generation was superseded. A `TabInstanceId` plus `SearchContentVersion` may supplement in-session evidence but is not a durable filesystem identity.
   - [x] Classify staleness per file so one rotated file does not invalidate unrelated files in the same multi-file result set.
   - [x] Expose and test internal stale classification without adding a warning or changing navigation behavior in Phase 8A.

3. Validate.
   - [x] Add same-size and larger replacement, rotation/append reversed-order, rotation-during-search, rotation-after-search, append-only, unstable-handle, and metadata-unavailable tests.
   - [x] Cover active and restored inactive scopes, per-file staleness in multi-file results, close/reopen, Clear/disposal, and retained Copy text.
   - [x] Verify no stale-classification subscription retains disposed tabs or sessions.
   - [x] Run focused tests and `dotnet build LogReader.sln` after each runtime commit.
   - [x] Run `dotnet test LogReader.sln` when Phase 8A completes.

User-visible contract: ordinary append/tail behavior remains unchanged, while detectable replacement reliably rebuilds rather than extending stale offsets. Detected scan instability retries within a defined bound and then reports a per-file problem; unavailable durable identity alone does not reject a readable file or hide its stable captured snapshot. Phase 8A does not warn about or block navigation of retained results.

## Phase 8B - Decide rollover-aware result navigation

Status: Deferred by decision
Primary goals: Predictable navigation, preserved snapshot Copy behavior

### Tracking

- [ ] Frequency and expected workflow for post-rollover result navigation confirmed with product input.
- [ ] Warning, confirmation, or blocking behavior selected. **Behavior gate.**
- [ ] Interaction and accessibility tests approved before implementation.
- Notes:
  - 2026-07-14: Search results retain captured snapshot text. Copy therefore remains internally consistent after the underlying file changes and does not re-read a line that may now contain different content.
  - 2026-07-14: Result navigation still uses the captured path and line number. After a daily rollover, replacement, or truncation, that coordinate can refer to unrelated content. This is a current predictability defect, not a reason to make Copy file-backed.
  - 2026-07-15: Phase 8A supplies trustworthy per-file staleness evidence but deliberately does not choose the navigation UX. Do not implement file-backed rows or line-number Copy lookups as part of this decision.

### Sub-steps

1. Choose the stale-navigation contract with product input.
   - [ ] Decide whether stale navigation warns and proceeds, requires confirmation, or is blocked when the current file generation differs.
   - [ ] Preserve captured result text and Copy output regardless of the navigation decision.
   - [ ] Define status, accessibility, keyboard, repeated-navigation, and multi-file partial-staleness behavior.

2. Validate before implementation.
   - [ ] Add daily-rollover/replacement interaction tests before broader manual-edit heuristics.
   - [ ] Cover stale and non-stale files in one result set, keyboard activation, repeated activation, and Copy from stale rows.
   - [ ] Run focused tests and `dotnet build LogReader.sln` after any runtime commit, then `dotnet test LogReader.sln` at slice completion.

Tradeoff to record: warning permits navigation to potentially unrelated content, while blocking prevents a formerly available action. Either choice is deliberate behavior drift and requires product approval.

## Phase 8C - Portability and remaining file edge cases

Status: Deferred by decision
Primary goals: Portability, file-state clarity, coherent edge-case behavior

### Tracking

- [ ] Export compatibility/remapping work selected for implementation. **Deferred by decision.**
- [ ] Encoding and missing-file behavior selected with product approval. **Deferred by decision; behavior gate.**
- [ ] Product approval recorded before changing zero-width regex counts or presentation. **Behavior gate.**
- Notes:
  - 2026-07-15: These items do not block the Phase 8A generation-correctness foundation and are not implicitly scheduled with it. Select and validate each as its own later slice.

### Sub-steps

1. Version and remap dashboard exports when selected.
   - [ ] Add export `schemaVersion`; treat existing unversioned exports as v1.
   - [ ] Reject unsupported future versions with a clear message.
   - [ ] Add an offline old-root to new-root path-remapping workflow.
   - [ ] Retain unresolved paths visibly rather than silently dropping them.

2. Improve file-state and encoding behavior when selected.
   - [ ] Distinguish invalid UTF-8 from ASCII ambiguity and define a documented CP-1252 fallback rule.
   - [ ] Make sampling tolerant of a final split UTF-8 sequence.
   - [ ] Publish a missing-file state after a rotation grace period while retaining the last viewport.
   - [ ] Clear missing state when the file reappears.

3. Make zero-width regex hits coherent only after product approval.
   - [ ] Choose visible zero-width markers, consistent suppression, or another explicitly approved representation.
   - [ ] Align search counts, navigation, highlighting, and copied output with the chosen behavior.
   - [ ] Add interaction tests for counts, selection, Copy, keyboard navigation, and highlighting before enabling the change.

4. Validate each selected slice independently.
   - [ ] Run its focused tests and `dotnet build LogReader.sln` after each runtime commit.
   - [ ] Run `dotnet test LogReader.sln` when a selected portability slice completes.
   - [ ] Run only the manual import/file-state checks relevant to behavior actually changed.

Tradeoff to record: path remapping adds an import decision, legacy encoding detection remains heuristic, missing-file state changes status timing, and any zero-width policy changes visible search semantics.

## Final integration gate

Status: Complete

### 2026-07-14 correctness-first stabilization pass

- [x] Keep the policy, retention, sequential-search, refresh-scheduler, matcher-reuse, and final documentation changes in separate coherent local commits.
- [x] Run `dotnet build LogReader.sln` after the runtime changes and at final integration.
- [x] Run focused state/search/copy, search/index, refresh/generation/open, and filter tests.
- [x] Run `dotnet test LogReader.sln --no-build` at final integration.
- [x] Record representative search/filter/scope/Clear/disposal managed-memory measurements.
- [x] Record user-visible tradeoffs and defer unapproved result ceilings, file-backed rows, rollover UX, oversized-line caps, and virtualization changes.
- Notes:
  - 2026-07-14: Final integration completed with a warning-free solution build. The no-build solution test run passed all 221 core tests and all 807 app tests.
  - 2026-07-14: Stabilization commits before this final documentation update are `6b660b9`, `dca77fc`, `49cf229`, `549ba6f`, `3dc5980`, `9492031`, and `9c9aa1a`. No push or pull request was performed.

### Remaining full-plan gate

- [x] Confirm every completed runtime slice has focused tests and its own coherent commit; confirm each unimplemented phase or sub-step has an explicit deferred/blocked decision.
- [x] Run `dotnet build LogReader.sln`.
- [x] Run `dotnet test LogReader.sln`.
- [x] Re-run only the large-file, filter, memory, or responsiveness measurements relevant to runtime slices that were actually implemented. **No additional measurement was applicable to correctness-only Phase 7.**
- [x] Compare against recorded artifacts or a defined pre-change A/B workload; do not claim comparison with an unavailable numeric pre-Phase-2 baseline. **No new performance comparison is claimed for Phase 7.**
- [x] Record every user-visible difference, including failure-only behavior and any stale-result navigation decision.
- [x] Review commit history before any push or pull request.
- Notes:
  - 2026-07-17: Phase 7 completed in separate runtime commits `3282dc1`, `6041453`, and `78e18ac`, with focused cleanup-failure coverage in `36aeab1` and the final inline-rename boundary fix in `0a399dc`. Final validation passed a warning-free build, all 256 core tests, and all 875 app tests. No push or pull request was performed.
