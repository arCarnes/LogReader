# UNC performance design

Status: Proposed implementation; no runtime changes delivered by this document. Baseline: `9228d9c`, reviewed 2026-11-02. Read the [execution plan](ExecutionPlan.md) for status and [validation requirements](ValidationAndMeasurements.md) for acceptance.

## Observed behavior

Source links below are relative to this document. Recheck symbols at execution time rather than relying on historical line numbers.

| Subsystem | Observation at the planning baseline |
| --- | --- |
| [FileSession](../../../LogReader.App/Services/FileSession.cs) | `BuildIndexOffUiAsync` skips `AdvanceAsync` for a newly created interactive build, then suspends it before releasing admission. |
| [Interactive reader](../../../LogReader.Infrastructure/Services/ChunkedLogReaderService.Interactive.cs) | Startup already opens a handle and captures evidence. Resumption after suspension reopens and validates identity, time, prefix and boundary. Scan quantum is 4 MiB; checks between reads allow yielding. |
| [UI policy](../../../LogReader.Core/AdaptiveParallelismPolicy.cs) | Dashboard host cap two, search/filter host cap three, share cap two; global caps 24 and 32. These are fixed topology limits. |
| [Dashboard scheduler](../../../LogReader.App/Services/DashboardLoadScheduler.cs) | Foreground previews have priority; background work has a separate one-operation restriction. |
| [MCP backend](../../../LogReader.Infrastructure/Services/HeadlessLogQueryBackend.cs) | All UNC paths share a one-permit gate. The effective default global disk cap is separately two. Ordinary operations, indexed leases and detached existence probes must all retain correct admission ownership. |
| [MCP monitor](../../../LogReader.Infrastructure/Services/HeadlessLogQueryBackend.Monitor.cs) | Baselines and poll files are processed sequentially; scanner open, encoding detection and validation involve additional file operations even on idle polls. |
| [Resumable query](../../../LogReader.Infrastructure/Services/HeadlessLogQueryBackend.Resumable.cs) | Search/count already prefetch the current and following file; increasing a gate alone cannot exceed this two-file lookahead. |
| [Scanner](../../../LogReader.Infrastructure/Services/ResumableLogScanner.cs) | Checkpoints retain generation, encoding, extent and logical line state. Recent changes preserve split UTF-8 writes; older performance results predate the current continuity fixes. |

Measured evidence supports the first two opportunities and overlapping idle checks. It does not prove which individual calls caused SMB packets, cold throughput, server saturation or application responsiveness.

## U1: first interactive scan

Within the existing acquired scheduler lease, create the interactive build and, when it can resume, call `AdvanceAsync` even on the creation turn. Keep its existing `shouldYield` callback and cancellation token. If creation selected the unknown-identity fallback, do not dereference or advance the disposed/null build; use the existing admitted fallback path.

Retain the handle only while that lease is active. If incomplete, suspend before releasing admission. A completed build transfers its index exactly once and follows existing publication/tail-registration ordering. Do not change the 4 MiB quantum, restart/encoding-change limits, identity checks, preview limits or background scheduling. A pending preview may cause the initial advance to yield without scanning; that is correct.

Tests must demonstrate first-turn scanning without a reopen when uncontended and suspension/revalidation at a real scheduling boundary. Prefer existing reader/scheduler test seams; add a small internal observer only if the behavior cannot otherwise be established. Do not expose diagnostics or implementation details in product UI.

## U2: UI concurrency candidate

Evaluate dashboard and search/filter UNC host limits of **four**, with share limit **four**. Keep dashboard global 24, search/filter global 32, local-root two and unknown-path one. Preserve the dashboard scheduler's preview reservation, foreground ordering and single background operation. No new setting or public policy API is required.

Test same share, multiple shares on one host, different hosts, local/UNC mixtures, extended UNC syntax and cancellation while queued. Host and share are independent bounds, not multipliers. Do not add real network targets for topology tests.

