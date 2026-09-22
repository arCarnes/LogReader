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
| `count_logs` | Count matching lines and occurrences across the complete supported scope, with optional relative windows and time buckets. |
| `list_field_profiles` | Discover saved text/number extraction profiles and built-in fields without scanning logs. |
| `query_logs` | Run a profile-based WQL snapshot filter with typed values and separate parsing/scan diagnostics. |
| `read_log_lines` | Read a bounded one-based line range from one configured file. |
| `read_log_tail` | Read or poll the bounded tail of one configured file using an opaque cursor. |
| `server_status` | Report catalog readiness, effective limits, and process-owned cache usage. |

Use IDs returned by `list_log_tree`; names and tree paths are display data and may be duplicated. Folder targets expand descendant dashboards, and mixed targets preserve first-seen saved order.

## Behavior and limits

The [WQL guide](./WqlGuide.md) explains field setup, expression syntax and agent examples.
`query_logs` accepts expressions up to 8,192 characters and otherwise reuses search's
configured-target, snapshot, paging and output protections. Its cursors additionally
bind the selected profile revision; profile edits require restarting the query.
These two tools are read-only additions; the versioned search result contract and count contracts remain unchanged.

Every request revalidates current saved dashboard membership before file I/O. Results use wire schema version 3 and include a request ID, catalog revision, partial/truncation flags, and structured errors. Results do not expose physical paths or storage roots.

Version 3 envelopes trim repetitive metadata for interactive agent use:

- Search/count results omit `statistics` by default; set `includeStatistics: true` to include performance diagnostics from that execution. `effectiveLimits` is always omitted; use `server_status.result.queryBackend.limits` for server caps/defaults.
- Search/count overall and per-file `incompleteReasons`, search `pageIncompleteReasons`, and search `hits`/`excerpts` are omitted when empty. Missing means an empty list.
- Search/count/read/tail file `error` is omitted when null. Missing means no file error.
- Search file `encoding` is omitted in `countsOnly`, and `evaluatedThroughLine` is present only for incomplete evaluations with a known boundary. Excerpt-line `isTruncated` is present only when true. `provenanceTotalCount` is present only when `isProvenanceTruncated` is true.
- Search `files` contains matches plus error, incomplete, unstable, or truncated file evidence. Clean exact zero-hit files are omitted and counted by `pageOmittedZeroHitFileCount`.
- Populated context and reasons, provenance, file IDs, text, cursors, counts, and the retained completion/truncation booleans remain available. False and zero values remain explicit; omission is not a substitute for checking completeness.

The advertised tool output schemas describe these optional fields. Search result contract version 4 returns compact hit coordinates plus merged excerpts; count result contract version 2 retains the compact count shape. The envelope remains schema version 3. Restart the MCP client after upgrading the sidecar to refresh its tools. Envelope version 2 previously removed the version 1 `backend`, `cacheOwnership`, `liveUiAvailable`, and `lastFallbackReason` fields because the dedicated sidecar is always headless and process-scoped.

`returnedHitCount` is the number of returned hit records. `matchingLineCount` counts matching lines, while `matchOccurrenceCount` counts every literal or regular-expression occurrence, including several occurrences on one line. `isPageComplete`, `isQueryComplete`, and per-file `isCountExact` are true only when the declared scope was fully evaluated against stable file generations and count-bearing content was not truncated. Otherwise numeric counts are lower bounds and `incompleteReasons` explains why. Compacting explanatory provenance alone does not invalidate counts.

`search_logs` accepts three result modes:

- `samples` (default) returns compact hit coordinates and chronological excerpts containing each retained hit plus requested context. Overlapping windows share one physical line. It preserves the historic early-stop behavior when a retained-hit limit is exceeded, so its counts can be incomplete.
- `matchesOnly` returns the same compact hit/excerpt shape with hit lines only and continues evaluating the current file page for counts.
- `countsOnly` returns no hit text or context and evaluates the current file page for compact matching-line and occurrence counts.

Each `hits` entry contains a one-based `lineNumber` and a zero-based `matchStart`/`matchLength` into that line's emitted excerpt text. The coordinates describe the first match on the line; `matchOccurrenceCount` still counts every occurrence. Each returned hit line appears once in `excerpts`, and every excerpt line carries its own one-based line number. Contiguous lines form one excerpt; disjoint ranges form separate excerpts.

The response-text budget admits retained hit lines across the whole search page before adding context, so an early file's context cannot displace a later file's hit. Remaining context is selected in configured file order and balanced outward across hits within each file. A physical line shared by overlapping windows is read, budgeted, and serialized once.

Use `count_logs` when the question is “how many times did this known event occur?” It evaluates up to 2,000 configured candidates in one call, while keeping each resolver/search work unit at 50 files. It returns both matching-line and occurrence totals: a line containing the literal twice contributes one matching line and two occurrences. Successful stable evaluation of the complete selected scope sets `isComplete`; deadline expiry, file failures, or generation uncertainty return explicit lower bounds and stable `incompleteReasons`. Explicit caller cancellation retains normal cancellation behavior instead of returning a partial count. No hit text or context is retained.

