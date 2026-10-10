# MCP Performance and Mainline Measurements

Status: resumable search/count acceptance evidence, with historical release measurements

Measured: 2026-10-04 (historical profile/acceptance and release measurements below).

Artifact: validation-only Release, self-contained, single-file `win-x64` `WeezTail.Mcp.exe`, 69,321,333 bytes.

## Compact presentation acceptance — 2026-10-04

Wire envelope v4 defaults to sparse count tuples/boundary grids and shared retained
provenance; explicit dense/inline modes remain available. Backend schema v3,
search contract v6, count contract v3, scan limits, authorization, provenance budgets
and cursor identity are unchanged. Reconstruction tests cover ordinary and unaligned
minute/hour/day bounds, time-only midnight, repeated fall-back hours/minutes,
spring-forward gaps, 23-/25-hour days and final exclusive boundaries. Shared
provenance reconstructs inline output across all four tools, including errors,
truncation, empty arrays, omitted tail records and independently interpretable replays.

Controlled same-result fixtures measure the presentation change directly:

| Fixture / measured portion | Dense or inline bytes | Compact bytes | Saved |
| --- | ---: | ---: | ---: |
| 1,000 dated minute buckets, one occupied bucket; bucket array vs grid + tuples | 169,019 | 132 | 99.92% |
| 50-file response, two repeated provenance routes; full structured JSON | 24,649 | 10,124 | 14,525 bytes (58.93%) |

The published sidecar also ran a 100-file × 1,000-line synthetic fixture with 400
matching lines/occurrences. Both formats retained exact completion and 100 file
records. The minute grid contained 1,000 logical buckets; sparse emitted one tuple,
dense emitted 1,000 objects. These are received UTF-8 protocol frame bytes, including
both structured content and the JSON text fallback:

| Operation | Dense/inline | Sparse/shared | Saved |
| --- | ---: | ---: | ---: |
| countsOnly search | 107,629 | 53,241 | 54,388 bytes (50.53%) |
| Unbucketed count | 120,521 | 66,145 | 54,376 bytes (45.12%) |
| 1,000-bucket count | 426,849 | 66,745 | 360,104 bytes (84.36%) |

The other two format combinations, statistics opt-in, default one-bucket invocation,
and an all-zero 1,000-bucket run also passed. The all-zero response emitted no tuples
or provenance table while retaining its grid and exact completeness. Every run exited
0 with empty stderr. PowerShell 7 validated each tool result against the discovered
schema and checked text/structured parity; the harness reconciled bucket totals.
Report v8 records logical versus emitted bucket counts and retained versus emitted
provenance, rather than treating missing tuples/records as missing semantic evidence.

Shared initialization guidance is 360 characters. Tool descriptions shrank from
2,900 to 1,072 characters, and combined input schemas from 7,869 to 7,467 bytes.
The full tools/list result grew from 33,212 to 36,248 bytes (same compact JSON encoding)
because output schemas now describe both formats, strict tuples, shared references
and conditional requirements. This discovery cost is a tradeoff; it is not a
claim that every response or client session is smaller. Actual Claude Code/Codex
token usage remains unmeasured. These comparisons measure serialized bytes, not
token counts, scan throughput or concurrent-client fairness.

Validation: Core.Tests build and 115 focused tests passed; solution build and 720 Core
plus 1,034 Windows/integration tests passed. Cached NU1900 vulnerability-feed warnings
were the only build warnings. A validation-only Release self-contained single-file
win-x64 sidecar was published to `artifacts/publish/McpCompactFormatsValidation`;
the stdio smoke passed against it. No installed client or release was updated.

Successful report directories under `artifacts/measurements` (ignored local evidence):
`mcp-headless-100files-20261004-182856-516` (sparse/shared),
`mcp-headless-100files-20261004-182955-337` (dense/inline),
`mcp-headless-2files-20261004-182955-656` (dense/shared),
`mcp-headless-2files-20261004-182955-854` (sparse/inline with statistics),
`mcp-headless-2files-20261004-182956-095` (absent query), and
`mcp-headless-2files-20261004-182956-344` (defaults).

Reproduce from the LogReader product directory:
```powershell
./packaging/scripts/Measure-McpLogServer.ps1 -ExecutablePath ./artifacts/publish/McpCompactFormatsValidation/WeezTail.Mcp.exe -FileCount 100 -LinesPerFile 1000 -CountBucketCount 1000
./packaging/scripts/Measure-McpLogServer.ps1 -ExecutablePath ./artifacts/publish/McpCompactFormatsValidation/WeezTail.Mcp.exe -FileCount 100 -LinesPerFile 1000 -CountBucketCount 1000 -BucketMode dense -ProvenanceMode inline
```

