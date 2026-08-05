# MCP Performance and Mainline Measurements

Status: v1 release evidence  
Measured: 2026-08-05  
Artifact: Release, self-contained, single-file `win-x64`, version 0.16.8, 164,800,703 bytes

## Method

`packaging/scripts/Measure-McpLogServer.ps1` creates an isolated portable configuration, generates a dashboard containing 1, 10, or 50 UTF-8 logs with 10,000 lines each, copies the published executable into that configuration, and drives the real stdio protocol. It records initialize, tree, cold/warm literal search, cold/warm indexed line read, tail, cancellation gate release, shutdown, process memory, backend choice, and stderr purity. With `-UseLiveUi`, it starts the same isolated executable as WPF and verifies every request remains on `liveUi`. `-AdditionalIdleClients 2` exercises the supported maximum of three connected MCP processes while two heavy searches are queued.

These are representative point measurements on the development Windows machine, not universal latency guarantees. Files were local. Working set includes shared executable/runtime pages and varies with OS trimming; private bytes are the more useful per-process comparison. The report is captured immediately after requests, so post-GC values may be lower. Generated logs and full JSON reports remain under ignored `artifacts/measurements` directories.

Reproduce after publishing:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\packaging\scripts\Measure-McpLogServer.ps1 -FileCount 1 -LinesPerFile 10000
powershell -NoProfile -ExecutionPolicy Bypass -File .\packaging\scripts\Measure-McpLogServer.ps1 -FileCount 10 -LinesPerFile 10000
powershell -NoProfile -ExecutionPolicy Bypass -File .\packaging\scripts\Measure-McpLogServer.ps1 -FileCount 50 -LinesPerFile 10000
powershell -NoProfile -ExecutionPolicy Bypass -File .\packaging\scripts\Measure-McpLogServer.ps1 -FileCount 50 -LinesPerFile 10000 -UseLiveUi -AdditionalIdleClients 2
```

## Headless results

| Configured logs | Input bytes | Initialize | Cold / warm search | Cold / warm line read | Cancel-to-gate-release | Shutdown | Final private | Peak working set |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | 0.43 MB | 953 ms | 48 / 8 ms | 40 / 5 ms | 1.5 ms | 14 ms | 28.9 MB | 77.0 MB |
| 10 | 4.30 MB | 950 ms | 168 / 23 ms | 40 / 4 ms | 2.9 ms | 16 ms | 55.8 MB | 105.0 MB |
| 50 | 21.50 MB | 970 ms | 681 / 104 ms | 40 / 4 ms | 4.0 ms | 18 ms | 115.4 MB | 163.3 MB |

All operations completed without partial/truncated errors, stdout contamination, or stderr output. Searches intentionally do not build line indexes: WeezTail's index stores line offsets, not search terms. The indexed session shown after each run belongs only to the line/tail probe and contains 10,000 offsets. The headless process retained at most one of its four allowed warm sessions in these runs.

## Running-UI results

The corrected packaged live run stayed on `liveUi` for status, tree, both searches, both line reads, tail, cancellation, and the post-cancel probe. It did not create a retained agent-only UI session after the request; `server_status` reported zero active/retained sessions and zero mapped offsets because unopened-file agent leases have zero warm retention in the UI process.

| Configured logs | Cold / warm search | Cold / warm line read | Tail | Cancel-to-gate-release | Final MCP private | UI working set after requests |
|---:|---:|---:|---:|---:|---:|---:|
| 10 | 196 / 58 ms | 55 / 10 ms | 24 ms | 6.7 ms | 21.8 MB | 186.3 MB |
| 50 | 796 / 176 ms | 57 / 11 ms | 23 ms | 6.8 ms | 32.7 MB | 201.1 MB |

The live path has pipe/serialization overhead, so it is not expected to beat a warm same-process headless call on small local files. Its purpose is ownership and memory safety: when the UI already owns an open file's session/index, tests prove the agent lease reuses that exact index without another build. When a file is not open, any cold agent index is bounded and evicted immediately rather than altering the UI's two-minute tab retention policy. Physical reads use a bounded heap snapshot after releasing the index lock.

For the 50-file scan, the MCP process used about 33 MB private memory with a live UI instead of about 115 MB in the standalone headless run. The scan allocations move into the already-running UI process, whose immediate post-request working set was about 47 MB above the settled no-client UI baseline. This is not a free memory reduction; it avoids a second persistent index/session owner and permits reuse when the UI already has the file open.

## Three-client result

With the UI and the supported maximum of three MCP processes connected, two no-match 50-file searches were queued behind the single shared heavy-operation gate. A status call through the separate light lane returned from `liveUi` in 1.86 ms. Cancelling both queued/active searches drained them and returned both clients to usable `liveUi` status in 5.86 ms.

The primary MCP process retained 29.1 MB private; the two otherwise idle/queued clients retained 16.6 MB each. All exited with code 0 and empty stderr after stdin closed. Immediate post-request UI working set/private bytes were 200.5/157.4 MB. The result confirms that three client connections do not multiply heavy UI scans: one runs, the others wait, interactive work retains priority, and light endpoint health checks remain responsive.

## UI and package baseline

- Warm median time-to-window: 408.5 ms pre-MCP vs 415.8 ms final (+7.3 ms, +1.8%). The final cold observation was faster than the retained baseline cold observation.
- Settled no-client UI: about +1.6-2.0 MB working set, +21-23 handles, +2 threads, and 0 ms sampled sustained idle CPU versus baseline.
- One/three idle live clients: UI working set rose by about 5.2/5.8 MB; sampled UI/client idle CPU remained 0 ms. Each idle MCP process used about 15.7-16.6 MB private.
- Executable: 164,800,703 bytes, +1,461,320 (+0.895%). Portable zip: 66,595,939 bytes, +331,971 (+0.501%). MSI: 57,864,192 bytes, +233,472 (+0.405%).

## Decisions supported by the evidence

- Keep the one-executable design. No-client startup, idle, and package changes are small and bounded.
- Keep optional live reuse with headless fallback. The server neither requires nor launches WPF; live reuse is an optimization when the matching UI/storage instance exists.
- Keep the current limits: 50 files, one live/two headless disk operations, one UNC operation, four indexed sessions, 2,000,000 offsets, 30 seconds, 200,000 response characters, and three live clients. The maximum representative local scan stayed well inside the deadline and cancellation/shutdown budgets without silent dropping.
- Do not add a daemon in v1. The normal case is one client, the live UI already provides shared ownership when available, and a persistent host would add discovery, crash, update, security, and support lifecycle costs. Revisit only if UI-absent multi-agent use becomes common and measured private-memory duplication is unacceptable.
- Do not claim indexed arbitrary search. Search remains bounded sequential I/O; the shared index accelerates line/context/tail addressing.

## Coverage beyond the benchmark

Automated stress coverage exercises exclusive file locks, missing/reappearing files, rapid append, truncation, replacement/rotation, multi-megabyte unterminated lines, invalid UTF-8/UTF-16 detection, response and index capacity, simulated slow/serialized UNC work, disconnects, UI loss/handoff, concurrent persisted-config replacement, deterministic ordering/serialization, cancellation at MCP/IPC/backend/cache layers, stdout purity, UI preemption, and slow physical reads after index-lock release. See [MCP Security and Resilience Model](./McpSecurityModel.md) for the threat and residual-risk record.
