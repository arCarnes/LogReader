# MCP Log Server Architecture Decision

Status: Accepted for v1
Date: 2026-08-04

## Context

WeezTail needs a read-only Model Context Protocol (MCP) server that can discover and query only the log files currently represented in the persisted dashboard tree. It should reuse a running UI process's file sessions and line-offset indexes when available, remain usable without the UI, and avoid adding another independently packaged executable unless the existing application shape cannot support redirected standard I/O.

The existing application is a .NET 8 WPF `WinExe`, published as a self-contained single-file Windows GUI executable. Its generated WPF entry point constructs `App` immediately, so MCP mode must branch before WPF construction, UI single-instance coordination, and interactive storage setup.

## Decision

- The packaged `WeezTail.exe` remains the only v1 executable. An exact, sole `--mcp-stdio` argument selects MCP mode; every other invocation follows the existing WPF startup path.
- MCP mode never starts, activates, or requires the WPF UI. It prefers a compatible current-user live endpoint when one is available and otherwise uses an in-process, WPF-free headless backend.
- The MCP transport boundary is the `LogReader.Mcp` class library. It pins the official `ModelContextProtocol.Core` package at version 2.0.0 and uses its low-level stdio server API. WeezTail explicitly registers its five fixed tools instead of adding the generic host and assembly-scanning dependency surface.
- MCP stdout is reserved for protocol frames. SDK logging is disabled at the transport boundary; sanitized WeezTail diagnostics use stderr or the existing diagnostic sink.
- Public MCP request/result contracts remain independent of SDK attributes and types. Core owns catalog, selection, query, limit, cursor, and result contracts; Infrastructure owns read-only persistence and headless query execution.
- Live line-index reuse occurs by executing an authorized request inside the UI process over a current-user named pipe. An MCP process never maps another process's private index files.
- Every process owns an isolated line-index cache subtree and lifetime lock. Cleanup is owner-scoped and cannot delete a live owner's mappings.
- V1 advertises bounded tools only. It does not expose arbitrary paths, raw whole-log resources, mutation tools, a network transport, or a persistent daemon.

## Core catalog and selection contract

- A request operates on one defensively immutable snapshot of groups, current catalog files, and configured date-path patterns. Its opaque `sha256:` revision covers every field that can change target authorization or path resolution, but never emits a physical path.
- Target IDs are explicitly typed as folder, dashboard, or log file. Folders expand descendant dashboards in `SortOrder` with an ordinal ID tie-breaker; dashboards preserve `FileIds` order; mixed targets use stable first-seen union order.
- A log-file ID is selectable only while it belongs to at least one dashboard in the same snapshot. Duplicate display names are valid. Duplicate file IDs are rejected; distinct IDs that resolve to the same case-insensitive normalized Windows path are scanned once and retain all equivalent IDs and provenance.
- Tree paths include the selected node name (`Folder / Dashboard / app.log`) and never include its physical path. Tree projection is pre-order, depth-bounded, node-bounded, and resumable by a revision-bound continuation position at the MCP boundary.
- `dateOffsetDays` equal to zero leaves the configured base path unchanged. A positive value expands configured date patterns against the caller-pinned reference date, without consulting the UI's in-memory modifier. Core produces candidates in configured order; an injected backend selector may choose the first existing candidate and must return one of those authorized candidates. If none exists, it preserves the existing UI fallback to the first transformed candidate so the file backend can report the ordinary missing-file error.
- An empty request, unknown or kind-mismatched target, invalid catalog, or target/file limit violation rejects the request before log I/O and returns no first-N subset. A selected file whose configured path or date transform is invalid produces a bounded per-file error while unrelated files remain available. Tree node limits paginate rather than reject valid discovery.
- Internal snapshot and resolved-file physical paths are explicitly excluded from JSON serialization. MCP adapters map only stable IDs, display names, tree provenance, revision, limits, and sanitized errors.

## Evidence

A disposable .NET 8 Windows-GUI-subsystem proof using the official SDK successfully handled MCP `initialize`, `tools/list`, and `tools/call` through redirected standard streams in both development output and a self-contained single-file `win-x64` publish. Closing stdin ended both processes with exit code 0. Protocol responses were the only stdout content; the hosted variant routed SDK logs to stderr.

