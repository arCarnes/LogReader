# MCP tool consistency scope and deferred work

Planning note: 2026-11-01. These are scope decisions, not implemented behavior.

Active work is defined in the [overview](McpToolConsistencyPlan.md) and the [execution plan](McpToolConsistencyExecutionPlan.md). This file preserves the deferred rolling-file-date scope.

Design principle: make scope, time boundaries, evidence, and stopping behavior understandable to people less familiar with AI tools. Tools may perform substantial bounded computation and offer capabilities beyond manual UI workflows when useful. Shared concepts do not require identical tool interfaces.

## Current pass

- Make monitoring accept the same configured folder, dashboard, and file targets as search/count, within one selected rolling-file date.
- Share applicable scope, matching, filter, and time-window rules without coupling the headless MCP process to the UI.
- Simplify search/count choices, continuation and recovery rules, progress/completeness reporting, and error presentation.
- Align monitoring lifecycle behavior and consider an explicitly requested search-to-monitor handoff within the same selected file-date.
- Keep existing single-date selection available; do not expand a request into additional rolling-file dates.

Search and count are bounded, as-of investigations by default. Continuations advance the original investigation; they do not refresh it. Relative windows are captured once for that investigation. The existing implementation freezes each file's extent when first opened, rather than taking an atomic snapshot of all files at one instant.

Agents should gather enough search evidence to answer the question, complete supported count traversal when exact totals are needed, and report terminal partial results with their caveats. Changed files or incomplete results must not cause repeated fresh scans or an automatic switch to monitoring.

Tail/monitor observes changes over an explicitly requested period or stopping condition, with bounded work per poll. Multi-file scope does not imply multi-date scope. A timestamp window crossing midnight is still allowed where supported within selected files; it does not authorize selecting additional dated files.

## Deferred: operations spanning rolling-file dates

Preserve these candidates for a later milestone:

1. Search/count across a range of dates resolved through configured rolling patterns, including scope preview and missing-date reporting.
2. Monitoring that follows rolling conventions into a new dated path, including midnight rollover and changes to date-selected candidates.
3. Search-to-monitor handoff across different rolling-file dates, including overlap, gaps, and duplicate prevention.
4. Shared rolling-file resolution between UI and MCP (previous recommendation item 8). Defer this consolidation as a whole for now, even though some internal refactoring might be date-local.

Before implementing these, define dated-instance identity separately from configured file identity, cross-date physical-file deduplication, provenance, frozen versus advancing date selection, authorization revalidation, and bounded continuation/cursor state.

Generation changes at the same selected path remain relevant to current-pass monitoring lifecycle work. Following a different convention-derived dated path is deferred. Preserve existing supported behavior until an explicit lifecycle policy is chosen; this scope note does not authorize new cross-date following.
