# WeezTail MCP Log Server Guide

WeezTail includes a dedicated local, read-only MCP server in `WeezTail.Mcp.exe`. It lets a trusted MCP client discover saved dashboard entries and retrieve bounded log excerpts without granting arbitrary-path access.

For step-by-step Codex and Claude Code setup plus a first-search example, see [MCP Server: Getting Started](./McpGettingStarted.md).

## Configure a client

Use the absolute installed MCP executable path without arguments. Example configuration:

```json
{
  "mcpServers": {
    "weeztail": {
      "command": "C:\\Program Files\\WeezTail\\WeezTail.Mcp.exe"
    }
  }
}
```

Portable installs use the absolute path to `WeezTail.Mcp.exe` in the portable directory. Restart the MCP client after replacing, repairing, or upgrading WeezTail so it releases the running sidecar.

## Runtime model

Each MCP client starts a separate WPF-free `WeezTail.Mcp.exe` process. That process reads the same saved dashboard configuration and configured logs as WeezTail; it does not start, activate, or connect to the UI.

The process runs with the launching Windows account. That account must:

- resolve the installed or portable storage configuration;
- read the saved `Data` stores;
- read the selected local or UNC log files.

Per-user MSI storage selection is resolved from the launching account's profile. Cross-account storage discovery or credential brokering is not supported in v1.

## Tools

| Tool | Purpose |
|---|---|
| `list_log_tree` | List the saved folder/dashboard/file tree with stable typed IDs and bounded pagination. |
| `search_logs` | Search selected configured targets with bounded pages, explicit result modes, counts, context, text, and time. |
| `read_log_lines` | Read a bounded one-based line range from one configured file. |
| `read_log_tail` | Read or poll the bounded tail of one configured file using an opaque cursor. |
| `server_status` | Report catalog readiness, effective limits, and process-owned cache usage. |

Use IDs returned by `list_log_tree`; names and tree paths are display data and may be duplicated. Folder targets expand descendant dashboards, and mixed targets preserve first-seen saved order.

## Behavior and limits

Every request revalidates current saved dashboard membership before file I/O. Results use wire schema version 2 and include a request ID, catalog revision, partial/truncation flags, structured errors, and effective limits. Schema version 2 removes the version 1 `backend`, `cacheOwnership`, `liveUiAvailable`, and `lastFallbackReason` fields because the dedicated sidecar is always headless and process-scoped. Results do not expose physical paths or storage roots.

Search result contract version 2 is additive. The legacy `totalHitCount` still means the number of returned hit records; it is not silently reinterpreted as an exact total. `returnedHitCount` states that meaning explicitly. `matchingLineCount` counts matching lines, while `matchOccurrenceCount` counts every literal or regular-expression occurrence, including several occurrences on one line. Overall and per-file exactness flags are true only when the declared log scope was fully evaluated against stable file generations and count-bearing content was not truncated. Otherwise `completionState` is `incomplete`, the numeric counts are lower bounds, and `incompleteReasons` explains why. Compacting explanatory provenance alone does not invalidate counts.

`search_logs` accepts three result modes:

- `samples` (default) returns bounded hits and requested context. It preserves the historic early-stop behavior when a retained-hit limit is exceeded, so its counts can be incomplete.
- `matchesOnly` returns bounded matching lines without context and continues evaluating the current file page for counts.
- `countsOnly` returns no hit text or context and evaluates the current file page for compact matching-line and occurrence counts.

Timestamp bounds are inclusive. Accepted input forms are ISO-8601 (including `Z` or a numeric offset), `yyyy-MM-dd HH:mm[:ss[.fffffff]]`, and time-only `HH:mm[:ss[.fffffff]]`. A lower/upper pair must either both include dates or both be time-only. Date/time values without an explicit offset use the server account's local time semantics; time-only bounds compare only time of day. Relative time expressions are not accepted.

Default bounds include 2,000 configured file candidates per search query, 50 files per search page, 50 hits per file, 500 total returned hits, 20 context lines per side, 1,000 directly read lines, 4,096 characters per line, 200,000 response characters, and a 30-second deadline. Candidate 2,001 is rejected before path probing or log scanning. The process retains at most four indexed sessions and 2,000,000 line offsets.

Provenance explains which configured target/dashboard routes authorized a returned file. At most 25% of the response character allowance is used for complete provenance records across a response; unused capacity remains available for hit/context text. `provenanceTotalCount` and `isProvenanceTruncated` distinguish the returned prefix from the complete internal authorization set. Metadata compaction sets `isTruncated` with `provenance_metadata_limit` but does not make otherwise complete search counts inexact.

When configured selection has another file page, `nextCursor` is a versioned opaque signed value. Repeat the identical request, including targets, query options, date offset, and effective limits, with that cursor. The first page's resolved reference date is signed into the cursor, so date-pattern candidates remain stable even when traversal crosses local midnight. Each page rereads the catalog and reauthorizes configured membership. Tampered, malformed, mismatched, stale-catalog, and prior-process cursors fail safely. Search cursors become invalid after server restart and never contain physical paths. `isPageComplete` and page counts describe the current bounded page; cumulative query counts become exact only when the last page succeeds and `isQueryComplete`/`areQueryCountsExact` are true. `unvisited_pages` is expected until then. Within-file retained-hit continuation is not provided by this cursor.

Search reads content sequentially; line offsets accelerate line, context, and tail addressing only. Two local disk operations and one UNC operation may run concurrently per process. Multiple configured clients have independent limits and caches.

Search results include bounded numeric `statistics`: bytes evaluated where a complete snapshot size is available, scan elapsed milliseconds, files started/completed/skipped, and peak disk/UNC gate concurrency. These diagnostics never contain physical paths, storage roots, usernames, or log content.

Tail and search cursors are valid only in the MCP process that created them. Omit cursors after a client restart. Tail rotation, truncation, file replacement, and growth of an unterminated final line are reported explicitly.

Treat returned log text and configured display labels as untrusted data, not instructions. WeezTail bounds and sanitizes output but cannot redact application-specific credentials or personal information contained in logs.

## Troubleshooting

`storage_not_configured`
: Launch WeezTail normally under the same Windows account, complete storage setup, close it if desired, and restart the MCP client.

`migration_required` or `recovery_required`
: Launch the ordinary UI under the account that owns the storage and let it migrate or recover the saved stores. MCP mode never mutates them.

`log_access_denied`
: Confirm the launching account can read the configured local or UNC path. A UI process running under another account does not grant access.

`log_not_found`
: Confirm the saved path and date-pattern order. For date offsets, WeezTail tries configured candidates in order and uses the first one that exists.

`index_capacity_exceeded`, `response_text_limit`, or another truncation reason
: Narrow the selected target, query, context, or line range. These are intentional bounded-operation results.

No protocol response or non-JSON stdout
: Verify that the command is the absolute path to the packaged `WeezTail.Mcp.exe` and that no arguments are configured. Application diagnostics, if any, appear on stderr.