The low-level `ModelContextProtocol.Core` 2.0.0 proof provided the same protocol behavior without the generic host. Its standalone single-file proof was 2,186,823 bytes smaller and avoided the full hosting, configuration, file-provider, EventLog, and console-logging dependency graph. The SDK is Apache-2.0 licensed and its .NET 8 target is compatible with WeezTail.

## Read-only persistence boundary

- Headless catalog discovery uses `PersistedDashboardSnapshotReader`; it never calls the normal repository `GetAllAsync` methods because those methods intentionally rewrite legacy payloads. It also avoids `JsonStore.GetFilePath`, storage validation, recovery coordination, and normal `AppPaths.RootDirectory` resolution because those paths can create directories, write probes, migrate MSI selection, rewrite envelopes, or move corrupt files.
- A non-interactive resolver reads only the installed configuration and the current MSI user-selection file. Missing per-user selection returns `storage_not_configured` with launch-once guidance. Legacy selection is not adopted or migrated by MCP mode.
- The reader opens `Data/loggroups.json`, `Data/logfiles.json`, and `Data/settings.json` directly with read/delete sharing. Settings are part of the coherent snapshot because date-path authorization depends on their configured replacement patterns.
- Only current schema-version-1 envelopes are interpreted. Raw legacy payloads and older envelopes return `migration_required`; future envelopes return `unsupported_schema`; malformed/null or recovery-artifact states return `recovery_required`. The MCP process never performs migration or recovery.
- Each attempt captures creation/length/write metadata around two content reads of all three stores. A changed stamp or content retries within a small bound; repeated changes or an in-progress `.tmp` write return retryable `catalog_unstable`. Referential validation prevents a missing or mismatched file catalog from authorizing a dashboard member.
- The reader keeps at most one immutable snapshot cache entry. Every request cheaply revalidates root and store metadata, and any settings/group/file replacement invalidates the entry and recalculates the opaque catalog revision.

## Headless query engine

- `HeadlessLogQueryBackend` implements the WPF-free `ILogQueryBackend` contract for tree listing, selected-log search, indexed line reads, polling tail reads, and status. Each request reloads or revalidates the persisted catalog snapshot and resolves typed configured IDs before opening a log. It never accepts a physical path from a client.
- General search calls the existing single-file sequential `SearchService` path. It does not acquire an indexed session or build `MappedLineOffsets` unless the caller explicitly asks for surrounding context. Stable selection order is retained even when bounded workers finish out of order.
- Line, context, and tail reads use a separate Infrastructure-owned `IndexedLogSessionCache`, keyed by normalized case-insensitive path plus resolved encoding. It has reference-counted leases, last-use eviction, a 30-second warm-retention window, four session slots, one serialized cold-build/update gate, and a 2,000,000 aggregate-offset admission budget. These are MCP-process limits and do not change the UI registry's two-minute tab-reopen policy.
- Bounded index construction fails before admitting an offset beyond its allowance. Bounded line reads cap bytes before decoding a pathological line, then cap returned characters. A representative 250,000-line measurement produced an exact 2,000,000-byte mapping in 54 ms on the development machine; because offsets are eight bytes each, the 2,000,000-offset aggregate ceiling limits retained mappings to 16,000,000 bytes, with a similar transient managed build-list ceiling while one serialized cold build is in progress.
- The headless process allows at most two disk-heavy operations and one UNC operation concurrently. Target, file, hit, line, context, response-text, and deadline limits are applied before or during acquisition. One file failure is a sanitized per-file result and does not discard successful files.
- Tail cursors are process-scoped HMAC-authenticated values. Their payload binds the configured file ID, a protected normalized-path identity, resolved encoding, protected file-generation identity, last returned line and byte offset, and observed file size. Rotation, truncation, or offset disagreement returns `generationChanged` and a fresh end-of-file tail; caller-modified or cross-process cursors are rejected.
- Log text is treated as untrusted. Non-tab control characters are replaced, long lines and response text are explicitly marked truncated, and normal errors contain categorical codes without paths, query text, line content, exception dumps, or credentials.
- Backend disposal cancels active requests and defers cache/gate cleanup until their request leases unwind. Cancellation and exceptional exits release disk gates, UNC gates, indexed-session leases, mappings, and cache-owner resources.