Use the [candidate decision rule](ValidationAndMeasurements.md#candidate-decision-rule) before retaining the new defaults. If rejected, retain the previous defaults, record the evidence and continue the independent MCP phases. Do not broaden scope to an adaptive feedback controller or an eight-worker default.

## U3: MCP admission

Replace the single UNC semaphore with one backend-owned admission coordinator for all backend disk-operation entry points. Share normalization with existing Core topology classification where practical, without coupling Infrastructure to WPF. UNC classification must handle ordinary and extended UNC paths case-insensitively; classification examines path strings only and performs no directory discovery. Preserve current treatment of non-UNC and malformed inputs; do not introduce mapped-drive/network discovery.

The coordinator uses the existing effective global `MaximumConcurrentDiskOperations`, unchanged at **two** by default. Per-host and per-share limits are each `min(2, effectiveGlobalLimit)`; explicit global limit one still serializes all disk work. Different hosts use different group counters. Queued requests receive all required permits atomically when global and relevant group capacity are available, using oldest eligible request order. A waiter for a busy group must not hold a global permit or block eligible work for another group. Active uninterruptible OS work can still consume available capacity; this is not a hard network-timeout guarantee.

Use a small internal request/lease interface: acquire by path and cancellation token; return an idempotently disposable lease owning global and group capacity. Cancellation removes queued work and releases partially established state. Metrics count admitted operations, not queued requests. Reclaim inactive topology entries when no active lease or waiter references them; the registry must not grow with historical paths/hosts. Disposal rejects new work, cancels pending waits and leaves active/detached leases releasable until their work completes.

Route `ExecuteDiskOperationAsync`, `AcquireDiskOperationAsync` and `ProbePathExistsAsync` through this coordinator. Transfer the whole lease to detached probe completion; returning early on cancellation must not return capacity while a synchronous OS probe is still running. Preserve backend lifetime accounting, exception observation and shutdown draining. Keep search/count lookahead at two and current heavy-request admission. Do not reuse the UI gate acquisition order without addressing its global-permit holding behavior.

## U4: parallel monitor preparation with ordered evaluation

Use a request-local two-slot lookahead, capped further by the effective global disk limit, for initial baselines and subsequent poll preparation. Workers operate on independent checkpoint/tracker clones. Start synchronous open/encoding/metadata work on worker threads so constructing the second task can actually overlap the first; do not execute that work on the UI or transport dispatch thread.

For initialization, prepare at most two existing admitted baseline slots concurrently. Merge each completed result in stable file order using the existing stopped-error policy. Keep the rule that a scope cursor commits only after every selected file has a safe baseline or explicit stopped error. Handoff keeps its existing source-boundary transfer semantics and does not establish fresh baselines.

For polling:

1. Select at most the existing `MaxFiles`/remaining-sweep slots in round-robin order; stopped entries still occupy their normal slot. Prepare only the next two eligible slots, not the whole scope.
2. Preparation acquires topology/global admission, opens the scanner on a private checkpoint and checks encoding using existing continuity rules. It does not evaluate lines, allocate response text or advance shared sweep state. Return a request-owned prepared reader/error and release the preparation I/O lease when the actual operation returns.
3. Reduce the next slot in stable order. Reacquire disk admission for its scan and after-read validation, apply the existing total/per-file budgets and update only that private tracker. The reducer alone mutates aggregate counters, response/provenance budgets, event output, `NextFile`, `SweepRemaining` and `SweepActive`.
4. Perform a final scanner validation before accepting a prepared reader's result, including idle readers. Concurrent preparation must not turn a delayed/stale encoding or generation observation into a successful unchanged result. Existing split-UTF-8 and unfinished-line semantics remain authoritative.
5. Consume a slot only after its success or existing per-file error is reduced. Discard unconsumed prepared clones when a total budget, period or cancellation stops the poll. A prepared but unconsumed file is not checked and does not advance the committed cursor.

Do not parallelize line evaluation or share mutable matchers/budgets across workers in this pass. Preserve the public 100-file scope cap, ten-file default poll, 4 MiB total scan cap, 256 KiB per-file cap, line/text/provenance bounds and selected date. Timeouts remain cooperative. Each file's deadline includes its preparation and reduction work; never reset the timeout when a prepared result is consumed. Preserve the current file-slice calculation from the admitted poll size and request timeout.

Keep at most two live scanner buffers/readers across preparing, prepared, reducing and disposal states in one request. An unconsumed failed/timed-out task still owns its slot and applicable lease until it returns; do not replace it with unlimited background work. Review existing working-memory reservation, currently including 512 KiB for two 256 KiB scanner buffers, and prove the candidate fits it. Reader disposal may temporarily retain rented buffers; include that lifetime in the bound. Do not increase public memory limits or allocate full indexes for monitor files.

On poll cancellation, discard uncommitted state and preserve the original usable cursor. Dispose/drain owned prepared work using existing lifetime rules, retaining admission for uninterruptible work. Never publish late results into a completed transaction. Replays return the existing committed envelope without new disk work. Scope authorization is rechecked through the existing transaction/catalog path on every advance and replay.

## Compatibility and diagnostics

No new public types, method parameters, settings, MCP tools, output fields, schema versions or cursor formats are planned. A change to public semantics is outside this package. Existing concurrency metrics can now report peaks up to the already reported effective global limit; document the scheduling change without raising that limit. Internal lease/preparation types and test seams are permitted.

Performance diagnostics must be bounded and omit credentials, full physical paths, log text and private labels. Keep MCP stdout protocol-only. Shared host/share keys stay internal; normalize report labels rather than emitting machine topology. Do not alter permissions, network configuration, signing/encryption, or persistent storage to obtain a better benchmark.

## Deferred decisions

Idle backoff changes freshness and two-observation detection latency, so it requires a separate product decision. Watcher hints need overflow/disconnect recovery and periodic identity verification. Metadata consolidation showed little gain; larger-buffer, content-cache, handle-pool and portable-cache changes lack representative evidence. These are excluded from execution, along with rolling-date following, schema redesign and broader file discovery.
