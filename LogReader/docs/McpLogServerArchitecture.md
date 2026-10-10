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
- The desktop MCP Server dialog persists continuation capacity settings. Each sidecar reads and validates these settings once at startup without migration or writes; running processes keep their limits until restart.
- The MCP transport and executable boundary remains the `LogReader.Mcp` project using the pinned `ModelContextProtocol.Core` package and eight explicitly registered tools.
- Stdout is reserved for protocol frames. Sanitized startup diagnostics use stderr.
- V1 exposes no arbitrary paths, whole-log resources, mutation tools, network listener, shared daemon, or cross-account broker.

## Catalog and authorization

- Every operation reads or revalidates one immutable snapshot of saved groups, files, and date-path patterns. The revision covers all authorization-relevant fields but never exposes physical paths.
- Callers select typed folder, dashboard, or log-file IDs. Folders expand descendant dashboards; dashboards preserve saved file order; duplicate physical paths are scanned once.
- A file remains selectable only while it belongs to a dashboard in the same snapshot. Invalid topology or request limits reject before log I/O.
- Positive `dateOffsetDays` values expand saved patterns in configured order. The backend selects the first existing authorized candidate and falls back to the first candidate only when none exist, allowing the ordinary missing-file error.
- Public contracts contain stable IDs, display names, provenance, revisions, limits, bounded text, and sanitized errors. Physical paths and storage roots never serialize.
- MCP tool envelopes use wire schema version 4. Search result contract 6 emits noteworthy file records with compact hit coordinates and merged chronological excerpts; count result contract 3 retains its sparse matched/incomplete records. Search hit lines are admitted across a page before context. Context is selected in configured file order, balanced outward across hits within each file, and each physical line is read, budgeted, and serialized once even when windows overlap. Aggregate aliases are removed in favor of one returned-hit field and one completion field at each scope. The MCP serialization policy omits search/count statistics by default, limit tables, selected empty excerpt/reason/hit arrays, null errors, exact-file evaluation boundaries, counts-only search encodings, false excerpt-line truncation, and unneeded provenance totals; populated evidence and explicit completion/truncation flags are preserved. `includeStatistics=true` opts into existing current-page search / whole-call count instrumentation. One MCP response projector uses fixed serializer options and per-call transformation state to return equivalent structured/text JSON. All four log tools use explicit typed envelope schemas with shared provenance definitions, strict tuple shapes, dense/inline alternatives and enum-constrained string inputs. The backend and cursor fingerprints do not consume presentation modes or the statistics flag. No diagnostic history is retained. Server limits remain available through `server_status`. Version 2 previously removed the version 1 live-backend and shared-cache status fields because the dedicated sidecar is always headless and process-scoped.

## Read-only persistence

- `PersistedDashboardSnapshotReader` reads the saved stores directly and never invokes repositories that migrate, rewrite, recover, validate by writing, or create storage.
- The non-interactive resolver uses the installed configuration and the launching Windows account's MSI storage-selection file. Missing setup returns `storage_not_configured`; legacy or corrupt stores require an ordinary UI launch for migration or recovery.
- Snapshot reads detect concurrent replacement and retry within a small bound. Referential validation prevents inconsistent stores from authorizing files.

## Query engine and resource ownership