Count responses include matched files plus incomplete/error files in configured order. Zero-count successful files are omitted. Per-file/provenance records may be compacted under the response budget; `fileRecordTotalCount`, `returnedFileRecordCount`, `isFileRecordTruncated`, and `count_metadata_limit` disclose that compaction without changing otherwise exact overall or bucket counts.

Timestamp bounds are inclusive. Accepted absolute forms are ISO-8601 (including `Z` or a numeric offset), `yyyy-MM-dd HH:mm[:ss[.fffffff]]`, and time-only `HH:mm[:ss[.fffffff]]`. A lower/upper pair must either both include dates or both be time-only. Date/time values without an explicit offset use the server account's local time semantics; time-only bounds compare only time of day. `search_logs` accepts only these absolute forms.

`count_logs` additionally accepts a mutually exclusive `relativeWindow`: `today` or `last <positive integer><m|h|d>`, through 365 elapsed days. `today` means server-local midnight through one captured request instant; `last Nd` means `N × 24` elapsed hours. The response returns the resolved inclusive bounds, offsets, and Windows time-zone ID so callers do not have to infer when the scan occurred.

Set count `bucketSize` to `minute`, `hour`, or `day` for a dense chronological series that includes zero-count buckets; the default is `none`. Bucketing requires a relative window or both absolute bounds and is limited to 1,000 buckets. Dated timestamps and explicit offsets are converted to server-local wall-clock buckets; repeated daylight-saving minutes and hours remain distinct because their offsets differ, nonexistent spring-forward walls are skipped, and each local calendar date has one day bucket. Time-only ranges use clock-time minute/hour buckets, including dated lines by their time of day; day buckets are rejected because no date is known. Bucket line and occurrence totals reconcile with the overall totals whenever `isComplete` is true.

Default bounds include 2,000 configured file candidates per search/count query, 50 files per search page or internal count work unit, 1,000 count buckets, 365 relative-window days, 50 hits per file, 500 total returned hits, 20 context lines per side, 1,000 directly read lines, 4,096 characters per line, 200,000 response characters, and a 30-second deadline. Candidate 2,001 is rejected before path probing or log scanning. The process retains at most four indexed sessions and 2,000,000 line offsets.

Provenance explains which configured target/dashboard routes authorized a returned file. At most 25% of the response character allowance is used for complete provenance records across a response; unused capacity remains available for hit/context text. When `isProvenanceTruncated` is true, `provenanceTotalCount` distinguishes the returned prefix from the complete internal authorization set. Metadata compaction sets `isTruncated` with `provenance_metadata_limit` but does not make otherwise complete search counts inexact.

When configured selection has another file page, `nextCursor` is a versioned opaque signed value. Repeat the identical request, including targets, query options, date offset, and effective limits, with that cursor. The first page's resolved reference date is signed into the cursor, so date-pattern candidates remain stable even when traversal crosses local midnight. Each page rereads the catalog and reauthorizes configured membership. Tampered, malformed, mismatched, stale-catalog, and prior-process cursors fail safely. Search cursors become invalid after server restart and never contain physical paths. `isPageComplete` and page counts describe the current bounded page; cumulative query counts become exact only when the last page succeeds and `isQueryComplete` is true. `unvisited_pages` is expected until then. Within-file retained-hit continuation is not provided by this cursor.

Search reads content sequentially; line offsets accelerate line, context, and tail addressing only. Two local disk operations and one UNC operation may run concurrently per process. Multiple configured clients have independent limits and caches.

Both `search_logs` and `count_logs` accept `includeStatistics` (default `false`). Set it to `true` when investigating scan performance. `result.statistics` then includes the existing `bytesEvaluated`, `elapsedMilliseconds`, `filesStarted`, `filesCompleted`, `filesSkipped`, `peakConcurrentDiskOperations`, and `peakConcurrentUncOperations` counters. Search statistics describe the current file page; count statistics describe the call's attempted work. These are performance diagnostics, not replacements for exactness/completion flags; bytes evaluated use available scan snapshot sizes and are not a physical I/O meter.

The flag only controls output: it does not change scanning, counts, limits, or context, and may change between search cursor pages while other query settings stay identical. Partial results retain available statistics when requested; failures without a result do not fabricate them. Statistics are not retained for a later follow-up call. The advertised schemas describe the optional field. Restart Codex/Claude Code after upgrading the sidecar to discover the new argument.

For example, add `"includeStatistics": true` to an existing search/count request when diagnosing performance. Leave it omitted for ordinary log investigation.

The measurement script records response bytes, elapsed call time, process memory, and count/completion results. Its optional `-IncludeStatistics` switch forwards this setting to search/count calls and records `includeStatistics` in the report. `-SearchQuery` can select a different fixture query, including an absent value for no-hit payload measurements. By default its server `statistics` and `traversalStatistics` fields are null. Run with and without the switch against the same workload to compare payload sizes; these are serialized bytes, not client token measurements.

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
