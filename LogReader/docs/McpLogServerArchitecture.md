# MCP Log Server Architecture Decision

Status: Accepted for v1
Date: 2026-08-05

## Context

WeezTail provides a local, read-only MCP server that discovers and queries only logs represented in the saved dashboard tree. Keeping the MCP host inside the .NET 8 WPF executable would couple the desktop application's entry point, dependency graph, release artifact, and replacement lifecycle to agent clients even though MCP execution is headless and process-isolated.

Sharing private UI indexes was evaluated and removed from v1. Cross-process reuse required a named-pipe service, backend arbitration, separate UI/agent ownership, cancellation, and scheduling inside the user-facing process. The measured index-build savings did not justify that concurrency and lifecycle surface.

## Decision

- Official packages contain `WeezTail.exe` for the WPF application and `WeezTail.Mcp.exe` for the MCP stdio server. They are built, versioned, installed, and upgraded together.
- `WeezTail.Mcp.exe` is always WPF-free and headless. It requires no mode argument and never starts, activates, connects to, or executes work inside the running UI.
- `WeezTail.App` has no reference to `LogReader.Mcp` or the MCP SDK and uses the generated WPF entry point.
- Each configured MCP client owns its process, persisted-catalog reader, concurrency gates, tail-cursor key, and bounded line-index cache.
- The MCP transport and executable boundary remains the `LogReader.Mcp` project using the pinned `ModelContextProtocol.Core` package and five explicitly registered tools.
- Stdout is reserved for protocol frames. Sanitized startup diagnostics use stderr.
- V1 exposes no arbitrary paths, whole-log resources, mutation tools, network listener, shared daemon, or cross-account broker.

## Catalog and authorization

- Every operation reads or revalidates one immutable snapshot of saved groups, files, and date-path patterns. The revision covers all authorization-relevant fields but never exposes physical paths.
- Callers select typed folder, dashboard, or log-file IDs. Folders expand descendant dashboards; dashboards preserve saved file order; duplicate physical paths are scanned once.
- A file remains selectable only while it belongs to a dashboard in the same snapshot. Invalid topology or request limits reject before log I/O.
- Positive `dateOffsetDays` values expand saved patterns in configured order. The backend selects the first existing authorized candidate and falls back to the first candidate only when none exist, allowing the ordinary missing-file error.
- Public contracts contain stable IDs, display names, provenance, revisions, limits, bounded text, and sanitized errors. Physical paths and storage roots never serialize.

## Read-only persistence

- `PersistedDashboardSnapshotReader` reads the saved stores directly and never invokes repositories that migrate, rewrite, recover, validate by writing, or create storage.
- The non-interactive resolver uses the installed configuration and the launching Windows account's MSI storage-selection file. Missing setup returns `storage_not_configured`; legacy or corrupt stores require an ordinary UI launch for migration or recovery.
- Snapshot reads detect concurrent replacement and retry within a small bound. Referential validation prevents inconsistent stores from authorizing files.

## Query engine and resource ownership

- `HeadlessLogQueryBackend` implements tree listing, bounded search, indexed line reads, polling tail reads, and status.
- Searches use bounded sequential I/O. Line offsets are built only for line/context/tail addressing; they are not a search index.
- `IndexedLogSessionCache` is keyed by normalized path and resolved encoding, retains at most four sessions for 30 seconds, and admits at most 2,000,000 mapped offsets across them.
- Every process owns a unique cache subtree and lifetime lock. Startup cleanup removes legacy flat indexes and stale versioned owners without deleting another live process's mappings.
- Indexed reads copy only bounded offsets, release the index operation gate before physical I/O, and revalidate generation afterward.
- At most two disk-heavy operations and one UNC operation run concurrently per MCP process. File, hit, line, context, text, index, and deadline limits apply before or during acquisition.
- Tail cursors are process-scoped HMAC values bound to configured file ID, protected path/generation identity, encoding, line offset, and observed size. Rotation, truncation, and unterminated-line growth are explicit.
- Disposal cancels active requests and releases gates, leases, mappings, and owner resources after request leases unwind.

## Consequences

- Normal WPF startup, dependency closure, and shutdown contain no MCP host, SDK, listener, IPC objects, agent leases, or scheduling hooks.
- Agent work is isolated from UI memory and locks, but its disk or network traffic can still contend with interactive activity at the operating-system level.
- Multiple MCP clients build independent bounded caches. A shared daemon should be considered only after measured multi-client usage justifies its lifecycle and security cost.
- The launching Windows account must resolve WeezTail's saved storage configuration and have read access to configured logs. Cross-account deployment is deferred until company requirements are known.
- Packaging must place the install configuration beside both executables and smoke-test the published sidecar before producing portable or MSI artifacts.
