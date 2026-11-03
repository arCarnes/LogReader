# UNC validation and measurements

Status: U0 complete; U1 assessed with its performance optimization rejected and correctness fixes validated; U2 accepted after its required ui-v2 repeat; U3 admission trial rejected on consistent primary-gate failure, preceding runtime restored and validated; U4 preparation trial rejected after the required repeat; preceding runtime restored and validated; U5 complete. Historical investigation remains separate from execution evidence. Read the [design](Design.md) and [execution plan](ExecutionPlan.md) together with this document.

Final outcome and requirement audit: [Results.md](Results.md).

## Historical evaluation

Measured on 2026-11-02 before the current planning baseline's latest UTF-8 continuity fixes. This is component-level investigation, not U0-U5 acceptance.

- Remote root: `\\mint-serv\Plex Media\Test`; read-only discovery stayed in that subtree, skipped reparse points, limited recursion to three levels and took at most 130 files. No parent/sibling enumeration occurred.
- Observed 111 log files totaling 2,398,322 bytes. Principal batch: 100 files, 1,679,152 bytes, 25,210 indexed lines and 205 search hits. Largest file: 611,029 bytes. These totals describe the historical fixture; later runs must calculate their own totals.
- Standalone Release/net10.0 harness referenced current Core/Infrastructure sources and used the existing test-friend assembly name to reach internal interfaces. It built successfully with zero warnings/errors using serial MSBuild and shared compilation disabled. An initial default parallel build exited one without compiler diagnostics; two serial builds succeeded.
- Five repetitions per batch operation/concurrency; worker-count order rotated; APIs warmed, caches not flushed. An initial UNC run confirmed the main trend; final UNC and local-control runs each completed 200 measurements. The indexed viewport had ten samples.
- A local mirror control used identical source bytes in a separate run. Index counts, search hit totals and probe byte totals agreed across compared variants. No log text was printed. There was no WPF rendering, server packet capture or full MCP response serialization in these component timings.

All values below are median milliseconds. Batch rows cover 100 files unless indicated.

| Operation | Workers | UNC | Local mirror |
| --- | ---: | ---: | ---: |
| Interactive initial suspend/reopen | 2 | 387.06 | 62.33 |
| Interactive retain initial handle through scan | 2 | 270.41 | 51.86 |
| Interactive initial suspend/reopen | 4 | 168.68 | 34.94 |
| Interactive retain initial handle through scan | 4 | 120.25 | 29.99 |
| Normal indexing | 2 | 267.57 | 50.81 |
| Normal indexing | 4 | 117.65 | 29.94 |
| Search with current policy | 2 | 337.77 | 24.81 |
| Same per-file search, caller-bounded batch | 2 | 337.45 | 27.86 |
| Same per-file search, caller-bounded batch | 4 | 138.67 | 15.86 |
| Same per-file search, caller-bounded batch | 8 | 66.12 | 11.09 |
| MCP idle scanner sequence | 1 | 523.84 | 49.62 |
| MCP idle scanner sequence | 2 | 237.05 | 27.46 |
| MCP idle scanner sequence | 4 | 101.70 | 13.57 |
| Tail open/identity/length/close | 1 | 135.71 | 15.50 |
| Tail probe using one native metadata result | 1 | 131.95 | 14.83 |
| Auto encoding detection | 1 | 15.08 | 19.18 |
| Attributes queried per file | 1 | 5.66 | 1.65 |
| Enumerate one known directory | 1 | 3.23 | 0.11 |
| Read 100 indexed lines from largest file | 1 | 1.53 | 0.41 |
| Raw read, 611,029 bytes, 64 KiB requests | 1 | 0.33 | 0.30 |
| Raw read, 611,029 bytes, 256 KiB requests | 1 | 0.19 | 0.23 |
| Raw read, 611,029 bytes, 1 MiB requests | 1 | 0.22 | 0.21 |

The initial run measured 402.34 -> 262.23 ms for two-worker interactive scanning and 333.14 -> 141.99 ms for two -> four search workers. It also contained a 745 ms alternative-interactive outlier. The final two-worker alternative ranged 262.70-273.00 ms. Tail metadata consolidation showed no consistent improvement across the two runs.

The MCP idle component used initialized EOF checkpoints and invoked real scanner open, encoding validation and after-read validation. It excluded scope resolution, transaction processing and protocol response work. The four-worker result does not authorize raising MCP's current two-operation global limit.

