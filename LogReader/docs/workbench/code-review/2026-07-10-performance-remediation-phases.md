# WeezTail performance remediation phases

Status: In progress
Scope: Follow-up work from the 2026-07-10 multi-agent review. This plan intentionally starts at Phase 2; baseline work and Phase 1 quick wins are excluded.

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

- Set a phase to `In progress`, `Blocked`, `Complete`, or `Deferred by decision`; add a short dated note under its tracking block.
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
  - 2026-07-14: The 2026-07-11 indexed-search implementation notes above are retained as experiment history, not current runtime behavior. Commit `49cf229` removed both indexed-search members from `ISearchService`, the adaptive planner/batching/callback path, and runtime eligibility wiring. Disk snapshot searches again use the ordinary sequential `SearchFilesAsync` path. Line-index leases and range reads remain for viewport and tail processing, and stable index timestamp metadata remains as harmless correctness evidence. The warning-free solution build, 82 focused core search/index tests, and 16 focused app tests passed.
  - 2026-07-14: Indexed disk search is **Deferred by decision** until it can read through one stable file handle or file identity, fall back to sequential search after indexed I/O errors, and demonstrate a material win on a realistic workload under the correctness-first threshold.

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

Tradeoff to record: inactive scope results can grow with the number and size of user-created scope states, within the existing per-file acquisition/text caps. The active scope no longer has a hidden stored duplicate, repeated switching does not grow retained memory, and disposal releases the managed graph; defensive cloning currently trades substantial transient allocation for clear ownership and predictable behavior.

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
- [ ] Product approval recorded for any match-omitting, truncating, or capped oversized-line behavior. **Behavior gate.**
- Notes:
  - 2026-07-14: Oversized-line caps are an explicit product behavior gate. They can omit matches, change copied text, or make viewport content differ from the file. Do not implement a cap until the user-visible status, search/Copy semantics, and interaction tests are approved; this stabilization pass makes no oversized-line behavior change.

### Sub-steps

1. Stream index offsets during construction.
   - [ ] Design a chunked or spill-to-temp-file builder for `MappedLineOffsets`.
   - [ ] Preserve BOM/newline behavior and cancellation cleanup.
   - [ ] Ensure failed/cancelled builds remove temporary artifacts.
   - [ ] Retain only bounded append overflow after freezing.

2. Define oversized-line handling.
   - [ ] Obtain product approval for any cap, rejection, truncation, or match-omission behavior.
   - [ ] Add a configurable maximum decoded-line size with a user-visible status.
   - [ ] Implement chunked literal search where feasible.
   - [ ] Define explicit regex behavior for oversized lines: safely reject or cap evaluation rather than allocating file-sized strings.
   - [ ] Make viewport behavior clear for lines that cannot be materialized in full.

3. Validate.
   - [ ] Add very-large line, binary-like/no-newline, UTF-8, UTF-16, CR/LF/CRLF, cancellation, and cleanup tests.
   - [ ] Add interaction tests covering displayed content, result counts, navigation, selection, and Copy before enabling a cap.
   - [ ] Measure index peak managed memory on a high-line-count fixture.
   - [ ] Run build, focused tests, full test suite, and memory comparison.

Tradeoff to record: capped or skipped oversized lines can omit matches and alter visible/copied content; reporting alone is insufficient without explicit product approval and interaction coverage.

## Phase 6 - Scale dashboard and ad hoc UI operations

Review findings: S05, S17, S19, S21  
Status: In progress
Primary goals: UI responsiveness, memory efficiency

### Tracking

- [x] Background member-refresh interaction behavior documented.
- [x] Member-refresh scheduler and generation tests added/updated.
- [ ] Implementation complete.
- [x] Member-refresh focused validation complete.
- [x] Full solution validation complete.
- [x] Member-refresh stabilization commit created.
- [ ] Product approval recorded before changing nested-list scrolling or virtualization behavior. **Behavior gate.**
- Notes:
  - 2026-07-14: Commit `549ba6f` replaced the unbounded task/closure chain with an internal scheduler that permits at most one running and one pending batch. Targeted pending requests merge by file ID with latest-path wins, a pending full refresh subsumes targeted work, each batch exposes one shared completion task, and work starts immediately without debounce.
  - 2026-07-14: Suppression teardown waits only for the batch containing its own request. Direct file opening no longer waits for a global refresh drain, so a tab becomes usable while unrelated metadata probes converge asynchronously. Foreground dashboard refresh commands remain outside the background notification scheduler and can run concurrently with a slow probe.
  - 2026-07-14: Dashboard activation now registers monotonic full and per-file targeted commit generations. Stale same-ID targeted work and older full work are discarded; disjoint targeted IDs remain independent; a current ordinary full refresh preserves member objects superseded by newer targeted work; and active modifiers promote targeted requests to full generations. Cancellation is checked after probes and immediately before UI mutations. Shutdown rejects new work and cancels both running and pending batches.
  - 2026-07-14: The warning-free solution build and 63 focused scheduler, generation, suppression, immediate-open, dispatcher, and targeted-member tests passed. The immediate-open race blocks an unrelated full metadata refresh, proves `OpenFilePathAsync` still completes, then verifies member metadata converges after release.
  - 2026-07-14: Final boundedness review found that generation metadata could retain an unbounded sequence of ad-hoc file IDs even though those IDs never affect dashboard members. Commit `9492031` filters scheduler and activation requests to current dashboard membership, skips probes for untracked IDs, and prunes no-longer-tracked generation keys. The warning-free build and 80 focused dashboard/scheduler/coordinator/open tests passed.
  - 2026-07-14: Virtualization remains an explicit product behavior gate. Recycling or bounding nested dashboard lists can change scrolling, keyboard focus, selection, drag/drop, and context-menu behavior; no virtualization change is part of the current stabilization pass.

