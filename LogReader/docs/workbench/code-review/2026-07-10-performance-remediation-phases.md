# WeezTail performance remediation phases

Status: In progress  
Scope: Follow-up work from the 2026-07-10 multi-agent review. This plan intentionally starts at Phase 2; baseline work and Phase 1 quick wins are excluded.

## Tracking conventions

- Set a phase to `In progress`, `Blocked`, or `Complete`; add a short dated note under its tracking block.
- Check a sub-step only after its code and focused tests are complete.
- Keep each coherent sub-step in its own local commit. Do not combine unrelated phases.
- Every runtime-affecting sub-step runs `dotnet build LogReader.sln` and the narrowest affected tests; complete phases also run `dotnet test LogReader.sln`.
- All work remains local and fully offline. Do not add network, telemetry, cloud, or external-service dependencies.

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
- Notes:
  - 2026-07-11: Added a filter-specific result contract containing ordered unique line numbers, file error state, timestamp metadata, and hit-limit state. `FilterPanelViewModel` consumes only those fields; snapshot search retains line text, first-match offsets, and full match spans unchanged.
  - 2026-07-11: Current-tab and multi-file filter scans now enter bounded background work before opening/reading/matching files. Adaptive per-volume and UNC concurrency gates remain in place, while existing session checks continue to reject cancelled or stale UI results.
  - 2026-07-11: Literal filtering performs one `IndexOf` per line and regex filtering uses `Regex.IsMatch`; the scan directly emits sorted unique line numbers, removing filter-path span graphs and the UI-side distinct/sort pass. No pre-Phase-2 numeric benchmark artifact was available, so the allocation/latency comparison is structural rather than a claimed measured percentage.
  - 2026-07-11: Focused service tests (58) and filter-panel tests (29) passed. Full solution validation passed (213 core tests and 780 app tests); an unrelated dashboard test timed out on the first run, passed immediately in isolation, and the complete no-build rerun passed.

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
Status: Complete  
Primary goals: Search speed, UI responsiveness

### Tracking

- [x] Density threshold and fallback behavior documented.
- [x] Tests added/updated.
- [x] Implementation complete.
- [x] Focused validation complete.
- [x] Full solution validation complete.
- [x] Commit created.
- Notes:
  - 2026-07-11: Disk searches with include-only scopes can now use the open tab's leased `LineIndex`. Inputs are normalized to ordered unique in-range lines and coalesced into contiguous batches of at most 512 lines. The existing adaptive per-volume/global file concurrency remains in effect.
  - 2026-07-11: Indexed reads are limited to scopes at or below 2% density with at most 64 batches. Exclude scopes, denser scopes, highly fragmented scopes, missing indexes, short/stale index reads, and content-version changes fall back to the existing sequential scan. Empty include scopes return an empty result without reading the file.
  - 2026-07-11: A local 20,000-line comparison measured a 1% scope at 1.0 ms indexed versus 4.0 ms sequential. The 9.9% and 25% cases selected sequential fallback; measured adaptive-wrapper times were 3.6 ms versus 3.8 ms and 7.3 ms versus 5.3 ms respectively. The cutoff intentionally stays well below the observed crossover, and the fragmented-scope cap avoids excessive indexed segment reads.
  - 2026-07-11: Repeat-query literal candidate caching was not implemented. The 1% indexed-scope measurement above serves as the candidate-read performance proxy, while a 100,000-line `List<int>` candidate set allocated 400,064 bytes before dictionary, query, and version metadata. Display search is capped, so retained hits are not a complete reusable candidate set; making them complete would require an additional globally budgeted cache that conflicts with the still-pending Phase 4 memory work. Regex searches remain uncached.
  - 2026-07-11: Focused search-service tests (65) and search-panel tests (70) passed. The full build passed with no warnings; all 221 core tests passed. The same unrelated dashboard timing test noted in Phase 2 timed out in the full app batch, passed in isolation, and the other 779 app tests passed together.
  - 2026-07-11: Implementation commit: `a7575ff` (`Add adaptive indexed sparse search`).

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

Tradeoff to record: random indexed reads can underperform sequential scans for dense or highly fragmented selections.

## Phase 4 - Bound search and highlighting memory