Supplementary local evidence originally lived under repository-root `artifacts/unc-evaluation-20261102/` (`Evaluation.md`, `Program.cs`, `UncEvaluation.csproj`, `results.json`, `results-local.json`, `summary.json`). These files are ignored, optional and machine-specific. The table and method above are the durable evidence; no execution step may require those artifacts or their original synthetic folder names.

These small warm-cache runs cannot establish cold throughput, p95 latency, behavior on every share, or a hard cancellation deadline. Buffered line seeks do not imply one network request per line. API call counts are not SMB packet counts. For earlier full MCP surface evidence, see [MCP consistency measurements](../../McpToolConsistencyMeasurements.md); do not combine their single-run timings with these component samples.

## Maintained measurement tooling (U0)

The opt-in driver is [Measure-UncPerformance.ps1](../../../packaging/scripts/Measure-UncPerformance.ps1), backed by a separate console project under existing Core test infrastructure. Its assembly uses the existing `LogReader.Core.Tests` friend identity, without extending runtime APIs or joining ordinary test discovery. The shared fixture-safety code is exercised by `UncPerformanceFixtureTests`.

From the repository root:

```powershell
# Use fresh IDs. Prepare builds/preserves its own Release component bundle before reading fixtures.
./LogReader/packaging/scripts/Measure-UncPerformance.ps1 -Mode Prepare -RunId baseline-fixtures -IncludeUnc
./LogReader/packaging/scripts/Measure-UncPerformance.ps1 -Mode Baseline -RunId baseline-components -FixtureId baseline-fixtures -BaselineDirectory ./artifacts/measurements/unc-performance/baseline-fixtures/baseline-components
# Publish a later harness build to a separate LOCAL output; do not overwrite the baseline bundle.
dotnet publish LogReader/LogReader.Core.Tests/UncPerformanceHarness/UncPerformanceHarness.csproj -c Release -m:1 -p:UseSharedCompilation=false -o artifacts/measurements/unc-performance/candidate-components
./LogReader/packaging/scripts/Measure-UncPerformance.ps1 -Mode Compare -RunId component-comparison -FixtureId baseline-fixtures -BaselineDirectory ./artifacts/measurements/unc-performance/baseline-fixtures/baseline-components -CandidateDirectory ./artifacts/measurements/unc-performance/candidate-components
```

Outputs stay under repository-root `artifacts/measurements/unc-performance/`. `manifest.private.json` contains physical paths and identity evidence and must remain local/ignored. Normalized reports contain hashes and fixture labels, not physical paths or log content. Prepare generates 100 numbered files (250 UTF-8 lines and one match each), 128 MiB and 9 MiB ASCII files, and nine short encoding/fragment/newline fixtures. It rejects existing fixture directories and reparse-point output ancestors. UNC discovery inspects only the authorized root and descendants, skips reparse entries, limits depth to three and files to 130, and uses a cooperative 30-second discovery budget. Sample runs revalidate descendant components, size/write/identity and full content hashes before/after each scenario. A scope exceeding 256 MiB or lacking full evidence is reported unavailable. No UNC writes or local mirrors are made.

Baseline runs two sequential seven-sample sessions. Compare runs two seven-pair sessions alternating AB/BA, with a fresh process and untimed API warmup per sample. It rejects mismatched exact result digests/counts, fixture hashes, missing scenarios and zero samples. Raw reports remain available if a later comparison fails. The driver records per-session medians, minima/maxima and paired ratios; no outliers are removed. Keep builds and tests idle while collecting timed sessions.

Component coverage is indexing, explicit initial-suspend interactive builds, production policy-driven search/filter, index-plus-100-line viewport reads, and EOF scanner open/encoding/validation at a caller bound of two. `viewport-100` **includes index creation**; it is not an isolated indexed seek. Component work peaks and scanner buffer bounds are observed for the harness batch; search/filter operation peaks are explicitly uninstrumented (`null`). These are not backend admission metrics.

