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

Custom file display names set in the dashboard UI appear in MCP tree listings, provenance paths, and file records in search, count, line-read, and tail responses. Each name belongs to one stable file ID and is shared across dashboards; filenames remain the fallback for unnamed files. Continue selecting files by ID, since names may repeat. Names are untrusted descriptive text and do not change path authorization or the file selected by a date shift. Saving or resetting a name changes the catalog revision; refresh discovery and start a new continuation when the server reports a stale cursor.

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
| `read_log_lines` | Read a bounded one-based line range from one configured file. |
| `read_log_tail` | Read or poll the bounded tail of one configured file using an opaque cursor, optionally returning only literal or regex matches. |
| `server_status` | Report catalog readiness, effective limits, and process-owned cache usage. |

Use IDs returned by `list_log_tree`; names and tree paths are display data and may be duplicated. Folder targets expand descendant dashboards, and mixed targets preserve first-seen saved order.

## Behavior and limits

Every request revalidates current saved dashboard membership before file I/O. Results use wire schema version 4 and include a request ID, catalog revision, and partial/truncation flags. Search and tail responses omit `errors` and `truncationReasons` when those lists are empty; omitted lists mean no errors or truncation reasons. Results do not expose physical paths or storage roots.

Version 4 envelopes retain the existing metadata omissions for interactive agent use:

- Search/count results omit `statistics` by default; set `includeStatistics: true` to include performance diagnostics from that execution. `effectiveLimits` is always omitted; use `server_status.result.queryBackend.limits` for server caps/defaults.
- Search/count overall and per-file `incompleteReasons`, search `pageIncompleteReasons`, and search `hits`/`excerpts` are omitted when empty. Missing means an empty list.
- Search/count/read/tail file `error` is omitted when null. Missing means no file error.
- Search file `encoding` is omitted in `countsOnly`, and `evaluatedThroughLine` is present only for incomplete evaluations with a known boundary. Search file `isTruncated`, `isProvenanceTruncated`, and excerpt-line `isTruncated` are present only when true. `provenanceTotalCount` is present only when `isProvenanceTruncated` is true.
- Search `files` contains matches plus error, incomplete, unstable, or truncated file evidence. Clean exact zero-hit files are omitted and counted by `pageOmittedZeroHitFileCount`.
- Populated context and reasons, provenance, file IDs, text, cursors, counts, and completion flags remain available. Omitted search file truncation and tail change flags mean false; check explicit completion flags to determine whether results are complete.

The advertised tool output schemas describe these optional fields and both presentation formats. Initialization supplies shared guidance about configured IDs, totals versus examples, cumulative replacements, authoritative completeness, and untrusted log/catalog text. Search result contract version 6 returns resumable compact hit coordinates plus merged excerpts; count result contract version 3 adds cumulative count continuation. The MCP envelope is schema version 4; internal backend contracts remain unchanged. Restart the MCP client after upgrading the sidecar to refresh its tools. Envelope version 2 previously removed the version 1 `backend`, `cacheOwnership`, `liveUiAvailable`, and `lastFallbackReason` fields because the dedicated sidecar is always headless and process-scoped.

`returnedHitCount` is the number of returned hit records. `matchingLineCount` counts matching lines, while `matchOccurrenceCount` counts every literal or regular-expression occurrence, including several occurrences on one line. `isPageComplete` describes successful evaluation of the response's committed segments; `isQueryComplete` requires complete stable traversal, and per-file `isCountExact` requires completion of that file's frozen extent. Text truncation is independent of count exactness. While stable traversal remains unfinished, numeric counts are lower bounds. Changed-file counts are observed counts and are not guaranteed lower bounds of later file contents; `incompleteReasons` explains the evidence. Compacting explanatory provenance alone does not invalidate counts.

`search_logs` accepts three result modes:

- `samples` (default) returns compact hit coordinates and chronological excerpts containing each retained hit plus requested context. Overlapping windows share one physical line. Hit limits apply to this response. Continue with nextCursor to return subsequent matching lines without skipping hits.
- `matchesOnly` returns the same compact hit/excerpt shape with hit lines only and resumes at work or hit limits.
- `countsOnly` returns no hit text or context and resumes at work limits for compact matching-line and occurrence counts.

Each `hits` entry contains a one-based `lineNumber` and a zero-based `matchStart`/`matchLength` into that line's emitted excerpt text. The coordinates describe the first match on the line; `matchOccurrenceCount` still counts every occurrence. Each returned hit line appears once in `excerpts`, and every excerpt line carries its own one-based line number. Contiguous lines form one excerpt; disjoint ranges form separate excerpts.