The artifact size and performance profile measurements below are historical.

## Five-second slice follow-up — 2026-10-03

Following independent review, the current profile restores the five-second normal
search/count scan slice. It retains 256 MiB scan bytes, 200-file work units,
200 hits/file/response, 2,000 hits/response, 800,000 content characters/response,
the 30-second request deadline and the cumulative 10,000-hit text allowance.
The measurements below used twenty-second slices and have not been rerun for this
follow-up. They support the larger paging profile, not a measured fairness benefit
from restoring five seconds. A controlled five-versus-twenty-second comparison with
all other revised limits fixed, slow local/UNC storage and concurrent line/tail reads
remains deferred before reconsidering a longer normal slice.

Focused build/tests passed 231 tests. The solution build passed with NU1900
vulnerability-data lookup warnings. A full solution test rerun passed all 1,671 tests
after one intermittent WPF dashboard collection-mutation failure; the unchanged test
also passed in isolation and its full 96-test class passed. Portable publishing,
artifact validation and the real stdio smoke passed with a 5,000 ms scan slice and
all retained profile limits. No new performance or contention benchmark ran.

## Twenty-second profile acceptance — 2026-10-03

The measured profile used a 20-second normal scan slice, 256 MiB scan budget, 200-file
work units, 200 hits/file/response, 2,000 hits/response, and 800,000 content characters
per response. The default/maximum request deadline remains 30 seconds. Text searches
stopped after 10,000 cumulative emitted hits with explicit incomplete/truncated evidence;
count-only modes could complete. No token or cumulative character bound is promised.
Published artifact: Release, self-contained win-x64 WeezTail.Mcp.exe, 69,304,508 bytes.
The large fixture committed all 2,171,514,000 bytes and returned 2,000 unique hit records
with one context line per side. The 2,000-file fixture committed all 21,764,000 bytes;
countsOnly search, count, and minute buckets returned exact 2,000-event totals.

| Fixture/search mode | Search cold / warm ms | Search pages cold / warm | Count cold / warm ms | Count pages cold / warm |
| --- | ---: | ---: | ---: | ---: |
| 2.17 GB samples/context | 3,747 / 3,075 | 11 / 11 | 1,248 / 1,207 | 9 / 9 |
| 2,000 files countsOnly | 9,178 / 1,091 | 10 / 10 | 1,113 / 1,042 | 1 / 1 |
| 60 files, capped matchesOnly | 2,967 / 1,837 | 5 / 5 | 1,708 / 1,692 | 1 / 1 |

The cap fixture contained 12,000 matches across 60 files and 129,084,000 bytes.
Cold and warm text traversals returned the same first 10,000 unique reference hits in
five responses, with query_hit_limit, partial/truncated flags, incomplete traversal,
and no continuation. Unbucketed and minute-bucketed count traversals returned exact
12,000-event totals. A separate published stdio probe verified a lower maxQueryHits
of three, cumulative allowance, continuation replay, and terminal replay.

Maximum measured large-file sample response latency was 649 ms cold / 321 ms warm;
maximum full reserialized protocol-response size was 1,652,517 bytes. The content
character budget does not include JSON framing or the protocol's repeated text and
structured content. Maximum measured 2,000-file countsOnly search response latency
was 1,021 ms cold / 125 ms warm. Minute-bucketed counts took 2,182 ms for the large
fixture and 1,847 ms for the 2,000-file fixture.

All three runs exited successfully with empty stderr. Cancellation probes answered
in 1.24 ms (large file), 1.83 ms (2,000 files), and 1.67 ms (cap fixture) after cancellation.
Peak sampled process working set reached 207 MiB in the large-file run and 439 MiB
in the 2,000-file run. These figures include runtime/GC/protocol allocations; the
unchanged 256 MiB continuation accounting ceiling is not a process working-set bound.
Larger response defaults permit larger output and can increase process memory.
These runs are acceptance evidence, not controlled before/after benchmarks.

Focused build/tests passed 231 tests; the solution build and 1,671 tests passed
(639 Core, 1,032 WPF/integration). The solution build reported NU1900 warnings because
NuGet vulnerability data could not be fetched; there were no compile errors.
Portable validation and stdio smoke checked tool discovery and the revised status limits.

