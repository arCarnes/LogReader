# MCP tool consistency validation and measurements

Measured 2026-11-02, America/New_York. These are local validation artifacts, not a release.

The [machine-readable summary](McpToolConsistencyMeasurements.json) records artifact hashes, per-scenario progress/bytes/latency, memory, legacy comparisons and raw report locations. Raw reports remain in ignored local `artifacts/measurements` folders. All six reports match the final published Infrastructure assembly hash `8AF10D4ADE69C34F9C88812FD8ECAA61A706EE4634ACD88B849FF9999F95DDAB`.

## Method and limits

Fresh Release framework-dependent `net10.0` sidecar output was copied into isolated portable fixtures. Each physical file initially held 250 lines and one known match. A normalized path alias using `\.\` and overlapping folder/dashboard/file targets exercised physical-path deduplication and provenance. The supported 100-file cap also passed with 300 candidates aliasing 100 paths across resolver pages; a separate regression proves rejection of 101 distinct files.

Real UNC fixtures used the user-authorized existing test directory and dedicated run subdirectories beneath it. Only that exact root was inspected; its parent and sibling directories were not enumerated. Synthetic run folders are retained for inspection. The committed summary omits machine-specific storage paths.

PowerShell 7 validated every measured result against its discovered output schema and checked structured/text parity. These single runs include ordinary machine scheduling noise and overlapping validation; they are not percentile benchmarks, isolated storage throughput measurements or client token estimates. Initialization captures one end per file, not a simultaneous filesystem snapshot. Scan-byte counters exclude encoding/baseline probes.

## Monitor results

Each idle/nonmatching/matching row is one complete scope round. Defaults check at most ten files per poll. Replay is recorded separately and is not applied twice.

| Storage | Files | Init ms | Idle polls / checked files | Idle ms | Nonmatch ms / scan bytes | Match ms / distinct events | Match protocol bytes | Max process working set MiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| local | 1 | 18.56 | 1 / 1 | 5.77 | 7.95 / 14 | 13.12 / 1 | 5,231 | 73.43 |
| local | 10 | 25.24 | 1 / 10 | 9.43 | 47.18 / 140 | 47.41 / 10 | 12,873 | 80.52 |
| local | 100 | 93.24 | 10 / 100 | 76.05 | 463.86 / 1,400 | 460.48 / 100 | 119,141 | 98.99 |
| unc | 1 | 26.12 | 1 / 1 | 10.94 | 33.52 / 14 | 33.91 / 1 | 5,242 | 73.09 |
| unc | 10 | 93.21 | 1 / 10 | 58.34 | 316.26 / 140 | 325.29 / 10 | 12,873 | 86.86 |
| unc | 100 | 1628.18 | 10 / 100 | 1051.31 | 1554.65 / 1,400 | 3132.80 / 100 | 119,152 | 104.87 |

Both 100-file idle rounds checked exactly 100 files in ten polls, read zero scan bytes and ended idle/caught-up. Nonmatching appends advanced every file without reporting idle. Matching appends yielded exactly 100 distinct file/generation/offset identities per storage kind. Wire bytes include full JSON-RPC responses with text and structured copies. Memory is the observed whole sidecar working set after prior query/read measurements, not solely monitor state or a guaranteed heap cap.

## Fairness, boundaries and recovery

| Storage | Files | Hot/quiet first round ms | Scan bytes | Quiet events with hot backlog | Further drain rounds / ms | Handoff ms / events | Release ms | Clean shutdown ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| local | 1 | n/a | n/a | n/a | n/a | 2.98 / 1 | 1.48 | 13.54 |
| local | 10 | 15.07 | 262,165 | 1 | 2 / 13.72 | 3.21 / 1 | 1.86 | 13.56 |
| local | 100 | 68.06 | 262,165 | 1 | 2 / 119.49 | 2.74 / 1 | 1.46 | 13.73 |
| unc | 1 | n/a | n/a | n/a | n/a | 17.64 / 1 | 1.15 | 15.27 |
| unc | 10 | 166.72 | 262,165 | 1 | 2 / 119.04 | 8.55 / 1 | 1.24 | 14.36 |
| unc | 100 | 1596.83 | 262,165 | 1 | 2 / 1027.30 | 6.07 / 1 | 0.72 | 15.61 |

A 600,000-character nonmatching hot line consumed only its per-file slice. The quiet file delivered its match during that scope round while hot-file backlog remained; two further rounds drained it. Immediate-previous-cursor replay returned the identical committed envelope, with no second event application.

The end-to-end source returned one hit per bounded response, froze its file extent, then received a controlled append. Continuing that same investigation returned exactly its two original matches and terminal `file_changed_during_search` uncertainty. Explicit handoff delivered exactly the post-extent append. No snapshot refresh or fallback scan occurred, and both observations were released.

Cold/warm search and count returned exact stable totals of 1/10/100 for both storage kinds. Legacy single-file unfiltered/filtered initial, idle, nonmatching and matching tail calls passed schema/content checks. Query statistics observed peak UNC concurrency 1 on the real share and 0 for local fixtures. Full compatibility tests cover every supported legacy result mode.

## Cancellation and network interpretation

The cancellation notification/liveness probes completed in 2.47 ms (local, 1 files), 2.49 ms (local, 10 files), 2.00 ms (local, 100 files), 1.45 ms (unc, 1 files), 1.36 ms (unc, 10 files), 0.98 ms (unc, 100 files). This transport measurement does **not** prove heavy-work admission. Reports explicitly record `WorkAdmissionVerified=false`; no startup sleep is used. Readiness-gated backend tests prove cancellation rollback, cursor reuse and owned-work draining separately. All measured processes exited 0 with empty stderr after stdin closed.

Controlled UNC contention uses a fake scanner and explicit readiness/release gates to verify shared UNC scheduling and concurrent local observation. The six measurements above separately exercise actual local/SMB files. Neither proves behavior on every share or a hard latency bound for unresponsive filesystem operations; deadlines are cooperative. NuGet vulnerability metadata remained unreachable (`NU1900`), while builds/tests completed.

## Verification evidence

- Release solution build passed with no compiler errors or new compiler warnings; only NuGet audit metadata availability warnings remained.
- Core tests: 920 passed. Monitor/lifecycle/handoff filter: 31 passed on three consecutive runs.
- Service-safe WPF/integration tests: 1,496 passed with `Category!=InteractiveDesktop` explicitly excluded.
- Separate interactive desktop lane: 5/5 executed and passed, none skipped. Its calendar-dependent icon baseline was fixed while preserving native pixel/taskbar assertions.
- The dashboard-order fixture now runs setup/assertions/cleanup on WpfTestHost with bounded fail-closed owned-work draining; focused fixture/host tests passed three times.
- Fresh artifact stdio smoke passed under PowerShell 7 for all seven tools, structured rejections, schemas, parity, protocol-only stdout and clean stdin exit.
- Final error-message clarification passed 117 tool schema/recovery tests; the initial-missing-file regression verifies no false retained-boundary/retry claim and healthy-file progress.

Commands and outcome traceability are recorded in the [execution plan](McpToolConsistencyExecutionPlan.md).