The UI's existing `ILogReaderService` entry points retain unbounded interactive behavior. The bounded methods are opt-in, Infrastructure remains a plain `net8.0` project with no App/WPF reference, and no normal startup, composition, repository, tab, viewport, selection, or tail-coordinator call site is changed by the headless engine.

## Live UI endpoint

- `LiveLogEndpoint` is composed only on the ordinary WPF path and starts after the main window is shown. Listener creation is fail-soft: a pipe failure cannot close the window or prevent normal startup/shutdown.
- The endpoint uses the exact immutable persisted catalog semantics used by headless mode, then executes authorized operations through a UI-backed `HeadlessLogQueryBackend` whose indexed-session provider leases the existing `FileSessionRegistry`. It never publishes an in-progress dashboard mutation.
- A derived pipe identity binds protocol version, current Windows user SID, and normalized active storage root without exposing those values. Server streams use `PipeOptions.CurrentUserOnly`; a native client-computer check rejects remote connections even when Windows pipe policy would otherwise allow them.
- The internal protocol is versioned 4-byte-length-prefixed JSON, capped at 1 MiB per frame, with handshake/capability negotiation, request IDs, cancellation frames, safe errors, bounded write/shutdown deadlines, and three listener slots. All clients share one disk-heavy request gate and two light-operation slots.
- UI sessions and agent leases have different ownership. UI tab leases keep the existing retention behavior. Agent-only sessions are capped at four sessions and 2,000,000 offsets and are eligible for immediate eviction; they cannot evict a UI-owned session.
- Interactive work has priority. UI search/filter cancels a competing live agent search, and a UI tab load cancels a cold agent index build. A preempted agent receives a retryable structured error rather than delaying the UI.
- Indexed reads snapshot only the bounded requested offset range while holding the index lease, release it before physical local/UNC I/O, then revalidate the index and file generation. A slow agent read therefore cannot retain an index read lock needed by UI update/tail work.
- An idle endpoint owns three pending asynchronous pipe accepts but performs no polling, catalog load, log I/O, index build, file session acquisition, watcher creation, or tail work.

## Backend arbitration

- `ArbitratingLogQueryBackend` prefers a compatible live client, lazily creates a headless backend when unavailable, and serializes selection so one request remains pinned to one backend.
- The live connect deadline is 300 ms and handshake deadline is 750 ms. After absence or incompatibility, availability is reprobed only on a later request after a two-second cooldown; no timer or background poll is retained.
- Live transport loss before a result is emitted permits one headless retry for these read-only idempotent operations. A live `busy`/interactive-priority result is returned to the caller and is never bypassed through headless disk I/O.
- When live service becomes available, the arbitrator disposes the owned headless backend and owner-scoped cache. Tail cursors do not cross a backend switch: the cursor is cleared and the response reports a generation/backend reset, which can repeat bounded lines but cannot silently omit them.
- Disposal cancels active work and waits for bounded lease cleanup. Each configured MCP client still owns its process; arbitration is not a daemon and never starts the UI.

## Consequences

- Normal UI startup now depends on a small custom `[STAThread]` entry point remaining behaviorally equivalent to the generated WPF entry point. Default and unknown-argument launch behavior require explicit regression coverage.
- The MCP SDK and host code increase the single packaged executable by a measured amount; release validation compares executable, portable zip, MSI, startup, and idle-memory deltas against a same-commit baseline.
- When the UI is running, agent searches still consume disk or network I/O. UI work receives priority and all live clients share one disk-heavy MCP work slot.
- When the UI is absent, each MCP client owns a bounded headless cache and process. V1 does not launch a shared daemon.
- The current Windows user is the v1 local trust boundary. Results omit physical paths and every file operation reauthorizes current dashboard membership.
- Normal-user impact, package measurements, scheduling safeguards, and release gates are maintained in [MCP Mainline Impact Analysis](./McpMainlineImpact.md).
- Security boundaries, adversarial coverage, current MCP guidance, and residual risks are maintained in [MCP Security and Resilience Model](./McpSecurityModel.md).