Reproduce from the LogReader product directory:
```powershell
./packaging/scripts/Measure-McpLogServer.ps1 -FileCount 1 -LinesPerFile 500000 -PaddingCharactersPerLine 4300 -SearchResultMode samples -SearchContextLines 1 -IncludeStatistics
./packaging/scripts/Measure-McpLogServer.ps1 -FileCount 2000 -LinesPerFile 250 -IncludeStatistics
./packaging/scripts/Measure-McpLogServer.ps1 -FileCount 60 -LinesPerFile 50000 -SearchResultMode matchesOnly -IncludeStatistics
```

Historical measurements retain their original limits and contract versions.

## Resumable search/count acceptance — 2026-10-01

The published stdio server exhausted a 2,171,514,000-byte fixture with 500,000 lines and 2,000 known matching lines. Samples included one context line on each side. Every traversal committed exactly the initial byte extent; text search returned all 2,000 hit coordinates once. Counts and minute buckets agreed with the generated reference. No continuation rebuilt a full-file context index.

| Operation | Total cold / warm ms | Pages | Slowest cold / warm response ms | Maximum cursor characters |
| --- | ---: | ---: | ---: | ---: |
| Samples with context | 4,197 / 3,378 | 42 | 235 / 103 | 173 |
| Count | 1,482 / 1,435 | 33 | 75 / 52 | 173 |
| Minute-bucketed count | 2,689 | 33 | 140 | 173 |

The final 2,000-file fixture contained 21,764,000 bytes and 2,000 events. Search completed in 40 pages: 10,283 ms cold and 1,483 ms warm, with maximum responses of 350 ms and 59 ms. Count completed in one response, 1,511 ms cold and 1,086 ms warm; bucketed count took 2,101 ms. All final counts were exact, with no failed or remaining files. An earlier run exposed repeated walks over completed-file records; saving the reducer position and pooling read buffers repaired that regression.

Both final runs exited successfully with empty stderr. Cancellation probes released in 1.35 ms (large file) and 1.28 ms (many files). Peak sampled process working set was 196 MiB and 287 MiB respectively, including runtime, GC and shared pages. These are not continuation-state measurements: accounted retained state is capped at 64 MiB per session and 256 MiB across the process, and working-copy/scratch admission is reserved separately against that process capacity. Deterministic tests cover capacity rejection and committed-state transfer; a process working-set ceiling is not promised.

Reports: `artifacts/measurements/mcp-headless-1files-20261001-231927-441/measurement.json` and `artifacts/measurements/mcp-headless-2000files-20261001-231821-825/measurement.json`. Reports retain each call's latency, committed-byte progress, stop reason, cursor size and memory sample. The harness rejects duplicate/missing text hits and count/byte reconciliation failures. These are local Windows measurements, not a guarantee for slow or unresponsive filesystems.

Reproduce from the `LogReader` directory after `packaging/scripts/Publish-Portable.ps1`:

```powershell
./packaging/scripts/Measure-McpLogServer.ps1 -FileCount 1 -LinesPerFile 500000 -PaddingCharactersPerLine 4300 -SearchResultMode samples -SearchContextLines 1 -IncludeStatistics
./packaging/scripts/Measure-McpLogServer.ps1 -FileCount 2000 -LinesPerFile 250 -IncludeStatistics
```

Final solution validation passed 1,610 tests (593 Core and 1,017 WPF/integration), with a clean build. Portable publishing validated the directory and the real six-tool stdio smoke. Historical tables and conclusions below describe their original contract versions; their large self-contained cursor sizes and one-call counts have been superseded by this release.

## Method

`packaging/scripts/Measure-McpLogServer.ps1` creates an isolated portable configuration, generates a dashboard of UTF-8 logs, copies the published `WeezTail.Mcp.exe` into that configuration, and drives the real stdio protocol. The release matrix keeps generated input near 21.5 MB while increasing configured-file count from 50 to the 2,000-candidate query ceiling. It records initialize, tree, cold/warm literal search, cold/warm unbucketed count, minute-bucketed count, cold/warm indexed line read, tail, cancellation gate release, shutdown, process memory, and stderr purity.

Measurement report schema version 7 exhausts search and count continuations and records per-slice latency, stop reason, cursor length, committed logical bytes and process memory. Text modes verify unique hits against the generated stable reference prefix and accept explicit terminal query_hit_limit results at the cumulative hit ceiling; count-only totals still reconcile the complete reference. The client transport wait adds five seconds of response grace to the unchanged 30-second server deadline. PaddingCharactersPerLine supports multi-gigabyte fixtures; SearchResultMode and SearchContextLines exercise text/context paging. Historical schema version 5 used one-call count measurements. It also records filtered and unfiltered initial, idle, nonmatching append, and matching append tail calls when single-file authorization succeeds, including compact reserialized structured-content bytes and actual full protocol response bytes. These byte counts are payload measurements, not client token counts. The report contains no configured paths or returned log text.

