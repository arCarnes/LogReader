# MCP Performance and Mainline Measurements

Status: v1 release evidence

Measured: 2026-08-05

Artifact: Release, self-contained, single-file `win-x64` `WeezTail.Mcp.exe`, version 0.16.8, 69,050,569 bytes. The companion `WeezTail.exe` is 163,586,167 bytes; the combined unpacked executable payload is 232,636,736 bytes.

## Method

`packaging/scripts/Measure-McpLogServer.ps1` creates an isolated portable configuration, generates a dashboard containing 1, 10, or 50 UTF-8 logs with 10,000 lines each, copies the published `WeezTail.Mcp.exe` into that configuration, and drives the real stdio protocol. It records initialize, tree, cold/warm literal search, cold/warm indexed line read, tail, cancellation gate release, shutdown, process memory, and stderr purity.

These are representative point measurements on the development Windows machine, not universal latency guarantees. Files were local. Working set includes shared executable/runtime pages and varies with OS trimming; private bytes are the more useful per-process comparison. Generated logs and full JSON reports remain under ignored `artifacts/measurements` directories.

Reproduce after publishing:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\packaging\scripts\Measure-McpLogServer.ps1 -FileCount 1 -LinesPerFile 10000
powershell -NoProfile -ExecutionPolicy Bypass -File .\packaging\scripts\Measure-McpLogServer.ps1 -FileCount 10 -LinesPerFile 10000
powershell -NoProfile -ExecutionPolicy Bypass -File .\packaging\scripts\Measure-McpLogServer.ps1 -FileCount 50 -LinesPerFile 10000
```

## Headless results

| Configured logs | Input bytes | Initialize | Cold / warm search | Cold / warm line read | Cancel-to-gate-release | Shutdown | Final private | Peak working set |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | 0.43 MB | 1,000 ms | 48 / 8 ms | 41 / 4 ms | 1.5 ms | 13 ms | 26.4 MB | 70.1 MB |
| 10 | 4.30 MB | 988 ms | 156 / 34 ms | 41 / 4 ms | 1.5 ms | 14 ms | 40.0 MB | 83.9 MB |
| 50 | 21.50 MB | 987 ms | 678 / 105 ms | 41 / 4 ms | 2.4 ms | 16 ms | 113.0 MB | 155.4 MB |

All operations completed without partial/truncated errors, stdout contamination, or stderr output. The measurement script now fails rather than recording a successful run if any probe is unexpectedly partial or truncated. Searches intentionally do not build line indexes: the index stores line offsets, not search terms. The retained indexed session belongs to the line/tail probe and contains 10,000 offsets, within the four-session process cap.

## Interpretation

- The maximum representative local scan stayed well inside the 30-second deadline.
- Warm line reads demonstrate the value of retaining a bounded process-local line index.
- Cancellation released the relevant operation gate in milliseconds in these runs.
- The dedicated MCP executable is 95.7 MB smaller than the prior combined 164.7 MB executable, and the desktop executable no longer carries the MCP SDK. Shipping two self-contained single-file executables increases the combined unpacked executable payload to 232.6 MB; this is the accepted packaging cost of the maintainable sidecar boundary.
- The current 50-file final private-memory point was 113.0 MB versus 88.1 MB in an earlier run of the same headless backend. Working-set and private-byte points vary enough that this is not evidence of a sidecar regression by itself; request latency and resource caps remain the more stable gates.
- Search remains bounded sequential I/O; WeezTail does not claim indexed arbitrary-text search.
- Each configured MCP client owns these resources independently. Several clients can duplicate memory and I/O, which is the accepted cost of avoiding a shared service and UI coupling in v1.

## Decisions supported by the evidence

- Keep the dedicated sidecar design. The WPF application does not reference or construct the MCP host.
- Keep the current limits: 50 files, two disk operations, one UNC operation, four indexed sessions, 2,000,000 offsets, 30 seconds, and 200,000 response characters.
- Do not add live-UI index sharing or a daemon in v1. Either option adds authentication, discovery, crash, update, cache ownership, and concurrency responsibilities. Revisit only if common multi-client use shows unacceptable duplication.
- Keep client processes short-lived when practical; stdin closure provides deterministic cancellation and cleanup.

## Coverage beyond the benchmark

Automated stress coverage exercises exclusive file locks, missing/reappearing files, rapid append, truncation, replacement/rotation, multi-megabyte unterminated lines, invalid UTF-8/UTF-16 detection, response and index capacity, simulated slow/serialized UNC work, concurrent persisted-config replacement, deterministic ordering/serialization, cancellation through MCP/backend/cache layers, stdout purity, and slow physical reads after index-lock release. See [MCP Security and Resilience Model](./McpSecurityModel.md) for the threat and residual-risk record.