The driver also supports `-Workflow Mcp` with `-McpExecutable <preserved-local-exe>` (and `-CandidateMcpExecutable <candidate-local-exe>` for Compare). Use identical newly built measurement bundles for both variants; a hash guard rejects mismatched clients/reference assemblies before launch. The server executables supply baseline/candidate runtime behavior. Each sample copies the server bundle into an isolated local deployment, writes its private local catalog, observes initialize readiness, then times actual stdio snapshot/search, count, 100-line reads per selected file, monitor initialization and full idle sweeps at 1/2/10/100-file scopes. The additional two-file scope measures real admission for U3. Local 128 MiB and 9 MiB workflows are included. It compares snapshots/counts/reads to independent Core file-reader references and checks complete traversal, BOM-aware full evaluated-byte totals, every checked slot, truthful idle/backlog fields, identical replay envelopes, monitor release and clean process shutdown. Both text and structured response bodies must agree. Reports retain wire bytes, observed working set/private bytes and forward-poll progress. Admission peaks come from existing snapshot/count statistics; absent read/monitor admission metrics are `null`, not inferred from memory. Compare also rejects schema/effective-limit changes. Older pre-audit reports lack observed scan-byte fields and must not be mixed with newer candidate timing as an acceptance comparison.

MCP samples generate separate owned local `old\n` files and append `other\n` or a numbered `needle` line for complete 1/10/100-file active sweeps. Writes and initial baselines are outside poll timing; original frozen fixtures remain untouched. Event kind, byte offset, text and generation presence are checked. Physical generation IDs vary across independent deployments, so cross-process hashes normalize identities to file/offset/kind/content.

The final lifecycle recipe adds 22 real stdio scenarios: every interior split of é/€/😀 with and without a UTF-8 BOM; a UTF-16 half-unit; ANSI; unfinished regex removal/rematch; split CRLF; replacement, truncation and encoding change alongside healthy-file progress; missing/reappearance of the same generation; and search/count frozen-boundary handoff with an incomplete character arriving before monitor creation. Every cursor poll validates exact ordered events, offsets, unchanged generation, checked/unchecked/backlog/idle/stopped fields, response parity and identical replay. Expected per-file errors must match their exact code/state and partial flag; they are explicit correctness scenarios, never complete successful throughput samples. Handoff creation starts an independent observation and is not a cursor replay. Timings sum forward poll response durations, excluding mutation, initial baselines, replay validation and release; release has its own field. Frozen source fixtures are never mutated. The actual dashboard and native-window evidence is documented below and in the execution checkpoint.

Resource observations distinguish protocol counters from deterministic ownership proofs. `ScannerLifetime_IncludesBlockedDisposalAndDrainsBeforeResponse` counts ownership before real scanner open through completion of real disposal, including a readiness-gated uninterruptible disposal. Search/count remain at two scanner lifetimes and the sequential baseline monitor at one; cancelled responses cannot finish while disposal retains its buffer. Each real scanner rents one 256 KiB buffer, so the request fits the unchanged 512 KiB scanner reservation. `Monitor_TotalBudgetRetainsUnconsumedSlotAndOrderedSweepProgress` exhausts exactly 4 MiB across 16 files, leaves the seventeenth slot unchecked and resumes there without skipping it; replay returns the committed response. Existing admission/detached-probe, continuation-memory and lifecycle tests remain authoritative for their corresponding bounds. Absent public monitor/read admission counters are reported as null; sampled process memory and UI planned policy limits are not substituted for them. U3/U4 must extend these proofs for their changed scheduling.

The UI driver uses `-Workflow Ui` and bundles built from `LogReader/LogReader.Tests/UncPerformanceHarness/UncUiPerformanceHarness.csproj` (executable `LogReader.Tests.exe`). It links the existing `WpfTestHost` and requires an available native input desktop. All view-model setup, observation, assertions and cleanup execute on that host. Samples use the real dashboard activation/scheduler/FileSession/readers, native `LogViewportView`, and search/filter panel commands. Each scope warms once, then measures dashboard completion, first preview-or-index publication, isolated indexed 100-line reads, UI disk search, filter and shutdown. Both ordinary preview latency and publication kind are retained; a fast indexed publication is not reported as a preview. Results verify every index offset, preview offset/text/truncation, hit identity/text, filter line identity, indexed read and scrolled viewport content. The required `UncPerformanceDesktopTests` smoke covers one-file and 100-file local load/search/filter/scroll/close and fails rather than skips on unavailable desktop access.

