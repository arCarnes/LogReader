# UNC performance outcomes

Status: U0-U5 complete. Final runtime source: `7f2c63b`; original runtime baseline: `415248a`. Detailed commands, failures, artifact provenance and resume handles are in [ExecutionPlan.md](ExecutionPlan.md); acceptance rules and coverage limits are in [ValidationAndMeasurements.md](ValidationAndMeasurements.md).

## Retained changes and decisions

Only four production files differ from U0: four-worker UI host/share policy, the dashboard scheduler's matching plan input, and interactive build ownership/continuity fixes in `FileSession` and the interactive reader. Creation-turn suspension is retained. MCP admission and sequential monitoring match the preceding accepted runtime. No public API/schema/budget, dependency, version or network/security setting changed.

| Phase | Complete primary paired B/A, sessions 1 / 2 | Outcome and retained evidence |
| --- | --- | --- |
| U0 | Baseline only | Reproducible component, actual WPF and MCP stdio clients; immutable fixtures/artifacts; lifecycle and full buffer-lifetime characterization. `ed345c8`. |
| U1 | Required repeat: 0.9753 / 0.9976 | Reject first-turn scan optimization; retain independently required ownership/generation fixes. `ae75ae2`. |
| U2 | Required ui-v2 repeat: 0.5027 / 0.5178 | Accept four UI workers per UNC host/share; protected/preview gates pass. Runtime `52bfedf`, acceptance `129140b`. |
| U3 | 0.9204 / 0.9047 | Reject atomic topology admission below 15%; preserve global two/shared UNC one. Trial `6239a67`, restored/test outcome `c2c0f9e`. |
| U4 | Required repeat: 1.5310 / 1.4732 | Reject two-slot monitor preparation: slower primary plus protected regressions. Trial `3b58dd6`, restored/test outcome `7f2c63b`. |
| U5 | Actual UI repeat: 0.4834 / 0.5135 | Component/UI/MCP integrated checks pass; no new MCP optimization retained. |

Lower ratios are faster. Retention requires primary B/A <=0.85 in both sessions and protected gates. U1/U2/U4 used the single prescribed whole-comparison repeat; U3 consistently failed without a repeat trigger. Rejected code remains reviewable in trial commits but is absent from final runtime.

## Final validation

Matched full solution Release build passed: SDK 10.0.403 under the configured latest-patch policy, runtime .NET 10.0.14, serial MSBuild, shared compilation disabled. Existing NU1900 warnings reflect the unavailable vulnerability advisory endpoint; builds had zero errors.

| Final native lane | Executed / passed / skipped |
| --- | --- |
| Full solution service-safe Core | 1,005 / 1,005 / 0 |
| Full solution service-safe WPF/UI | 1,510 / 1,510 / 0 |
| Retained Core concurrency, three runs | 39 / 39 / 0 each |
| Retained WPF concurrency, three runs | 25 / 25 / 0 each |
| Required real desktop, three runs | 7 / 7 / 0 each |
| Fresh final MCP stdio artifact smoke | Exit zero |

The service-safe lane explicitly excludes `Category=InteractiveDesktop`; the separate native lane requires actual desktop access and rejects zero tests/skips. WPF concurrency setup/assertions/cleanup run in `WpfTestHost`; gates drain in `finally`. Core coverage includes explicit-one/single-UNC bounds, detached normal/late-fault probes, rollback/replay/authorization, exact aggregate monitor budgets, UTF/unfinished-line lifecycle and scanner ownership through blocked real disposal. A discovered cache-test race now awaits the actual index-deletion event rather than zero cache entries; its five-second bound and strict assertions remain.

## Final paired measurements

All comparisons use two sequential seven-pair AB/BA sessions, fresh processes, untimed warmup, identical client recipes and original manifest, warm/cache-uncontrolled caches, all raw samples retained and no concurrent builds/tests. Baseline clones retain exact original U0 runtime DLLs; original bundles are untouched. Local provenance records 22 final/baseline entries and the original 32-entry inventory is reverified.

| Final component UNC-100 search, 205 exact hits | Session 1 | Session 2 |
| --- | ---: | ---: |
| U0 median (min-max), ms | 346.65 (325.84-351.23) | 349.64 (306.08-370.22) |
| Final median (min-max), ms | 158.30 (154.03-177.74) | 157.67 (145.59-164.23) |
| Paired B/A | 0.4670 | 0.4556 |

Components: 28 processes / 1,344 measurements / 48 cases per process; exact results and all protected gates pass; 28/28 owner locks inactive. Components are diagnostic: `viewport-100` includes index creation; the UI's indexed read measures an already-built index. Harness overlap counters do not measure SMB requests.

## Final actual WPF comparison

The prescribed unchanged repeat passed all primary, protected workflow/publication and raw-preview gates in both sessions. Initial local indexed-read/filter regressions triggered that repeat and remain recorded; no outlier was removed. Publication uses the specified 16 ms floor, other protected workflows 5 ms, each combined with 10%. The repeat also passes a stricter 5 ms publication check.

