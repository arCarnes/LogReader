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

## Consequences

- Normal UI startup now depends on a small custom `[STAThread]` entry point remaining behaviorally equivalent to the generated WPF entry point. Default and unknown-argument launch behavior require explicit regression coverage.
- The MCP SDK and host code increase the single packaged executable by a measured amount; release validation compares executable, portable zip, MSI, startup, and idle-memory deltas against a same-commit baseline.
- When the UI is running, agent searches still consume disk or network I/O. UI work receives priority and all live clients share one disk-heavy MCP work slot.
- When the UI is absent, each MCP client owns a bounded headless cache and process. V1 does not launch a shared daemon.
- The current Windows user is the v1 local trust boundary. Results omit physical paths and every file operation reauthorizes current dashboard membership.