Recipe `ui-v2` seeds the identical stable-ID versioned log-file envelope in one untimed local write, then validates every ID/name/path through the real repository before VM construction. This replaces `ui-v1`'s repeated whole-store rewrite for each entry; measured operations, source fixtures, exact-result checks and thresholds are unchanged. Both variants must use matching recipes. Preserve older evidence separately and do not pool recipes for acceptance. Stage diagnostics reset at the start of each warm/measured scope; bounded failure traces contain at most 24 method names, without exception messages or physical paths. No persistence error is caught or retried to obtain a timing result.

UI tail polling is a synchronized registration-only double, with zero registrations required after shutdown. This isolates loading from background polling; it does not measure production tail polling. Loading uses default auto-scroll, then manual scroll validation disables it. Each warm/measured scope has separate local repositories/cache state, and drains all owned view-model work before host reuse. Policy limits are reported as planned concurrency, not inferred admission peaks; working set is a sample rather than buffer-lifetime evidence. The UI candidate bundles may differ in application/runtime assemblies, but must retain the same measurement recipe and frozen manifest. Prepare fixtures first using the Components workflow; UI/MCP Prepare is rejected before writing artifacts.

```powershell
dotnet publish LogReader/LogReader.Tests/UncPerformanceHarness/UncUiPerformanceHarness.csproj -c Release -m:1 -p:UseSharedCompilation=false -o artifacts/measurements/unc-performance/ui-baseline-bundle
./LogReader/packaging/scripts/Measure-UncPerformance.ps1 -Mode Baseline -Workflow Ui -RunId ui-baseline -FixtureId baseline-fixtures -BaselineDirectory ./artifacts/measurements/unc-performance/ui-baseline-bundle
# Build the same harness source with candidate runtime code into a DIFFERENT local output before Compare.
./LogReader/packaging/scripts/Measure-UncPerformance.ps1 -Mode Compare -Workflow Ui -RunId ui-comparison -FixtureId baseline-fixtures -BaselineDirectory ./artifacts/measurements/unc-performance/ui-baseline-bundle -CandidateDirectory ./artifacts/measurements/unc-performance/ui-candidate-bundle
```

The sandbox's VSTest host could not connect and its UNC read failed. Native authorized Windows execution succeeded without changing timeout or network settings. Record the execution context in subsequent evidence; a sandbox startup abort does not count as a test pass.

## Fixture and benchmark protocol

U0 must implement this protocol in maintained, opt-in tooling before runtime changes. Do not execute new measurements while creating the planning package.

1. Capture baseline commit, SDK/runtime, OS, Release configuration, effective limits, artifact hashes and normalized fixture manifest. Build baseline and candidate outputs into separate local locations without switching branches or overwriting baseline binaries.
2. For UNC discovery, normalize the exact allowed root and enforce a separator-aware descendant check before access. Inspect the supplied root itself, never its parent. Walk at most three levels, skip reparse points, take at most 130 regular files and fail if a selected path escapes the root. Use a time budget and cancellation checkpoints, while acknowledging that a synchronous OS call can outlast them. Do not invoke an existing harness that creates remote run folders.
3. Select stable ordered 1/10/100-file manifests from the discovered files. Report unavailable sizes as missing evidence. Cap content read per individual benchmark invocation at 256 MiB; report larger sources as unavailable for that scenario, not as complete after a partial scan. Record before/after size/write/identity evidence so changed fixtures invalidate paired exact-result comparisons.
4. Create separate local fixtures under ignored `artifacts/measurements/unc-performance/`: 1/10/100 files with exactly 250 numbered UTF-8 lines each and one `needle` match per file; a 128 MiB ASCII file made of repeated 128-byte newline-terminated lines; a file larger than two 4 MiB scan quanta; and short BOM/BOM-less UTF-8/UTF-16/ANSI, split-character, CRLF and unfinished-line fixtures. Keep generation rules and seeds in the maintained harness. No writes or mutation to UNC fixtures.
5. Use local files and readiness-gated fake UNC paths for appends, replacement, truncation, deletion/reappearance, failures, slow I/O, other hosts and other shares. A fake-path test is not real UNC evidence. Do not enumerate additional servers or shares.
6. Warm each API once outside timing. Perform seven baseline/candidate pairs, alternating AB/BA order each pair. Run two independent sessions sequentially, with fresh processes and the same fixture manifest. Do not run tests/builds concurrently with timed sessions. Do not flush system/server caches; label results warm/cache-uncontrolled.
7. Measure both isolated components and real entry points. UI: actual session/dashboard load and preview publication, search/filter batches and 100-line reads. MCP: actual stdio snapshot/search/count/read, monitor initialization and complete idle scope sweeps, plus release/shutdown. Local active polls cover nonmatches, matches and lifecycle continuity. Observe child-process readiness rather than using startup sleeps.
8. Verify exact line/hit/event identities and counts, checked/unchecked/backlog fields, cursor replay, response parity and resource caps on every relevant run. Report partial/error responses as such; a fast incomplete call is not a speedup. Stop processes and release cursors/owned work in `finally`.
9. Save raw samples and summarized medians/minima/maxima, paired ratios, elapsed time, actual admitted peak concurrency, response bytes, working set, live readers/buffers/leases and cleanup results. Include total sweep time and calls, not just one fast idle poll. Report labels must omit credentials, full physical paths, private labels and log text. Store the path manifest separately as a private local fixture input.