The 2026-09-22 compact-tail comparison used the same 50-file, 100-line fixture and exact pre-change `HEAD` source for the baseline. Each row shows structured-content bytes / full protocol bytes before → after:

| Tail call | Before → after | Match rate |
| --- | ---: | ---: |
| Unfiltered idle | 1,079 / 2,625 → 256 / 719 | — |
| Filtered idle | 1,201 / 2,899 → 256 / 719 | — |
| Filtered nonmatching append | 1,201 / 2,899 → 776 / 1,839 | 0/1 |
| Filtered matching append | 1,282 / 3,101 → 1,297 / 3,141 | 1/3 |

The added `isIdle: false` field costs 40 full protocol bytes on the matching append. Single-call timings were 3.66 → 3.45 ms for unfiltered idle, 1.75 → 1.38 ms for filtered idle, and 1.73 → 1.55 ms for the filtered nonmatching append; these samples do not establish a timing improvement. Byte sizes are a proxy for client token use, not token counts.

These are representative point measurements on the development Windows machine, not universal latency guarantees. Files were local. Working set includes shared executable/runtime pages and varies with OS trimming; private bytes are the more useful per-process comparison. Generated logs and full JSON reports remain under ignored `artifacts/measurements` directories.

Reproduce after publishing:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\packaging\scripts\Measure-McpLogServer.ps1 -FileCount 1 -LinesPerFile 10000
powershell -NoProfile -ExecutionPolicy Bypass -File .\packaging\scripts\Measure-McpLogServer.ps1 -FileCount 50 -LinesPerFile 10000
powershell -NoProfile -ExecutionPolicy Bypass -File .\packaging\scripts\Measure-McpLogServer.ps1 -FileCount 100 -LinesPerFile 5000
powershell -NoProfile -ExecutionPolicy Bypass -File .\packaging\scripts\Measure-McpLogServer.ps1 -FileCount 500 -LinesPerFile 1000
powershell -NoProfile -ExecutionPolicy Bypass -File .\packaging\scripts\Measure-McpLogServer.ps1 -FileCount 1000 -LinesPerFile 500
powershell -NoProfile -ExecutionPolicy Bypass -File .\packaging\scripts\Measure-McpLogServer.ps1 -FileCount 2000 -LinesPerFile 250
```

## Headless results

| Configured logs | Lines / file | Pages | Cold / warm search | Total / max-page response | Max cursor | Final private | Cancel-to-gate-release |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 50 | 10,000 | 1 | 395 / 136 ms | 64,719 / 64,719 B | 0 chars | 75.4 MB | 1.7 ms |
| 100 | 5,000 | 2 | 647 / 169 ms | 135,774 / 71,149 B | 3,223 chars | 82.1 MB | 1.9 ms |
| 500 | 1,000 | 10 | 1,771 / 236 ms | 870,980 / 108,289 B | 21,894 chars | 128.1 MB | 22.1 ms |
| 1,000 | 500 | 20 | 3,199 / 376 ms | 2,208,548 / 154,857 B | 45,227 chars | 293.8 MB | 4.3 ms |
| 2,000 | 250 | 40 | 6,321 / 635 ms | 6,289,666 / 248,397 B | 91,898 chars | 208.0 MB | 4.6 ms |

### First-class count results

| Configured logs | Lines / file | Cold / warm count | Minute-bucketed count | Unbucketed / bucketed response | Exact and complete |
|---:|---:|---:|---:|---:|:---:|
| 50 | 10,000 | 135 / 88 ms | 384 ms | 58,465 / 58,967 B | yes |
| 100 | 5,000 | 172 / 135 ms | 404 ms | 113,779 / 114,281 B | yes |
| 500 | 1,000 | 257 / 163 ms | 627 ms | 528,193 / 528,695 B | yes |
| 1,000 | 500 | 332 / 309 ms | 646 ms | 838,207 / 838,709 B | yes |
| 2,000 | 250 | 557 / 545 ms | 887 ms | 1,462,207 / 1,462,085 B | yes |

Every count evaluated its complete candidate set internally, returned no incomplete reasons, and reconciled 2,000 matching lines and 2,000 occurrences across its overall, per-file, and bucket totals. The generator places one known event every 250 lines, giving every scale shape the same total. The minute-bucketed runs returned one dense bucket and remained exact. At 500 files and above, the envelope reported metadata truncation while keeping every numeric total exact; the configured character budget bounds string content rather than JSON property/framing overhead.

Every search page contained exactly 50 files, except that no final remainder was needed in this matrix. Final cumulative statistics were respectively `50/50/0/0/0`, `100/100/0/0/0`, `500/500/0/0/0`, `1000/1000/0/0/0`, and `2000/2000/0/0/0` for selected/searched/skipped/failed/remaining. Every final result was query-complete with no incomplete reasons, evaluated approximately 21.5 MB, emitted no stderr, and exited successfully.

The 1,000-file tree probe was intentionally response-truncated at its 500-node bound. Single-file line and tail probes above 50 configured files were reported partial with `log_access_denied` because those separate operations retain the existing configured-selection authorization bound. These expected probe results do not affect the completed paged-search release gate; the harness fails on any partial/truncated status or search result.

## Interpretation

### Search excerpt layout evidence (2026-09-22)

A representative Codex `samples` response containing 12 clustered hits repeated 168 hit/context line objects for 39 unique physical lines. Re-encoding only that response's hit/context portion as contract 4 compact `hits` plus merged `excerpts`, while leaving the surrounding envelope and file metadata in place, reduced compact UTF-8 JSON from 79,503 to 33,742 bytes: 45,761 bytes (57.6%) smaller. This comparison measures serialized bytes from one supplied response, not client token consumption. Automated coverage separately verifies overlap merging, disjoint excerpts, long-line match coordinates, page-wide hit priority, balanced context selection, truncation, failures, and text/structured-content parity.

- The maximum representative local scan stayed well inside the 30-second deadline. Search covered 2,000 authorized candidates through 40 signed pages without skips or failures, while `count_logs` evaluated the same 21.76 MB scope and 2,000 known events in one call in 557 ms cold and 545 ms warm; minute bucketing completed in 887 ms.
- Cursor state grew with visited-file identities but remained below its 100,000-character decoder bound at the 2,000-candidate release gate (91,898 characters maximum). The 200,000-character response limit budgets retained log/provenance string content rather than total JSON framing or the opaque cursor; the largest serialized page was 248,397 bytes.
- Warm line reads demonstrate the value of retaining a bounded process-local line index.
- Cancellation released the relevant operation gate in milliseconds in these runs.
- The dedicated MCP executable is 95.7 MB smaller than the prior combined 164.7 MB executable, and the desktop executable no longer carries the MCP SDK. Shipping two self-contained single-file executables increases the combined unpacked executable payload to 232.6 MB; this is the accepted packaging cost of the maintainable sidecar boundary.
- The current 50-file final private-memory point was 113.0 MB versus 88.1 MB in an earlier run of the same headless backend. Working-set and private-byte points vary enough that this is not evidence of a sidecar regression by itself; request latency and resource caps remain the more stable gates.
- Search remains bounded sequential I/O; WeezTail does not claim indexed arbitrary-text search.
- Each configured MCP client owns these resources independently. Several clients can duplicate memory and I/O, which is the accepted cost of avoiding a shared service and UI coupling in v1.

## Decisions supported by the evidence

- Keep the dedicated sidecar design. The WPF application does not reference or construct the MCP host.
- Keep the 2,000-candidate query ceiling, 50-file per-page limit, two disk operations, one UNC operation, four indexed sessions, 2,000,000 offsets, 30-second deadline, and 200,000 response-character content bound. Traverse the supported candidate set with signed continuation rather than a larger I/O work unit.
- Do not add live-UI index sharing or a daemon in v1. Either option adds authentication, discovery, crash, update, cache ownership, and concurrency responsibilities. Revisit only if common multi-client use shows unacceptable duplication.
- Keep client processes short-lived when practical; stdin closure provides deterministic cancellation and cleanup.

## Coverage beyond the benchmark

Automated stress coverage exercises exclusive file locks, missing/reappearing files, rapid append, truncation, replacement/rotation, multi-megabyte unterminated lines, invalid UTF-8/UTF-16 detection, response and index capacity, simulated slow/serialized UNC work, concurrent persisted-config replacement, deterministic ordering/serialization, cancellation through MCP/backend/cache layers, stdout purity, and slow physical reads after index-lock release. See [MCP Security and Resilience Model](./McpSecurityModel.md) for the threat and residual-risk record.