All values below are median (min-max) milliseconds. Each cell is **U0 → final**.

| UNC-100 workflow | Session 1 | Session 2 | Exact units |
| --- | --- | --- | ---: |
| Dashboard load | 1002.08 (965.36-1043.54) → 808.62 (764.99-826.28) | 997.57 (940.27-1053.00) → 796.85 (753.28-816.36) | 25210 |
| First content | 15.51 (12.11-19.58) → 18.40 (16.91-29.14) | 17.90 (12.56-19.14) → 18.64 (12.54-28.79) | 1 |
| Disk search | 352.59 (330.20-699.03) → 171.94 (159.61-185.80) | 339.01 (328.74-360.75) → 171.78 (164.94-179.69) | 205 |
| Filter apply | 640.58 (593.18-914.21) → 450.15 (428.58-471.01) | 630.47 (583.01-658.91) → 444.49 (420.05-460.26) | 205 |
| Indexed 100-line reads | 142.45 (140.50-149.76) → 145.43 (137.75-169.26) | 146.02 (137.44-149.82) → 144.55 (137.92-159.99) | 10000 |
| Shutdown | 17.70 (15.65-19.68) → 15.90 (14.37-16.28) | 16.71 (14.84-18.33) → 16.09 (14.23-18.81) | 0 |

UNC-100 search paired B/A **0.4834 / 0.5135**, or **51.66% / 48.65% improvement**. First content was preview in all seven pairs per side/session. Raw preview medians (min-max) were **15.50 (12.10-19.57) → 18.39 (16.90-29.13) ms** and **17.89 (12.56-19.13) → 18.63 (12.53-28.78) ms**; both gates pass. Planned search concurrency is two → four; observed UI admission peaks are unavailable. Sampled final UNC-search working-set medians were about **316.0 / 315.3 MiB**, diagnostic only.

UI: 56 sampler processes / 2,688 measurements across original and required repeat; exact results, zero unavailable scopes and owned-work drain checks all pass. **896/896** owner locks inactive. Primary and protected acceptance uses the complete repeat; the baseline 699.03 ms outlier is retained.

## Final actual MCP comparison

All **2,184 measurements / 28 fresh sampler/server pairs / 78 scenarios per process** pass exact-result, evaluated-byte, lifecycle/replay, structured/text parity, public schema/limit and protected workflow/release checks. No repeat trigger. Zero unavailable scopes, stderr, active indexed leases or nonzero server exits. Retained indexed sessions were four (within the unchanged cap).

Median (min-max) milliseconds; each cell is **U0 → final**. Wire bytes are complete workflow totals across all pages/calls, not a per-response budget.

| UNC-100 stdio workflow | Session 1 | Session 2 | Exact units | Final wire bytes, both sessions min-max |
| --- | --- | --- | ---: | ---: |
| Snapshot search | 860.74 (847.77-882.95) → 857.95 (834.64-864.86) | 852.06 (829.99-918.29) → 846.06 (825.64-888.15) | 205 | 128,528-128,528 |
| Exact count | 593.10 (545.80-818.37) → 709.25 (557.56-852.58) | 571.28 (538.53-785.15) → 574.59 (535.14-743.10) | 205 | 64,202-64,202 |
| 100-line reads | 1127.29 (1025.28-1866.24) → 1071.64 (990.71-1237.06) | 1030.92 (1009.29-1164.13) → 1059.73 (1024.20-1357.35) | 10000 | 2,376,398-2,376,398 |
| Monitor initialization | 676.51 (610.71-689.73) → 679.89 (635.99-717.31) | 664.11 (654.13-696.79) → 675.01 (667.44-692.55) | 100 | 61,624-61,646 |
| Complete idle sweep | 555.60 (538.32-597.91) → 571.49 (520.27-578.28) | 541.53 (521.33-600.13) → 536.14 (526.31-586.18) | 100 | 85,247-85,313 |

No MCP speedup is claimed: idle-sweep paired B/A is **1.0035 / 1.0238**. Count paired B/A is **0.9788 / 0.9659**, despite the first session's higher separate final median; preserve both statistics and ranges rather than infer consistent improvement/regression from either alone. UNC-100 secondary workflows are reported, not substituted for the prospectively selected UI primary or protected local/single-file gates.

Local active 100-file matching polls delivered exactly 100 events: medians **39.59 → 39.71 / 38.29 → 36.61 ms**; nonmatching polls delivered zero events: **38.67 → 41.70 / 37.26 → 37.41 ms**. All protected lifecycle cases and separately measured release gates pass. Process startup medians **753.93 → 730.46 / 693.39 → 714.61 ms**, shutdown **16.36 → 15.80 / 15.77 → 16.33 ms** are separate diagnostics. The baseline 408.27 ms shutdown outlier is retained.

Observed available query counters: disk peaks **1-2**, UNC peaks **0-1**; read/monitor counters remain null. Active indexed leases were zero, retained four, and every server exited zero. All 28 final public schema hashes match `704E9C03DE2134E494F88BEC3BB005B51C07D049EEFBBFD29A33799C030B5202` and every effective limit matches U0. Whole-workflow response totals across all fixtures range **2,620-2,376,398 bytes**; unchanged per-response limits remain separately enforced.