The local 128 MiB and lifecycle fixtures are mandatory. Large-file real UNC throughput is explicitly outside this read-only fixture coverage when no suitable authorized file exists; say so. Do not synthesize large remote files to close that gap. An inaccessible allowed root leaves required real UNC acceptance open; do not broaden discovery.

## Environment representativeness

The user describes the authorized fixture as a Windows client accessing Samba on Linux, hosted on a mini PC backed by an HDD enclosure. This topology is user-provided context, not an independently measured hardware/network inventory. The real fixture covers one UNC host/share and one storage path. Production may span multiple shares and servers with stronger CPU, network and storage infrastructure; those differences can change the useful concurrency and dominant costs. Warm/cache-uncontrolled timings here support the package's fixed accept/reject decisions for this fixture, not a universal choice for all SMB deployments. Controlled multi-host/share tests establish correctness, normalization, isolation and resource bounds only; they do not establish real multi-server throughput. Additional production measurements require separately authorized representative targets; never discover or benchmark outside the currently allowed subtree.

## Candidate decision rule

These are prospective engineering acceptance thresholds, not claims about the historical data. Compare each candidate against the preceding accepted phase, and final delivery against U0.

- Correctness, contract compatibility, aggregate limits and complete owned-work cleanup are hard requirements. A failure cannot be traded for speed.
- Retain a performance candidate only if its primary UNC workflow's paired median elapsed time improves by at least 15% in each of the two seven-pair sessions. U1 primary workflow: 100-file interactive loading; U2: 100-file UI disk search; U3: complete `unc-2/search-snapshot` workflow with admitted two-file UNC work (including resolver/protocol overhead); U4: complete 100-file idle monitor sweep. Record initialization and all other workflows too. Use complete workflow time, not the synthetic bypass of a production gate.
- A protected local/single-file workflow must not regress by more than both 10% and 5 ms in median time; UI preview publication must not regress by more than both 10% and 16 ms. This combines a relative bound with a noise floor. All public resource caps remain unchanged; increasing an unbounded backlog or buffer lifetime is a rejection.
- The UI client's `preview-or-index-publication` first-content metric uses the publication **16 ms** floor; its raw-preview latency is also checked separately with that floor and observed sample counts/kinds. Other protected UI workflows, including indexed reads and filtering, use **5 ms**. This maps measured metrics to the preceding fixed thresholds; it does not change them.
- If sessions disagree or exceed regression bounds, repeat the entire two-session comparison once without changing thresholds. A still-inconclusive or regressing candidate is rejected and the preceding accepted runtime behavior is retained. Record the rejection and proceed with independent phases.
- Do not infer tail latency from fourteen pairs, average away failures, remove outliers silently, increase timeouts, relax synchronization assertions, or change fixtures between baseline/candidate to make a candidate pass.

## Regression matrix

The matrix includes evaluated trial requirements as well as retained contracts. First-turn interactive scanning, atomic multi-UNC admission and two-slot monitor preparation were tested in their archived trial commits and rejected under the measured decision rule; their feature-specific assertions are not requirements to reintroduce those designs into the final runtime. Final retained coverage instead proves creation-turn suspension, interactive continuity/ownership, shared single-UNC admission and sequential monitor semantics, together with the accepted four-worker UI policy. See the execution record for trial and restoration evidence.

