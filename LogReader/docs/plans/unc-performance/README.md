# UNC performance improvement plan

Created: 2026-11-02. Status: U0 complete; U1 optimization rejected; U2 four-worker UI policy accepted; U3 admission optimization rejected; U4 optimization rejected; preceding runtime restored and validated; U5 complete.

Evaluate bounded improvements to UNC loading, search and MCP monitoring while preserving file-generation safety, UI responsiveness, monitoring continuity and resource limits. The first-handle trial did not meet the measured acceptance gates; its scheduling change was restored and independently required correctness fixes retained.

## Start here

Completed outcome: see [Results.md](Results.md) for retained changes, all phase decisions, final timing ranges, validation, resource bounds and deployment limitations.

| Document | Authority |
| --- | --- |
| [Design](Design.md) | Behavior, architecture, compatibility decisions and deferred work. |
| [Execution plan](ExecutionPlan.md) | Phase status, next action, work boundaries, commits and completion evidence. |
| [Validation and measurements](ValidationAndMeasurements.md) | Required regression scenarios, commands, benchmark method and historical evidence. |

Current user instructions and the applicable repository `AGENTS.md` take precedence. The execution plan owns progress; the design owns technical decisions; the validation document owns acceptance evidence. Update linked documents together when an implementation finding requires a change. Historical plans and measurements do not authorize additional work.

The planning baseline is commit `9228d9c`; execution reconciled the clean runtime baseline at `415248a`. U0 is **Complete**, U1 is **Complete — optimization rejected**, U2 is **Complete — accepted**, U3 is **Complete — optimization rejected**, U4 is **Complete — optimization rejected**, and U5 is **Complete**. The execution checkpoint records tooling, actual measurements, bounded-resource proofs and disclosed diagnostic limitations.

## Intended outcomes and order

| Phase | Outcome | Dependency |
| --- | --- | --- |
| U0 | Reproducible baselines and characterized correctness contracts. | None |
| U1 | Initial-handle scan assessed and rejected on paired timing/protected gates; ownership/continuity fixes retained. | U0 |
| U2 | Evidence-based four-worker UI host/share policy. | U1 |
| U3 | Admission trial assessed and rejected below 15%; preceding single-UNC admission retained. | U2 |
| U4 | Two-slot monitor preparation assessed and rejected on repeated slowdown; sequential monitor restored. | U3 |
| U5 | Integrated regression, desktop and end-to-end measurement evidence. | U4 |

The initial evaluation found a 387.06 -> 270.41 ms interactive component improvement with two workers and a 337.45 -> 138.67 ms per-file search improvement with two -> four workers on one small, warm UNC fixture. These support investigation; they are not application latency guarantees. The [measurement record](ValidationAndMeasurements.md#historical-evaluation) preserves the method, control results and limitations without requiring ignored local artifacts.

## Scope and limits

- Real network tests may access only `\\mint-serv\Plex Media\Test` and its descendants. Never enumerate or list its parent or siblings, or follow reparse points outside that subtree. Remote fixtures remain read-only for this plan.
- Keep setup, synthetic writes, indexes, result files and logs in isolated local ignored artifacts. Use test doubles for additional hosts, slow shares, failures and lifecycle mutation.
- Preserve public APIs, MCP schemas, tool counts, cursor formats, default global disk concurrency and public budget values. Internal implementation and test seams may change where required.
- Do not alter network security, share permissions, SMB settings, signing, encryption, authentication, installed data, dependencies, product versions or releases. No pushes, tags, branch switches or history rewriting are included.
- Defer idle backoff, watcher-assisted polling, metadata consolidation, buffer tuning, content caching, handle pools and portable-cache relocation. Rolling-date expansion and broader discovery remain outside scope.

## Completion

Completion requires passing correctness checks, repeated concurrency coverage, actual performance comparisons, a passing desktop lane, a documented accept/reject decision for each candidate, updated checkpoints and coherent local commits. A candidate rejected on measured regression is a valid documented outcome; a missing required check is not a pass. Do not mark the future implementation goal complete while required evidence or owned-work cleanup remains outstanding.

The maintained opt-in measurement entry point is `LogReader/packaging/scripts/Measure-UncPerformance.ps1`, with component, actual WPF dashboard and real MCP stdio workflows, including local lifecycle/handoff scenarios. See the execution checkpoint before resuming and preserve the frozen fixture and baseline bundles for candidate comparisons.