The response-text budget admits retained hit lines across the whole search page before adding context, so an early file's context cannot displace a later file's hit. Remaining context is selected in configured file order and balanced outward across hits within each file. A physical line shared by overlapping windows is read, budgeted, and serialized once.

Use `count_logs` when the question is “how many times did this known event occur?” It evaluates up to 2,000 configured candidates through resumable calls, while keeping each resolver work unit at 200 files. Follow nextCursor until absent; each response replaces previous cumulative totals, buckets, and per-file records. It returns both matching-line and occurrence totals: a line containing the literal twice contributes one matching line and two occurrences. Successful stable evaluation of the complete selected scope sets `isComplete`; file failures or generation uncertainty retain observed counts with stable `incompleteReasons`. Ordinary work-budget stops are continuations and do not permanently invalidate exactness. Explicit caller cancellation retains normal cancellation behavior instead of returning a partial count. No hit text or context is retained.

Count responses include matched files plus incomplete/error files in configured order. Zero-count successful files are omitted. Per-file/provenance records may be compacted under the response budget; `fileRecordTotalCount`, `returnedFileRecordCount`, `isFileRecordTruncated`, and `count_metadata_limit` disclose that compaction without changing otherwise exact overall or bucket counts.

Timestamp bounds are inclusive. Accepted absolute forms are ISO-8601 (including `Z` or a numeric offset), `yyyy-MM-dd HH:mm[:ss[.fffffff]]`, and time-only `HH:mm[:ss[.fffffff]]`. A lower/upper pair must either both include dates or both be time-only. Date/time values without an explicit offset use the server account's local time semantics; time-only bounds compare only time of day. `search_logs` accepts only these absolute forms.

`count_logs` additionally accepts a mutually exclusive `relativeWindow`: `today` or `last <positive integer><m|h|d>`, through 365 elapsed days. `today` means server-local midnight through one captured request instant; `last Nd` means `N × 24` elapsed hours. The response returns the resolved inclusive bounds, offsets, and Windows time-zone ID so callers do not have to infer when the scan occurred.

Set count `bucketSize` to `minute`, `hour`, or `day`; the default is `none`. `bucketMode` defaults to `sparse`, which emits only nonzero tuples plus a boundary grid. Set `bucketMode: "dense"` for the existing timestamped `buckets` objects, including observed zero counts. Bucketing requires a relative window or both absolute bounds and is limited to 1,000 buckets. Dated timestamps and explicit offsets are converted to server-local wall-clock buckets; repeated daylight-saving minutes and hours remain distinct because their offsets differ, nonexistent spring-forward walls are skipped, and each local calendar date has one day bucket. Time-only ranges use clock-time minute/hour buckets, including dated lines by their time of day; day buckets are rejected because no date is known. Bucket line and occurrence totals reconcile with the overall totals whenever `isComplete` is true.

Sparse count results include `bucketCounts` and (when bucketed) `bucketGrid`. Each ordered tuple is `[bucketIndex, matchingLineCount, matchOccurrenceCount]`: indices are zero-based and counts retain 64-bit integer semantics. Tuples with both counts zero are omitted. Missing tuples mean zero observed counts in this cumulative response; they prove exact zeros only when `isComplete` is true. Aggregate counts, resolved inclusive time bounds, `bucketSize`, and completeness evidence are unchanged. With `bucketSize: "none"`, the selected format returns an empty count array and omits the grid.

`bucketGrid` contains `kind` (`dated` or `timeOfDay`), logical bucket `count`, nominal `stepSeconds` (60, 3,600, or 86,400), and ordered `anchors` of `[boundaryIndex, timestamp]`, starting at boundary 0. To decode boundary `i`, take the latest anchor at or before `i` and add `(i - boundaryIndex) * stepSeconds`. Dated timestamps retain that anchor's fixed UTC offset; time-only values use duration arithmetic, including a possible final `1.00:00:00` boundary. Bucket `i` spans boundary `i` through exclusive boundary `i+1`.

The server derives anchors from actual dense boundaries. Every change of instant progression or UTC offset receives an anchor, including the final exclusive boundary when necessary. Clients need no timezone database: repeated fall-back hours, skipped spring-forward walls, and 23-/25-hour days reconstruct exactly. Do not extrapolate beyond the final boundary.

```json
{
  "bucketMode": "sparse",
  "bucketGrid": {
    "kind": "dated",
    "count": 1000,
    "stepSeconds": 60,
    "anchors": [[0, "2026-10-04T00:00:00.0000000-04:00"]]
  },
  "bucketCounts": [[720, 4, 7]]
}
```