| Area | Required cases and assertions |
| --- | --- |
| Interactive scan | New build advances under its first lease; pending preview yields; incomplete quantum suspends before permit release; completion transfers index once; unknown generation uses admitted fallback without a null dereference. |
| Interactive lifetime | Cancellation/failure in create/advance/suspend/dispose; multiple quanta; restart and auto-encoding change; no publication after shutdown; tail-registration append caught once. |
| File correctness | Append, truncate, same-size rewrite, replacement, missing/reappeared file, denied access and unknown identity; UTF-8 fragments, UTF-16 halves, BOM-less encodings, CRLF splits and unfinished matching/rematching/removal identities. |
| UI policy | Same share, two shares on one host, different hosts, local plus UNC, duplicate/case/extended paths and malformed paths; global/group peaks; foreground preview and single background lane unchanged. |
| MCP admission | Explicit global cap one; default two; host/share independence; blocked-group waiters do not own global capacity; FIFO among eligible requests; queued cancellation; disposal; metrics and inactive registry reclamation. |
| Detached work | Cancel while a synchronous probe is actually admitted and blocked; capacity/owner persists until return; no reuse/release race; late fault observed; shutdown accounts for remaining work. |
| Monitor preparation | At most two live readers/buffers including disposal; reverse completion order; second preparation starts while first is gated; no shared checkpoint/matcher mutation; one global permit reduces lookahead safely. |
| Monitor budgets | Tiny/exact byte/line/text/provenance bounds; no multiplication by workers; pending preparations discarded without marking checked; persistent next slot/sweep counts; no false idle with unchecked/backlog work. |
| Monitor failure/time | Slow/error/stopped/hot file with healthy quiet file; deadline spans queue/preparation/reduction; no unlimited replacements for timed-out work; observation end and release cause no late mutation. |
| Transactions | Readiness-gated cancellation before commit leaves prior cursor usable; concurrent replay returns identical committed envelope and performs no new I/O; changed authorization/catalog rejects advance and replay. |
| Initialization/handoff | Earlier-file appends during initialization retained; all baseline slots resolved/error before cursor commit; handoff preserves actual frozen boundaries and source availability; no new scan or observation implicitly starts. |
| Public surface | Same tools/schemas/default budgets; structured/text parity, sanitized errors/provenance and protocol-only stdout; existing legacy modes and exact-count traversal unchanged. |
| Desktop | Real WPF window opens/loads/scrolls/closes a local fixture without a hang or leaked work; measure actual dashboard/preview behavior with allowed read-only UNC fixtures separately. Existing required native desktop smoke also passes. |

Run all async WPF/view-model setup, assertions and cleanup on `WpfTestHost`. Worker-thread fakes must synchronize counters/collections. Use explicit readiness/release gates, bounded waits and `finally` cleanup; drain owned work after assertion failures. If draining times out, fail closed and never reuse that host. Do not fix synchronization by sleeping, extending timeouts or disabling more parallelism.

## Validation commands

Run from the repository root. These commands define the maintained validation lanes; actual completed U0-U5 results and exclusions are recorded in Results.md and ExecutionPlan.md. Confirm `dotnet --version` matches `global.json`. Build/test as separate awaited operations. Use Release, serial MSBuild and `UseSharedCompilation=false` consistently. Rebuild after any source/test change before `--no-build` testing. Restore failures must be resolved/reported, not hidden by stale binaries.

### V-Core

```powershell
dotnet build LogReader/LogReader.Core.Tests/LogReader.Core.Tests.csproj -c Release -m:1 -p:UseSharedCompilation=false
dotnet test LogReader/LogReader.Core.Tests/LogReader.Core.Tests.csproj -c Release --no-build --no-restore -m:1 --filter "FullyQualifiedName~InteractiveLogReaderTests|FullyQualifiedName~AdaptiveParallelismPolicyTests|FullyQualifiedName~SearchServiceTests|FullyQualifiedName~HeadlessLogQueryBackendTests|FullyQualifiedName~ResumableLogScannerTests|FullyQualifiedName~QueryContinuationStoreTests|FullyQualifiedName~McpContinuationSettingsTests"
dotnet test LogReader/LogReader.Core.Tests/LogReader.Core.Tests.csproj -c Release --no-build --no-restore -m:1
```