- `HeadlessLogQueryBackend` implements tree listing, resumable search and counting/aggregation, indexed line reads, polling tail reads, and status.
- `QueryContinuationStore` owns bounded search/count state and signs small process-scoped session/revision references. It serializes advancement and retains one prior reply for retries. The legacy `SearchCursorCodec` remains for alternative non-incremental search providers. Resolver continuation remains a pure Core contract and contains only a stable-file index plus truncated SHA-256 path identities for cross-page deduplication; no physical path is serialized.
- Searches use bounded sequential I/O. Context is collected during scanning; line offsets are built only for explicit line/tail addressing; they are not a search index.
- `count_logs` resumes the same authorized resolver and scanner in internal 200-file work units, retains cumulative counts/buckets and returns no log text. Search/count normally yield after 5 seconds or 256 MiB, with at most two active file checkpoints. Retained sessions expire after 15 minutes idle and are bounded to 16 sessions, 64 MiB/session and 256 MiB/process. Each file extent freezes on first open; append drift is conservative and replacement/edit/truncation terminates that file. Relative windows and date-pattern resolution share one captured server-local request instant.
- Text search limits each response to 200 hits/file and 2,000 total hits, and each query to 10,000 emitted hits across continuations. The cloned traversal counter commits atomically with the page; cancellation rolls back and replay spends no additional allowance. A query-cap terminal reply is replayable but remains traversal-incomplete with no next cursor. Count-only modes are exempt. The 30-second request deadline and 800,000-character per-response content budget are independent of the scan slice.
- WQL profile discovery and snapshot queries reuse the read-only settings snapshot and search scanner. The Core extractor/compiler is shared with desktop preview/search; it adds no database or scripting dependency. WQL retains its existing snapshot evaluator and configured-file paging; the resumable text scanner does not evaluate WQL. WQL cursors bind the selected profile revision separately from catalog authorization revisions, and WQL/text-search cursors cannot be interchanged. See [Basic WQL and structured fields](WqlGuide.md).
- `IndexedLogSessionCache` is keyed by normalized path and resolved encoding, retains at most four sessions for 30 seconds, and admits at most 2,000,000 mapped offsets across them.
- Every process owns a unique cache subtree and lifetime lock. Startup cleanup removes legacy flat indexes and stale versioned owners without deleting another live process's mappings.
- Indexed reads copy only bounded offsets, release the index operation gate before physical I/O, and revalidate generation afterward.
- At most two disk-heavy operations and one UNC operation run concurrently per MCP process. File, hit, line, context, text, index, and deadline limits apply before or during acquisition.
- Tail cursors are process-scoped HMAC values bound to configured file ID, protected path/generation identity, encoding, line offset, and observed size. Rotation, truncation, and unterminated-line growth are explicit.
- Disposal cancels active requests and releases gates, leases, mappings, and owner resources after request leases unwind.

## Compact MCP presentation

- `McpResponseJsonPolicy` reports envelope v4 only at serialization; Core envelopes remain v3, search contract v6 and count contract v3. Existing metadata/statistics omissions remain in force.
- Count defaults to sparse indexed `[bucketIndex, matchingLineCount, matchOccurrenceCount]` tuples. The projector derives a grid from actual dense boundaries, adding anchors whenever fixed-offset/duration progression differs in instant or UTC offset. Final exclusive boundaries are included when needed. Decoding uses the latest preceding anchor plus nominal steps, preserving DST and variable-length days without reproducing timezone rules. Missing tuples establish exact zeros only for complete results. Dense mode keeps backend bucket objects; unbucketed results have a mode-specific empty array and no grid.
- Search/count/read/tail default to a per-response provenance table and ordered file references. Record equality covers all provenance fields with ordinal strings. Only metadata retained by existing backend budgets is projected; authorization and budget accounting are unchanged. Inline mode retains original arrays. Hidden tail files never populate a table.
- Presentation choices are validated before backend invocation and excluded from Core queries, continuation fingerprints and tail filter binding. Cached backend results can be projected differently on replay; tables never survive a response. No mutable serializer or cross-call projection state is shared.
- Initialization supplies at most 512 characters of shared investigation guidance. Tool descriptions retain specific cap/cursor caveats; `server_status` supplies effective limits. Schemas document omission, reconstruction and explicit alternative formats.
- Measurement report v8 records both modes, logical buckets versus emitted tuples, retained versus emitted provenance, protocol/structured bytes and optional schema validation. Serialized-byte savings are separate from actual Claude Code/Codex token measurements.

## Consequences

- Normal WPF startup, dependency closure, and shutdown contain no MCP host, SDK, listener, IPC objects, agent leases, or scheduling hooks.
- Agent work is isolated from UI memory and locks, but its disk or network traffic can still contend with interactive activity at the operating-system level.
- Multiple MCP clients build independent bounded caches. A shared daemon should be considered only after measured multi-client usage justifies its lifecycle and security cost.
- The launching Windows account must resolve WeezTail's saved storage configuration and have read access to configured logs. Cross-account deployment is deferred until company requirements are known.
- Packaging must place the install configuration beside both executables and smoke-test the published sidecar before producing portable or MSI artifacts.