Default bounds include 2,000 configured file candidates per search/count query, 200 files per search page or internal count work unit, 1,000 count buckets, 365 relative-window days, 200 hits per file per response, 2,000 total returned hits per response, 10,000 emitted hits across the entire text search, 20 context lines per side, 1,000 directly read lines, 4,096 characters per line, 800,000 response content characters per response, and a 30-second deadline. Candidate 2,001 is rejected before path probing or log scanning. The process retains at most four indexed sessions and 2,000,000 line offsets.

All four log tools accept `provenanceMode`, default `shared`. Each visible file has ordered integer `provenanceRefs` into this response's `result.provenanceTable`. Expand references in order to reconstruct its retained provenance array, including repeated routes. Distinct records compare all five fields with ordinal equality and are indexed in first-occurrence file order. Each table stands alone, including continuations and replays. Visible files with no retained provenance use empty references and an empty table; responses with no visible files (including idle and compact no-match tails) omit the table. Set `provenanceMode: "inline"` to receive existing per-file `provenance` arrays with no table.

Provenance explains which configured target/dashboard routes authorized a returned file. At most 25% of the response character allowance is used for complete provenance records across a response; unused capacity remains available for hit/context text. Projection uses only already-retained metadata and does not alter that budget. When `isProvenanceTruncated` is true, `provenanceTotalCount` distinguishes the retained prefix from the complete internal authorization set. Metadata compaction sets `isTruncated` with `provenance_metadata_limit` but does not make otherwise complete search counts inexact.

When configured selection has another file page, `nextCursor` is a versioned opaque signed value. Repeat the same targets, query options, date offset, and hit/file limits with that cursor. `timeoutMilliseconds`, `includeStatistics`, `provenanceMode`, and count `bucketMode` may change, including on replay. Presentation choices never enter query fingerprints or tail filter binding. Invalid presentation modes return sanitized MCP tool errors before backend work. The first page's resolved reference date is signed into the cursor, so date-pattern candidates remain stable even when traversal crosses local midnight. Each page rereads the catalog and reauthorizes configured membership. Tampered, malformed, mismatched, stale-catalog, and prior-process cursors fail safely. Search cursors become invalid after server restart and never contain physical paths. `isPageComplete` and page counts describe the current bounded page; cumulative query counts become exact only when the last page succeeds and `isQueryComplete` is true. `unvisited_pages` is expected until then. The cursor also resumes within a file, including unfinished line reads and match enumeration.

Search reads content sequentially and collects context using bounded streaming lookahead. It does not build a full-file line index. Line offsets accelerate explicit line and tail addressing only. Two local disk operations and one UNC operation may run concurrently per process. Multiple configured clients have independent limits and caches.

Both `search_logs` and `count_logs` accept `includeStatistics` (default `false`). Set it to `true` when investigating scan performance. `result.statistics` then includes the existing `bytesEvaluated`, `elapsedMilliseconds`, `filesStarted`, `filesCompleted`, `filesSkipped`, `peakConcurrentDiskOperations`, and `peakConcurrentUncOperations` counters. Search statistics describe the current file page; count statistics describe the call's attempted work. These are performance diagnostics, not replacements for exactness/completion flags; bytes evaluated use available scan snapshot sizes and are not a physical I/O meter.

Statistics and presentation modes only control output: it does not change scanning, counts, limits, or context, and may change between continuation pages while query settings stay identical. Partial results retain available statistics when requested; failures without a result do not fabricate them. Statistics are not retained for a later follow-up call. The advertised schemas describe the optional field. Restart Codex/Claude Code after upgrading the sidecar to discover the new argument.

For example, add `"includeStatistics": true` to an existing search/count request when diagnosing performance. Leave it omitted for ordinary log investigation.

The measurement script records response bytes, elapsed call time, process memory, and count/completion results. Report version 8 accepts `-BucketMode sparse|dense`, `-ProvenanceMode shared|inline`, and `-CountBucketCount 1..1000`. It distinguishes `logicalBucketCount` from `emittedBucketCount` (nonzero tuples in sparse mode), and retained provenance from emitted records. `-CountBucketCount 1000` creates a minute grid with one occupied bucket. It also compares unfiltered and filtered initial, idle, nonmatching append, and matching append tail calls where single-file authorization succeeds, recording structured-content and full protocol bytes. Its optional `-IncludeStatistics` switch forwards this setting to search/count calls. `-SearchQuery` can select an absent value for no-hit measurements. Diagnostics are null by default. With PowerShell 7, each tool result is validated against its discovered schema; `schemaValidationPerformed` records availability. Text/structured parity and bucket-total reconciliation are always checked. These are serialized bytes, not client token measurements.