Select the relevant subset first for each phase. `HeadlessLogQueryBackendTests` is the actual partial class containing monitor, lifecycle and resumable tests; do not filter only by their filenames. The rejected U3 coordinator fixture is preserved in trial commit `6239a67`; retained admission/late-fault coverage is in the actual `HeadlessLogQueryBackendTests` partial class. Include any differently named new fixture in the filter and verify selected/executed counts. Repeat changed concurrency coverage three consecutive times after the final relevant edit; a zero-test result fails the check.

### V-UI

```powershell
dotnet build LogReader/LogReader.Tests/LogReader.Tests.csproj -c Release -m:1 -p:UseSharedCompilation=false
dotnet test LogReader/LogReader.Tests/LogReader.Tests.csproj -c Release --no-build --no-restore -m:1 --filter "(FullyQualifiedName~DashboardLoadingResponsivenessTests|FullyQualifiedName~DashboardLoadSchedulerTests|FullyQualifiedName~SessionThreadingLifetimeTests|FullyQualifiedName~SearchPanelViewModelTests|FullyQualifiedName~SearchTailMonitoringSessionTests|FullyQualifiedName~LogTabViewModelFilterTests|FullyQualifiedName~McpStdioProtocolTests)&Category!=InteractiveDesktop"
dotnet test LogReader/LogReader.Tests/LogReader.Tests.csproj -c Release --no-build --no-restore -m:1 --filter "Category!=InteractiveDesktop"
```

Apply the same phase-specific selection and three-run concurrency rule. Report that this service-safe lane explicitly excludes `Category=InteractiveDesktop`; do not describe it as complete desktop coverage.

### Final solution and desktop lanes

```powershell
dotnet build LogReader/LogReader.sln -c Release -m:1 -p:UseSharedCompilation=false
dotnet test LogReader/LogReader.sln -c Release --no-build --no-restore -m:1 --filter "Category!=InteractiveDesktop" --logger trx --results-directory artifacts/test-results/unc-performance/service-safe
./.github/scripts/Invoke-InteractiveDesktopTests.ps1 -Configuration Release
```

The [existing desktop runner](../../../../.github/scripts/Invoke-InteractiveDesktopTests.ps1) rejects no results, zero executed tests, failures and skips. Run outside a service/session-0 environment with actual desktop access. Add any automated real-window acceptance introduced for this work to the `InteractiveDesktop` category. Its required execution is separate from headless WpfTestHost coverage. Record manual performance observations with fixture/run IDs; do not claim a generic launcher dialog test measured dashboard latency.

### Fresh MCP artifact smoke

```powershell
dotnet publish LogReader/LogReader.Mcp/LogReader.Mcp.csproj -c Release -r win-x64 --self-contained false -m:1 -p:UseSharedCompilation=false -o artifacts/publish/UncPerformanceValidation
./LogReader/packaging/scripts/Test-McpStdioArtifact.ps1 -ExecutablePath ./artifacts/publish/UncPerformanceValidation/WeezTail.Mcp.exe
```

Await publish before smoke testing. This is a local validation artifact, not an installation or release. The existing [stdio smoke](../../../packaging/scripts/Test-McpStdioArtifact.ps1) creates isolated local fixtures. The existing performance harness may create remote run folders when passed `LogDirectoryRoot`; do not use that mode under this read-only plan. U0 must supply a separate read-only UNC path.

## Evidence and completion record

For each phase record date, baseline/candidate hashes, executed commands, selected/executed/pass/fail/skip counts, three repeated concurrency outcomes, artifacts, actual fixture scope/totals, performance decision and active-resource cleanup. Distinguish environment warnings from failed checks. Do not copy historical test counts into a new result.

For U5 publish a concise tracked summary/table with normalized fixtures, timing ranges, exact-result checks, topology/global peaks, memory/buffer bounds, response sizes, candidate decisions and limitations. Keep detailed raw samples in local ignored artifacts. Update the execution checkpoint and affected user/developer documentation for accepted behavior. Required unavailable checks keep acceptance incomplete; explicitly deferred real large-file UNC throughput is a disclosed coverage limit, not an invented pass.

Documentation-package delivery validation consists of link/command/source-reference review, baseline-value checks and Git whitespace/diff review. Runtime build/test execution is unnecessary until runtime or test infrastructure is changed in U0 or later.
