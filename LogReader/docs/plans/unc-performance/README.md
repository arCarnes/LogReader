# UNC performance improvement plan

Created: 2026-11-02. Status: Planned; runtime implementation has not started.

Improve UNC file loading, searches and bounded MCP monitoring by avoiding an unnecessary interactive reopen and overlapping independent file operations. Preserve file-generation safety, UI responsiveness, monitoring continuity and existing resource limits.

## Start here

| Document | Authority |
| --- | --- |
| [Design](Design.md) | Behavior, architecture, compatibility decisions and deferred work. |
| [Execution plan](ExecutionPlan.md) | Phase status, next action, work boundaries, commits and completion evidence. |
| [Validation and measurements](ValidationAndMeasurements.md) | Required regression scenarios, commands, benchmark method and historical evidence. |

Current user instructions and the applicable repository `AGENTS.md` take precedence. The execution plan owns progress; the design owns technical decisions; the validation document owns acceptance evidence. Update linked documents together when an implementation finding requires a change. Historical plans and measurements do not authorize additional work.

The planning baseline is commit `9228d9c`. Reconcile the actual working tree and HEAD before execution; subsequent commits may have changed the relevant behavior. All runtime phases U0-U5 are **Not started**. Creating these documents does not create or activate a goal.

## Intended outcomes and order

| Phase | Outcome | Dependency |
| --- | --- | --- |
| U0 | Reproducible baselines and characterized correctness contracts. | None |
| U1 | First interactive scan can use the initial admitted handle. | U0 |
| U2 | Evidence-based four-worker UI host/share policy. | U1 |
| U3 | Shared topology-aware MCP admission, within its existing global cap of two by default. | U2 |
| U4 | At most two monitor files prepared concurrently, then evaluated and reduced in stable order. | U3 |
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

The current delivery consists only of this planning package. Runtime tests and UNC measurements will be performed during later execution.