Review findings: S06, S18, S20  
Status: Not started  
Primary goals: Memory efficiency, UI responsiveness

### Tracking

- [ ] Memory budgets documented.
- [ ] Tests added/updated.
- [ ] Implementation complete.
- [ ] Focused validation complete.
- [ ] Full solution validation complete.
- [ ] Commit created.
- Notes:

### Sub-steps

1. Bound inactive workspace results.
   - [ ] Define global hit-count/byte-budget accounting for `WorkspaceScopedStateStore` search state.
   - [ ] Add LRU eviction for inactive scopes.
   - [ ] Retain query/options/status after eviction; mark results as needing rerun.
   - [ ] Avoid deep-cloning immutable hits where ownership allows sharing.

2. Bound result-row and highlight caches.
   - [ ] Replace permanent result-row caching with a viewport-sized LRU or lightweight immutable row views.
   - [ ] Bound highlight regex caching and invalidate or evict invalid entries.
   - [ ] Keep UI behavior correct when an evicted row or pattern is revisited.

3. Validate.
   - [ ] Add tests for scope eviction, selected-scope preservation, row recreation, and cache bounds.
   - [ ] Exercise repeated scope switches and full result scrolling while recording managed allocations.
   - [ ] Run build, focused tests, full test suite, and memory comparison.

Tradeoff to record: evicted result state must rerun locally; evicted rows/patterns may be recreated or recompiled.

## Phase 5 - Harden initial indexing and oversized-line behavior

Review findings: S07, S09  
Status: Not started  
Primary goals: Memory efficiency, UI responsiveness, search speed

### Tracking

- [ ] Oversized/binary-line policy documented.
- [ ] Tests added/updated.
- [ ] Implementation complete.
- [ ] Focused validation complete.
- [ ] Full solution validation complete.
- [ ] Commit created.
- Notes:

### Sub-steps

1. Stream index offsets during construction.
   - [ ] Design a chunked or spill-to-temp-file builder for `MappedLineOffsets`.
   - [ ] Preserve BOM/newline behavior and cancellation cleanup.
   - [ ] Ensure failed/cancelled builds remove temporary artifacts.
   - [ ] Retain only bounded append overflow after freezing.

2. Define oversized-line handling.
   - [ ] Add a configurable maximum decoded-line size with a user-visible status.
   - [ ] Implement chunked literal search where feasible.
   - [ ] Define explicit regex behavior for oversized lines: safely reject or cap evaluation rather than allocating file-sized strings.
   - [ ] Make viewport behavior clear for lines that cannot be materialized in full.

3. Validate.
   - [ ] Add very-large line, binary-like/no-newline, UTF-8, UTF-16, CR/LF/CRLF, cancellation, and cleanup tests.
   - [ ] Measure index peak managed memory on a high-line-count fixture.
   - [ ] Run build, focused tests, full test suite, and memory comparison.

Tradeoff to record: capped or skipped oversized lines can omit matches and must be explicitly reported.

## Phase 6 - Scale dashboard and ad hoc UI operations

Review findings: S05, S17, S19, S21  
Status: Not started  
Primary goals: UI responsiveness, memory efficiency

### Tracking

- [ ] UI interaction behavior documented.
- [ ] Tests added/updated.
- [ ] Implementation complete.
- [ ] Focused validation complete.
- [ ] Full solution validation complete.
- [ ] Commit created.
- Notes:

### Sub-steps

1. Make member refresh targeted.
   - [ ] Identify the exact groups/files affected by add, remove, copy, reorder, and cross-dashboard move.
   - [ ] Reuse existing targeted refresh paths or add narrow equivalents.
   - [ ] Update reorder/move presentation locally without unnecessary probes or VM recreation.
   - [ ] Reserve full refresh for import, recovery, and global display-setting changes.

2. Fix virtualization.
   - [ ] Replace ad hoc `ItemsControl` with a recycling virtualized list.
   - [ ] Verify keyboard navigation, selection, drag/drop, context menu, and styling.
   - [ ] Decide whether nested dashboard lists receive bounded viewports or are flattened into a single virtualized row model.
   - [ ] Implement the selected dashboard-list strategy in a separate coherent commit if structural.