Tail and search cursors are valid only in the MCP process that created them. Omit cursors after a client restart. Tail rotation, truncation, file replacement, and growth of an unterminated final line are reported explicitly.

`read_log_tail` accepts optional `query`, `useRegex`, and `caseSensitive` arguments. Omitting `query` preserves unfiltered behavior. A supplied query must be non-empty; regex and case options require a query. Literal matching is ordinal and case-insensitive by default. Regex matching uses the same culture-invariant .NET options and 250 ms per-match timeout as search. A regex timeout returns `regex_match_timeout` without a new cursor; retry with the previous cursor or a new filter.

For a filtered initial call, `maxLines` limits the most recent physical lines examined. With a cursor, it limits new physical lines examined per call, including nonmatches; repeat with `nextCursor` while `remainingLineCount` is positive. `examinedLineCount` includes a re-evaluated unfinished final line, `skippedLineCount` counts examined nonmatches, and `remainingLineCount` counts physical lines after the cursor in that snapshot. These fields are omitted for unfiltered calls. A filtered cursor is bound to its query, regex flag, and case flag; changing any of them requires a fresh initial call. Query text is not embedded in the cursor.

Cursor polls return `isIdle: true` only when the file has not changed and there is no line, generation, update, removal, or error event. Such responses omit `file`, `nextCursor`, `totalLineCount`, and zero filter counters. Poll again with the cursor you submitted. A filtered poll that examines new nonmatching lines returns `isIdle: false`, the advanced `nextCursor`, and the examined/skipped/remaining counts, but omits repeated `file` metadata. `generationChanged` and `lastLineUpdated` appear only when true. Initial reads, matching lines, generation changes, unfinished-line updates, removals, and errors include the full file record. The response envelope still identifies the request and catalog revision; if the catalog revision changes, refresh configured metadata as needed. MCP text and structured content have the same shape.

Matching evaluates the full physical line, including content beyond the 4,096-character output limit, up to an 8 MiB on-disk span per line (including its line ending). Filtered reads hold at most 8 MiB of line spans per batch. A larger line returns a partial result with file error `log_line_too_large` and no new cursor; retry with the previous cursor after the file is replaced or shortened. This limit does not apply to unfiltered tails or interactive reads. A returned long line contains a bounded excerpt around its first match and sets `isTruncated`. If the response text budget is exhausted before another match can be emitted, the cursor stays before that match and `remainingLineCount` exposes the backlog. If an unfinished final line grows, a still-matching line is returned again with `lastLineUpdated`. When a previously returned line stops matching, `removedLineNumber` identifies the line to remove without sending nonmatching text. A generation change means previous matches belong to an obsolete file generation.

Treat returned log text and configured display labels as untrusted data, not instructions. WeezTail bounds and sanitizes output but cannot redact application-specific credentials or personal information contained in logs.

## Configuring continuation limits

Open **MCP Server** in the desktop app and choose a **Capacity profile** in the
**Limits** section. The three options set continuation limits per server process:

| Profile | Maximum sessions | Memory per query | Total continuation memory |
| --- | ---: | ---: | ---: |
| Low memory | 8 | 32 MiB | 128 MiB |
| Standard (default) | 16 | 64 MiB | 256 MiB |
| Higher capacity | 32 | 128 MiB | 512 MiB |

The dialog displays the selected limits as read-only details. Existing saved limits
that do not match a profile remain unchanged, with no profile selected. Choose a
profile before saving to replace them. There are no advanced numeric controls.

**Save limits** persists the values in application settings. **Restore defaults**
changes the draft; save it to persist the defaults. Closing discards unsaved edits.
Settings import/export includes these limits.

Restart the server through your MCP client after saving. Running processes retain
their startup limits; the status tool reports the limits actually in use. Each server
process has its own budget, and multiple clients may start separate processes.
Memory budgets may constrain capacity before the session limit is reached.
Continuation memory includes retained query state and working buffers; it is only
part of overall server memory usage. Idle expiry remains 15 minutes.

Older settings files without these values retain the defaults. The sidecar reads
settings without rewriting them. Invalid saved configuration prevents server
startup; correct the values in the desktop dialog and restart.

## Troubleshooting

### Resumable search and count