### Sub-steps

1. Bound background member-refresh work and commits.
   - [x] Allow at most one running and one mergeable pending batch, starting immediately without debounce.
   - [x] Merge targeted IDs with latest-path wins and let a pending full refresh subsume targeted work.
   - [x] Share one completion task per batch and keep suppression teardown waiting for its exact batch.
   - [x] Remove the global refresh drain from direct file opening while retaining asynchronous metadata convergence.
   - [x] Reject new work and cancel pending/running work on shutdown; check cancellation after probes and before UI mutation.
   - [x] Keep foreground dashboard refreshes independent from the background scheduler.
   - [x] Guard full and targeted UI commits with monotonic generations, including modifier promotion and disjoint-ID races.

2. Make remaining member refresh operations targeted.
   - [ ] Identify the exact groups/files affected by add, remove, copy, reorder, and cross-dashboard move.
   - [ ] Reuse existing targeted refresh paths or add narrow equivalents.
   - [ ] Update reorder/move presentation locally without unnecessary probes or VM recreation.
   - [ ] Reserve full refresh for import, recovery, and global display-setting changes.

3. Fix virtualization only after product approval.
   - [ ] Obtain approval for scrolling, focus, selection, and nested-list behavior changes.
   - [ ] Replace ad hoc `ItemsControl` with a recycling virtualized list.
   - [ ] Verify keyboard navigation, selection, drag/drop, context menu, and styling.
   - [ ] Decide whether nested dashboard lists receive bounded viewports or are flattened into a single virtualized row model.
   - [ ] Implement the selected dashboard-list strategy in a separate coherent commit if structural.

4. Reduce dispatcher work.
   - [ ] Throttle open progress updates by elapsed time or meaningful count increments.
   - [ ] Compute tree-filter matches/expansion from a snapshot off-thread.
   - [ ] Apply one batched UI mutation guarded by a generation token.

5. Validate.
   - [x] Add scheduler tests for 10,000 notifications, ID/path merging, full dominance, failure recovery, and shutdown cancellation.
   - [x] Add reversed-completion generation tests for full/targeted ordering, same and disjoint IDs, and modifiers.
   - [x] Prove direct opening completes while an unrelated metadata probe is blocked and suppression still waits for its own batch.
   - [ ] Add remaining tests for selection/modifier state, targeted operations, stale tree-filter results, and progress throttling.
   - [ ] Manually exercise large dashboard/ad hoc workloads.
   - [ ] Run build, focused tests, full test suite, and UI responsiveness comparison.

Tradeoff to record: metadata may visibly converge just after a newly opened tab becomes usable; generation guards make that convergence predictable. A bounded nested list changes scrolling behavior, while flattening requires more presentation-state management, so either virtualization design requires explicit approval and interaction tests.

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
- [ ] Result navigation behavior after file rollover decided. **Deferred by decision.**
- Notes:
  - 2026-07-14: Search results retain captured snapshot text. Copy therefore remains internally consistent after the underlying file changes and does not re-read a line that may now contain different content.
  - 2026-07-14: Result navigation still uses the captured path and line number. After a daily rollover, replacement, or truncation, that coordinate can refer to unrelated content. This is a current predictability defect, not a reason to make Copy file-backed.
  - 2026-07-14: Rollover-aware result staleness is **Deferred by decision** pending a focused investigation. Use `SearchContentVersion` and existing rotation/replacement signals to determine when snapshot results should be marked stale, then decide whether navigation warns or is blocked. Prioritize daily rollover/replacement over arbitrary manual-edit edge cases.
  - 2026-07-14: Do not implement file-backed rows, line-number Copy lookups, or a warning/blocking UX in this pass.

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

3. Investigate rollover-aware result navigation.
   - [ ] Correlate each result set with `SearchContentVersion` and available rotation, truncation, and replacement signals.
   - [ ] Mark snapshot results stale without changing their retained text or Copy output.
   - [ ] Decide, with product input, whether navigation warns and proceeds or is blocked when the current file generation differs.
   - [ ] Add daily-rollover/replacement tests before broader manual-edit heuristics. **Deferred by decision.**

4. Make zero-width regex hits coherent.
   - [ ] Choose and document either visible zero-width markers or consistent suppression.
   - [ ] Align search counts, navigation, highlighting, and copied output with the chosen behavior.

5. Validate.
   - [ ] Add export compatibility/remapping, invalid-UTF8, missing/recreated-file, rollover-staleness, and zero-width-regex tests.
   - [ ] Run build, focused tests, full test suite, and manual import/file-state checks.

Tradeoff to record: path remapping adds an import decision; legacy encoding detection remains heuristic.

## Final integration gate

Status: In progress

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

- [ ] Confirm every phase has its focused tests and one coherent commit.
- [ ] Run `dotnet build LogReader.sln`.
- [ ] Run `dotnet test LogReader.sln`.
- [ ] Re-run local large-file, sparse-filter, broad-filter, and responsiveness measurements.
- [ ] Compare results against the pre-Phase-2 baseline and document regressions/tradeoffs.
- [ ] Review commit history before any push or pull request.
