# MCP 8/4/2/1 admission and measurements

Date: 2026-11-03. Runtime source: `a8c02ef`. Baseline source: `251a547` (pre-change PR 25 build).

This subsequent capacity-policy change implements the explicitly requested MCP limits. It does not change the historical U0–U5 trial outcomes. Correctness validation passes; the earlier package’s full performance-acceptance criteria are **not met**: the protected local-count regression persisted in the unchanged repeat, and two-file UNC search did not improve by at least 15% in both sessions. No overall performance acceptance or universal speedup is claimed. The requested policy is retained with this measured tradeoff disclosed.

## Current policy

| Bound | Per MCP process |
| --- | ---: |
| Global disk admission | 8 |
| UNC host and share | 4 each; shares share their host budget |
| Local root | 2 |
| Unknown paths | 1 |
| Heavy requests | 2 |
| Search/count lookahead | 2 files per request |

Global and normalized group capacity are granted atomically in oldest-eligible order. Blocked groups reserve no global slot; inactive groups are reclaimed. Case and extended-path aliases share capacity. Mapped drives remain local roots, and classification performs no network discovery. Detached synchronous probes retain admission and backend lifetime through cancellation until the actual OS work returns, including late faults. Metrics retain their original request ownership through detached completion. Monitoring stays sequential.

A single search can now admit two UNC fills; two concurrent searches can admit four. Eight is the shared server ceiling, not eight workers for one search. Two 256 KiB scanner buffers per search/count request and existing continuation-memory reservations remain unchanged. The public global disk-limit value changes from two to eight; tool schemas and other effective limits match. Higher concurrency can increase contention on existing shares; configured-path authorization and read-only access remain unchanged.

## Validation

- Release solution build passed; zero errors. NU1900 warnings reflect the unavailable NuGet advisory endpoint.
- Final deterministic admission/lifetime selection passed **104/104 three consecutive times**.
- Full service-safe solution suite passed **1,016 Core + 1,510 WPF/UI = 2,526**, zero failures/skips. `Category=InteractiveDesktop` was explicitly excluded; desktop smoke was not rerun for this headless change.
- Freshly published MCP stdio artifact smoke passed.
- Tests cover explicit global one/two/eight, four-slot host/share alias bounds, two-slot local roots, one-slot unknown paths, busy groups allowing other storage to progress, eligible FIFO order, cancellation, idempotent disposal and registry reclamation. Backend coverage proves four admitted UNC fills across two heavy requests and canceled normal/late-fault probes retaining all four host slots until return.

## Paired measurements

Both comparisons used identical fresh measurement clients, preserved separate baseline/candidate servers, the same frozen manifest, warm/cache-uncontrolled caches, API warmup, alternating AB/BA pairs and two sequential seven-pair sessions. No builds/tests ran during timing. Each comparison produced **28 reports / 2,184 measurements / 78 scenarios**; original plus repeat total **56 reports / 4,368 measurements**. No unavailable scenarios, fixture/result mismatches, stderr, active indexed leases or nonzero server exits. Final native process check found zero measurement clients/servers. All raw samples and outliers remain retained.

The driver’s explicit `-AllowDiskConcurrencyChange` permits only the planned global disk-limit difference. It records the opt-in and keeps both actual limits; schemas, other effective limits, client hashes, fixture hashes and exact results must still match. All 56 tool-schema hashes match `704E9C03DE2134E494F88BEC3BB005B51C07D049EEFBBFD29A33799C030B5202`.

### Protected local-count regression

| Comparison, session 1 | Baseline median (min–max), ms | Candidate median (min–max), ms | Paired B/A |
| --- | ---: | ---: | ---: |
| Original: local-100/count | 68.30 (65.30–92.86) | 87.53 (64.87–114.94) | 1.1818 |
| Unchanged repeat: local-100/count | 66.72 (58.75–93.05) | 75.89 (63.62–89.89) | 1.0233 |