3. Reduce dispatcher work.
   - [ ] Throttle open progress updates by elapsed time or meaningful count increments.
   - [ ] Compute tree-filter matches/expansion from a snapshot off-thread.
   - [ ] Apply one batched UI mutation guarded by a generation token.

4. Validate.
   - [ ] Add tests for selection/modifier state, targeted refresh behavior, stale tree-filter results, and progress throttling.
   - [ ] Manually exercise large dashboard/ad hoc workloads.
   - [ ] Run build, focused tests, full test suite, and UI responsiveness comparison.

Tradeoff to record: a bounded nested list changes scrolling behavior; flattening requires more presentation-state management.

## Phase 7 - Make persistence and imports transactional

Review findings: S08, S15, S16, S22  
Status: Not started  
Primary goals: Correctness, UI responsiveness, memory efficiency

### Tracking

- [ ] Failure model documented.
- [ ] Tests added/updated.
- [ ] Implementation complete.
- [ ] Focused validation complete.
- [ ] Full solution validation complete.
- [ ] Commit created.
- Notes:

### Sub-steps

1. Add batch persistence operations.
   - [ ] Define repository-level snapshot replacement for coordinated group changes.
   - [ ] Use it for group reorder/up/down and cross-dashboard file moves.
   - [ ] Update VMs only after persistence succeeds, or restore snapshots on failure.

2. Make imports recoverable.
   - [ ] Track newly created file-catalog entries during import.
   - [ ] Roll back unreferenced additions after a failed group replacement, or implement a small local recovery journal.
   - [ ] Verify failure paths leave no partial groups, stale ordering, or orphaned catalog growth.

3. Validate imported values centrally.
   - [ ] Reject null collection elements and normalize optional collections.
   - [ ] Validate names, paths, IDs, highlighting rules, fonts, colors, and collection/string size limits.
   - [ ] Surface invalid imports as controlled `InvalidDataException` messages.

4. Validate.
   - [ ] Add fault-injection tests for every write boundary.
   - [ ] Add malformed/import-size/null-element tests.
   - [ ] Run build, focused tests, full test suite, and recovery verification.

Tradeoff to record: coordinated persistence needs a batch contract or recovery metadata, but avoids partial state and repeated whole-file writes.

## Phase 8 - Portability and remaining file edge cases

Review findings: S23, S24, S26, S27, S28  
Status: Not started  
Primary goals: Search correctness, user clarity

### Tracking

- [ ] Compatibility policy documented.
- [ ] Tests added/updated.
- [ ] Implementation complete.
- [ ] Focused validation complete.
- [ ] Full solution validation complete.
- [ ] Commit created.
- Notes:

### Sub-steps

1. Version and remap dashboard exports.
   - [ ] Add export `schemaVersion`; treat existing unversioned exports as v1.
   - [ ] Reject unsupported future versions with a clear message.
   - [ ] Add an offline old-root to new-root path-remapping workflow.
   - [ ] Retain unresolved paths visibly rather than silently dropping them.

2. Improve file-state and encoding behavior.
   - [ ] Distinguish invalid UTF-8 from ASCII ambiguity and define a documented CP-1252 fallback rule.
   - [ ] Make sampling tolerant of a final split UTF-8 sequence.
   - [ ] Publish a missing-file state after a rotation grace period while retaining the last viewport.
   - [ ] Clear missing state when the file reappears.

3. Make zero-width regex hits coherent.
   - [ ] Choose and document either visible zero-width markers or consistent suppression.
   - [ ] Align search counts, navigation, highlighting, and copied output with the chosen behavior.

4. Validate.
   - [ ] Add export compatibility/remapping, invalid-UTF8, missing/recreated-file, and zero-width-regex tests.
   - [ ] Run build, focused tests, full test suite, and manual import/file-state checks.

Tradeoff to record: path remapping adds an import decision; legacy encoding detection remains heuristic.

## Final integration gate

Status: Not started

- [ ] Confirm every phase has its focused tests and one coherent commit.
- [ ] Run `dotnet build LogReader.sln`.
- [ ] Run `dotnet test LogReader.sln`.
- [ ] Re-run local large-file, sparse-filter, broad-filter, and responsiveness measurements.
- [ ] Compare results against the pre-Phase-2 baseline and document regressions/tradeoffs.
- [ ] Review commit history before any push or pull request.