## Bounds and interpretation

- UI: dashboard global 24; search/filter global 32; UNC host/share four, local root two, unknown one. Preview priority/reservation and one background operation remain. Topology tests prove shared bounds for aliases/multiple shares/hosts and cancellation. UI report limits are planned, not measured admission peaks.
- MCP: default global disk two, shared UNC one, heavy-request and two-file search/count lookahead unchanged. Deterministic real-scanner tests count before open through completed disposal: search/count at most two 256 KiB buffers; restored monitor at most one, including explicit global one and cancellation. Both fit the unchanged 512 KiB scanner component of the existing working reservation. Sampled process memory is diagnostic.
- Public monitor scope 100, default poll ten, total poll 4 MiB, per-file 256 KiB and existing line/text/provenance/time limits remain. Prepared/unseen work cannot claim checked/idle; committed replay performs no new I/O. Original sequential contract is preserved after U4 rejection.
- Public read/monitor admission counters are unavailable (null). Gated ownership/admission tests establish those bounds; policy values and working-set samples do not substitute for missing counters. MCP indexed cache retains at most four sessions intentionally; active leases must be zero and every measured child exits before acceptance.

## Fixture and deployment limits

Normalized remote fixture: 100 files / 1,679,152 bytes / 25,210 lines / 205 search hits; ordered two-file subset 621,817 bytes / eight hits. Discovery originally found 111 files / 2,398,322 bytes. Largest remote file is 611,029 bytes: representative large-file UNC throughput is explicitly deferred. Local 128 MiB and 9 MiB fixtures cover large/multi-quantum controls; local mutation recipes cover 1/10/100-file matching/nonmatching polls, split UTF-8 with/without BOM, UTF-16 halves, ANSI, CRLF, unfinished rematching, generation/encoding/disappearance and frozen-boundary search/count handoff.

The user describes this Windows-to-Linux Samba path as a mini PC backed by an HDD enclosure. It is one real host/share/storage path, not an independently measured hardware inventory or a substitute for multi-server production. Better servers/storage/network and multiple independent shares/hosts can change bottlenecks and useful concurrency. Current accept/reject decisions apply the fixed gates to this fixture; they do not establish universal throughput or reject future separately scoped designs. Controlled topology tests establish correctness/bounds only. All remote access stays read-only inside the authorized subtree; no broader discovery or network/security changes.

The WPF measurement uses real view models, scheduler, FileSession, reader, search/filter and native viewport, with a synchronized registration-only tail double. It measures first-content kind and raw preview separately. Native desktop smoke and local lifecycle tests supplement it; no real remote activity is generated.

Desktop-search preparation remains discussion-only pending user approval. Idle backoff/watchers, metadata consolidation, buffer/cache/handle-pool tuning, rolling-date expansion and broader discovery remain deferred. No push, tag, version bump, installation or release is included.

## Completion evidence audit

| Requirement | Inspected authoritative evidence |
| --- | --- |
| Ordered U0-U5 execution and candidate rules | Baseline/trial/restoration commits and complete original/required-repeat reports in the execution ledger; all phase outcomes above. Rejected runtime is absent. |
| Reproducible unchanged fixtures/runtimes | Original 32 hashes, final 22 provenance entries, unchanged manifest SHA-256, all 112 reports' runtime hashes and exact fixture/results. Original bundles never overwritten. |
| Final build/test/desktop requirements | Matching solution build output; XML counters in both final service-safe reports, all six concurrency repetitions and three actual desktop reports; fresh final stdio smoke. No runtime/test edit followed validation. |
| Correctness, public contracts and resources | Exact digests/units/evaluated bytes, every MCP schema/effective-limit report, 78-case lifecycle recipe, current retained ownership/admission/budget/continuation tests, and source review of all four production diffs. |
| Security and scope | Production/version/project diffs show no auth/network/permission/dependency change; manifest-only read access inside the authorized subtree; local-only outputs/mutations and no reparse traversal. No additional targets or unapproved desktop preparation. |
| Cleanup and preserved work | All measurement/test handles terminal; 4,456 historical local owner locks inactive, zero active measurement executables/test runners; no unrelated working changes. Detailed local completion/cleanup audits are preserved. |
| Delivery | This summary, synchronized plan/validation checkpoints and accepted developer documentation committed locally; no push, tag, branch/history rewrite, version bump, installation or release. |

Final U5 totals: **112 sampler processes / 6,216 measurements** (component 1,344; UI original/repeat 2,688; MCP 2,184). The final public budgets, source and diagnostics limitations are unchanged. Detailed raw reports remain local ignored artifacts; tracked tables preserve the conclusions without exposing log contents/private manifests. Runtime commit **7f2c63b**; resolve the final outcome-document commit with `git log -1 -- LogReader/docs/plans/unc-performance/Results.md`.
