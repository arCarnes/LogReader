# MCP Performance and Mainline Measurements

Status: v1 first-class count and v2 many-file search release evidence

Measured: 2026-08-29

Artifact: Release, self-contained, single-file `win-x64` `WeezTail.Mcp.exe`, 69,177,545 bytes.

## Method

`packaging/scripts/Measure-McpLogServer.ps1` creates an isolated portable configuration, generates a dashboard of UTF-8 logs, copies the published `WeezTail.Mcp.exe` into that configuration, and drives the real stdio protocol. The release matrix keeps generated input near 21.5 MB while increasing configured-file count from 50 to the 2,000-candidate query ceiling. It records initialize, tree, cold/warm literal search, cold/warm unbucketed count, minute-bucketed count, cold/warm indexed line read, tail, cancellation gate release, shutdown, process memory, and stderr purity.

Measurement report schema version 3 also exhausts signed search cursors when the configured file count exceeds the 50-file page size and exercises one-call `count_logs` over the same candidate set. It records cursor page count/coverage, count exactness, bucket density, cumulative serialized response bytes, selected/searched/skipped/failed/remaining/matched counts, returned/matching-line/occurrence counts, completion and incomplete reasons, traversal bytes/files, elapsed scan time, and peak disk/UNC gate concurrency. These fields are numeric or categorical and contain no configured paths or log text.

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
