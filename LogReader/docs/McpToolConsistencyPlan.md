# MCP tool consistency: implementation overview

Status: planned, not implemented. Created 2026-11-01 (America/New_York).

Use the [execution plan](McpToolConsistencyExecutionPlan.md) for milestones, acceptance criteria, validation, decisions, and resuming work. Keep rolling-file-date work in the [deferred backlog](McpToolConsistencyBacklog.md).

## Goal

Make WeezTail's MCP tools powerful and predictable: a model can search, count, or monitor the same configured scope using understandable matching rules, without manually coordinating one tail request per file or repeatedly searching to chase certainty.

Understandability governs concepts and explanations; utility governs capabilities. UI workflows are evidence of useful behavior, not a ceiling. Share underlying rules where appropriate, while keeping purpose-specific operations and the headless/UI process boundary.

## Time and stopping semantics

- Search/count are bounded, as-of investigations. A continuation advances the original investigation; it does not refresh the selection date, relative window, or already-frozen file extents.
- The existing snapshot is per-file: an extent freezes when that file is first opened. This project does not promise a simultaneous filesystem snapshot.
- Search may stop when enough evidence answers the question. Exact totals require finishing supported count traversal. A cap, terminal failure, or changed file can leave an honestly partial answer.
- Do not repeatedly restart searches, narrow windows, or rescan changed files merely to chase exactness. Terminal uncertainty is a valid reported outcome.
- Monitoring requires an explicit intent to observe over time. It uses bounded polls, reports backlog, and respects the requested period or stopping condition. Search/count never enable it automatically.
- More internal computation is welcome when it makes investigations simpler and remains bounded, attributable, and within the requested scope.

## Recommended changes

1. **Multi-file monitoring.** Use configured folder/dashboard/file targets, one scope cursor, physical-file deduplication, fair bounded polling, per-file events/errors, and explicit backlog. Reward high; risk high.
2. **Shared scope and filter semantics.** Reuse applicable text/regex, case, include/exclude, and time-filter rules. Support time-only filtering and captured relative windows where meaningful. Each tool retains purpose-specific inputs. Reward high; risk medium.
3. **Monitoring lifecycle and handoff.** Define consistent events and explicit policies for same-path rotation, truncation, availability, encoding, and unfinished lines. Add an opt-in snapshot-to-monitor handoff within the selected file-date. Reward high; risk high.
4. **Progress and confidence.** Distinguish work remaining, evidence uncertainty, and output omitted. Report timestamp coverage and differentiate caught-up monitoring from a batch with no matches. Reward high; risk medium.
5. **Simpler search/count choices.** Prefer search for evidence plus optional context, and count for totals/buckets. Preserve compatibility while reducing redundant mode choices in guidance. Reward medium-high; risk medium.
6. **Continuation and recovery.** Align cursor/replay/expiry/revision rules and actionable recovery guidance without equating snapshot completion with a live monitor catching up. Reward high; risk medium-high.
7. **Errors.** Make rejected calls, retryable failures, and partial per-file success consistently distinguishable; preserve useful evidence and avoid blind retry guidance. Reward high; risk low-medium.

The execution order starts with contract decisions, builds monitoring and lifecycle behavior, then finishes public simplification and compatibility. It covers all seven outcomes; the first milestone is not the entire project.

## Scope boundary

Use one selected rolling-file date per investigation. Keep existing single-date selection available. Do not select or follow another convention-derived dated path, including at midnight. A timestamp interval may cross midnight within selected files where supported; it does not expand physical-file selection.

Same-path file generation changes remain in scope. Date-range traversal, cross-date monitoring/handoffs, and rolling-path resolver consolidation are deferred. No new daemon, UI dependency, arbitrary path inputs, dependency upgrades, version bump, push, or release is part of this work.

## Delivery

Implement and validate coherent milestones, commit relevant changes locally, and keep the execution checkpoint current. Ship compatible tool schemas, documentation, deterministic regression coverage, real stdio verification, and representative bounded multi-file measurements. Make UI changes only where required to establish the agreed shared behavior; a broad UI redesign is not part of this goal.
