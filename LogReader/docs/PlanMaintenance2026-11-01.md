# Plan maintenance - 2026-11-01

## Active MCP project

- [Overview and recommendations](McpToolConsistencyPlan.md).
- [Execution plan and resume checkpoint](McpToolConsistencyExecutionPlan.md).
- [Deferred rolling-file-date backlog](McpToolConsistencyBacklog.md).

These documents supersede this chat's earlier recommendation ordering and the backlog's initial UI-parity emphasis. Understandability and utility guide the work; manual UI workflows do not constrain useful bounded capabilities. Runtime work has not started.

## Cleanup performed

Archived 43 historical plan documents after reviewing their completion checkpoints, recorded validation/outcomes, and open/deferred items. This is archival of completed agreed scope, not a claim that every historical proposal shipped, every manual check ran, or every branch merged into current main. No plan content was deleted or rewritten.

- 39 completed local plans from `.agent/plans/` now live in [.agent/plans/archive/2026-11-01](../../.agent/plans/archive/2026-11-01/).
- Two completed ignored workbench plans, initial MCP v1 and Shared Views phases 1-3, now live in the archive's [workbench subdirectory](../../.agent/plans/archive/2026-11-01/workbench/). They remain local/ignored; Shared Views completion does not imply its feature branch is merged into this checkout.
- Two already-tracked completed plans retain their Git history under [docs/plans/archive/2026-11-01](plans/archive/2026-11-01/): [seasonal icons](plans/archive/2026-11-01/seasonal-window-icons.md) and [performance remediation](plans/archive/2026-11-01/2026-07-10-performance-remediation-phases.md).
- The local [archive manifest](../../.agent/plans/archive/2026-11-01/manifest.json) records every original path, destination, SHA-256, tracking status, and archival basis. Hash checks verify all 43 documents are byte-for-byte preserved.
- Historical relative links and source references inside archived documents retain their original context; consult each manifest entry's original source path. Do not execute historical instructions or interpret optional/deferred work as completed implementation.

The many-file search plan contains a stale unchecked "Work unit G committed" item immediately followed by its checked commit evidence (`a07f6a2`), plus a final completed checkpoint. It was archived using the later completion evidence rather than treating that stale box as open work.

## Preserved unresolved or uncertain work

These documents stay in their existing locations. Do not execute them as part of MCP tool consistency:

- [Auto-scroll and persistent selection](../../.agent/plans/auto-scroll-persistent-selection.md): native interactive/physical clipboard acceptance remains explicitly unverified.
- [.NET 10 upgrade](../../.agent/plans/net10-upgrade.md): implementation and local runtime-isolation validation are recorded complete, but disposable clean-machine installation/upgrade/uninstall and manual UI acceptance remain outstanding.
- [Release 1.2.1](../../.agent/plans/release-1.2.1.md): publication was explicitly deferred by the user; do not resume an obsolete release automatically.
- [Viewport rollover proposal](workbench/viewport-rollover-plan.md): no conclusive completion checkpoint; retained without assuming it is still necessary or already implemented.
- [MSI reliability execution plan](workbench/bug-fixes/2026-09-15-msi-reliability-execution.md) and [overview](workbench/bug-fixes/2026-09-15-msi-reliability-overview.md): scoped implementation is recorded, but signing and disposable-Windows checks remain deferred/blocked.
- [Historical code-review tracker](workbench/code-review/README-tracker.md): mixed partial/not-started work needs a fresh source audit before further cleanup; no completion inferred from age.

Optional or user-deferred work inside completed archived plans remains preserved in those records. Archiving does not authorize resuming it.

## Validation of this maintenance change

Documentation-only: review current scope against user decisions; verify new/local document links, archive inventory and SHA-256 preservation; run unstaged/staged whitespace checks; review only relevant staged files. Runtime build/test validation is not applicable. No application code, dependencies, settings, versions, tags, release artifacts, installed data, branches, or remote state changed.