Search/count calls normally yield after 5 seconds of work or 256 MiB of new scan bytes,
retaining the existing 30-second default/maximum request deadline. Larger byte, file,
hit and response limits remain in place.
Admission/setup uses the same deadline and cancellation is cooperative. A slow
filesystem operation can exceed the normal slice duration. `stopReason` is `time_slice`,
`scan_budget`, `hit_limit`, `response_limit`, or `scope_exhausted`. These limits are reported
by `server_status`; callers cannot raise them. The character limit bounds retained content,
not complete JSON size or tokens; the hit cap bounds enumeration, not cumulative context
characters across pages. Long lines/context can still consume a large client context.

A count continuation that reaches its deadline before scanning begins returns a retryable
`deadline_exceeded` error without a `result`. Keep the last cumulative totals and retry the
submitted cursor; its committed progress is unchanged. Deadlines during scanning retain
committed progress in a partial result with a continuation when work remains. An initial
count request that times out before scanning still returns zero incomplete counts.

Follow `nextCursor` until null or absent. `isTraversalComplete` means every candidate was
visited or terminated with an explicit error; check `isQueryComplete` for search or
`isComplete` for count to establish exactness. A terminal result may remain inexact.
Search hit records and page counts describe this response, while overall counts are
cumulative. Search per-file counts describe this response's committed segment; a file's
`isCountExact` becomes true only after its frozen extent finishes without uncertainty.
Count totals, buckets and file records replace the previous response; never sum them.
The 200-per-file and 2,000-total hit caps apply per response. The separate 10,000-hit
query cap persists across every file and continuation in samples/matchesOnly. Optional
maxQueryHits can lower that cap; repeat it unchanged with a cursor. queryReturnedHitCount
is cumulative and maxQueryHits reports the effective query allowance. At the cap, a search
with unvisited work terminates with stopReason hit_limit, query_hit_limit incomplete and
truncation reasons, isPartial/isTruncated true, and no nextCursor. Both isQueryComplete and
isTraversalComplete remain false; absence of a cursor does not prove completion. Counts
remain observed partial counts. If the frozen scope is already known exhausted, normal
completion applies. Narrow the query or use countsOnly/count_logs to obtain complete totals.
The query hit cap does not apply to count-only modes. Context may overlap responses,
but forward traversal emits each matching line's hit once. Text/context truncation does not
invalidate completed counts. Per-call statistics describe committed segments, not snapshot
sizes or a physical I/O meter; unfinished lines can advance a cursor before contributing counts.

Each file's extent freezes when first opened, including its initial unfinished final line.
Appended bytes are excluded. Growth/write metadata drift permanently marks counts unverified;
replacement, truncation and detectable same-size edits terminate that file without restarting
it. This is not an atomic snapshot of all files. Unchanged metadata is best-effort evidence.
Search/count reject physical lines larger than 8 MiB on disk, including the line ending,
with `log_line_too_large`; prior results survive and other files proceed. Desktop search
and unfiltered tail limits are unchanged.

Continuations use small signed references to process-local state: 16 retained sessions,
15-minute idle expiry, 64 MiB retained state per session and 256 MiB of accounted retained/working
state per process. No state is persisted and no file handle survives a call. The catalog is
revalidated before advancement or replay. Retrying the immediately preceding input cursor
returns the same logical response; older revisions fail. Caller cancellation rolls back
uncommitted advancement. Completed initial calls retain no session because they expose no
continuation; terminal continuation replies remain replayable until expiry.

`invalid_search_cursor` / `invalid_count_cursor`
: Start again without a cursor after server restart or when a token is malformed or tampered.

`expired_search_cursor` / `expired_count_cursor`
: Restart the query after 15 minutes without use.

`stale_search_cursor` / `stale_count_cursor`
: Refresh the saved selection after catalog changes, or use the latest continuation revision.

`mismatched_search_cursor` / `mismatched_count_cursor`
: Repeat the same query and scope. Timeout and statistics are the only changeable options.

`continuation_capacity_exceeded`
: The optional `reason` field distinguishes capacity failures:

- `session_limit_exceeded`: Resume existing queries or wait for idle sessions to expire.
- `memory_budget_exceeded`: Retry after active work finishes or retained sessions expire.
- `query_too_large`: Restart with a narrower scope or less context; retrying the unchanged query cannot resolve this failure.

No unexpired session is silently evicted. The 16-session limit is independent of the
64 MiB per-session and 256 MiB process memory budgets; memory can prevent admission
before all session slots are occupied.

Restart the MCP client after upgrading to discover the search v6/count v3 contracts and the
maxQueryHits search argument.

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