The original separate medians regress by 19.22 ms (28.1%); the repeat regresses by 9.17 ms (13.7%). Each exceeds both the 10% and 5 ms protected threshold. Paired medians differ from ratios of separate medians and do not replace that gate. Neither session two nor any other protected timing row fails. Protected release checks pass in both comparisons. One complete repeat was performed without changing code, fixtures or thresholds; no additional repeats were used to seek acceptance.

### Unchanged-repeat UNC results

Times are milliseconds, median (min–max). Paired B/A below one is faster. Both sessions are shown separately; absolute timings varied considerably across the original and repeat comparisons.

| Session | Scope | Workflow | Baseline | Candidate | Paired B/A |
| --- | --- | --- | ---: | ---: | ---: |
| 1 | unc-100 | count | 382.36 (373.40–400.41) | 368.80 (356.77–402.59) | 0.9688 |
| 1 | unc-100 | monitor-idle-sweep | 336.63 (327.88–342.51) | 335.70 (328.87–339.62) | 0.9916 |
| 1 | unc-100 | monitor-initialize | 435.45 (407.32–580.03) | 421.69 (413.38–577.00) | 0.9529 |
| 1 | unc-100 | read-100 | 806.05 (777.16–846.32) | 793.09 (780.50–808.86) | 0.9821 |
| 1 | unc-100 | search-snapshot | 561.53 (536.26–638.38) | 550.63 (533.91–586.11) | 0.9839 |
| 1 | unc-2 | search-snapshot | 21.02 (19.73–22.61) | 17.84 (16.16–19.83) | 0.8641 |
| 2 | unc-100 | count | 422.99 (354.78–560.26) | 380.11 (361.50–534.19) | 0.9535 |
| 2 | unc-100 | monitor-idle-sweep | 332.91 (321.26–529.46) | 327.83 (323.56–505.87) | 0.9896 |
| 2 | unc-100 | monitor-initialize | 558.85 (414.59–678.57) | 445.07 (404.46–670.29) | 0.7616 |
| 2 | unc-100 | read-100 | 808.21 (788.75–1016.59) | 809.56 (792.33–1011.05) | 0.9976 |
| 2 | unc-100 | search-snapshot | 577.02 (546.07–839.16) | 554.88 (539.39–845.68) | 0.9839 |
| 2 | unc-2 | search-snapshot | 21.93 (19.73–32.14) | 18.59 (16.32–30.06) | 0.8480 |

Two-file UNC snapshot search improves by **13.6% / 15.2%** in paired medians. UNC-100 search improves about **1.6% / 1.6%**. Observed per-request UNC search peaks change from one to two; these sequential-request measurements do not prove four/eight-way production throughput. The shared bounds are verified by deterministic tests. Local controls, reads, count, complete idle sweeps, active monitoring, lifecycle/replay and release remain in the raw reports.

UNC-100 covers 100 files / 1,679,152 bytes / 205 matching lines. The two-file subset covers 621,817 bytes / eight matches. Remote inputs remain read-only in the existing development fixture subtree. These are small-file, single-share measurements; representative large files, production-configured targets, multiple independent servers and simultaneous-client throughput remain unmeasured. No tail-latency claim is supported by fourteen pairs.

## Local evidence

Ignored repository-root measurement artifacts:

- `artifacts/measurements/unc-performance/mcp-8421-unc-comparison-20261103/`: `summary.json`, raw reports and `decision-audit.json`.
- `artifacts/measurements/unc-performance/mcp-8421-unc-comparison-repeat-20261103/`: `summary.json`, raw reports and `decision-audit.json`.
- Preserved pre-change server: `pr25-mcp-candidate-20261103`.
- Frozen candidate/client: `mcp-8421-server-20261103` / `mcp-8421-client-20261103`. Binaries were built from the exact runtime tree committed as `a8c02ef`; the commit itself followed validation/publish, so embedded source-revision metadata may name its parent.
- Private path manifests and source log contents remain local/ignored.
