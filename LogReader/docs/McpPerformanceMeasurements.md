# MCP Performance and Mainline Measurements

Status: v2 many-file search release evidence

Measured: 2026-08-26

Artifact: Release, self-contained, single-file `win-x64` `WeezTail.Mcp.exe`, 69,103,305 bytes.

## Method

`packaging/scripts/Measure-McpLogServer.ps1` creates an isolated portable configuration, generates a dashboard of UTF-8 logs, copies the published `WeezTail.Mcp.exe` into that configuration, and drives the real stdio protocol. The release matrix keeps generated input near 21.5 MB while increasing configured-file count from 50 to the 2,000-candidate query ceiling. It records initialize, tree, cold/warm literal search, cold/warm indexed line read, tail, cancellation gate release, shutdown, process memory, and stderr purity.

Measurement report schema version 2 also exhausts signed search cursors when the configured file count exceeds the 50-file page size. It records cursor page count/coverage, cumulative serialized response bytes, the search contract's selected/searched/skipped/failed/remaining/matched counts, returned/matching-line/occurrence counts, page/query completion, incomplete reasons, traversal bytes/files, elapsed scan time, and peak disk/UNC gate concurrency. These fields are numeric or categorical and contain no configured paths or log text.

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
| 50 | 10,000 | 1 | 416 / 120 ms | 64,589 / 64,589 B | 0 chars | 54.1 MB | 1.5 ms |
| 100 | 5,000 | 2 | 708 / 162 ms | 135,108 / 70,813 B | 3,220 chars | 45.7 MB | 1.5 ms |
| 500 | 1,000 | 10 | 1,795 / 246 ms | 869,662 / 108,153 B | 21,891 chars | 89.6 MB | 4.4 ms |
| 1,000 | 500 | 20 | 3,384 / 336 ms | 2,205,310 / 154,689 B | 45,216 chars | 92.4 MB | 3.9 ms |
| 2,000 | 250 | 40 | 6,321 / 646 ms | 6,283,250 / 248,229 B | 91,886 chars | 208.9 MB | 3.8 ms |

Every search page contained exactly 50 files, except that no final remainder was needed in this matrix. Final cumulative statistics were respectively `50/50/0/0/0`, `100/100/0/0/0`, `500/500/0/0/0`, `1000/1000/0/0/0`, and `2000/2000/0/0/0` for selected/searched/skipped/failed/remaining. Every final result was query-complete with no incomplete reasons, evaluated approximately 21.5 MB, emitted no stderr, and exited successfully.

The 1,000-file tree probe was intentionally response-truncated at its 500-node bound. Single-file line and tail probes above 50 configured files were reported partial with `log_access_denied` because those separate operations retain the existing configured-selection authorization bound. These expected probe results do not affect the completed paged-search release gate; the harness fails on any partial/truncated status or search result.

## Interpretation

- The maximum representative local scan stayed well inside the 30-second deadline and covered 2,000 authorized candidates through 40 signed pages without skips or failures.
- Cursor state grew with visited-file identities but remained below its 100,000-character decoder bound at the 2,000-candidate release gate (91,886 characters maximum). The 200,000-character response limit budgets retained log/provenance string content rather than total JSON framing or the opaque cursor; the largest serialized page was 248,229 bytes.
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
