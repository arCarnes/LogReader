# Filtered MCP log tailing — Execution Plan

## Document control
Requested 2026-09-22. Owner: Codex. Living implementation plan.

## Resume checkpoint
Implementation, full solution validation, and packaged stdio measurement are complete. Next: final diff review and local commit.

## Purpose and observable outcome
`read_log_tail` can return matching lines only, advance past examined nonmatches, and report continuation without losing matches or stale final-line updates.

## Scope
Core contracts, headless backend, cursor codec, MCP tool schema, focused tests, guide, and MCP measurement harness.

## Non-goals
WPF tail filtering, persistent cursors, multiple-file tailing, and context lines.

## Definitions
`maxLines` limits physical lines examined. A removal marker identifies a previously matching unfinished line that no longer matches.

## Existing behavior and evidence
Tail reads use an indexed snapshot and signed process-scoped cursor. Unfiltered default is 200 lines. Current measurement samples only one initial tail call.

## Decisions and invariants
- Optional literal or regex query; no query preserves the current behavior. Case-insensitive by default; regex timeout is 250 ms.
- First call examines the latest `maxLines` physical lines; later calls examine at most `maxLines` lines after the cursor.
- Match against full line, emit at most 4,096 characters centered around the first match when needed.
- Advance across nonmatches; stop before a matching line that cannot fit the response budget.
- Bind filter settings into a signed cursor without exposing query text. Reject changed filters.
- Reevaluate a growing unterminated final line and emit a removal marker when a prior match ceases.

## Open questions
None.

## Milestones / issue summary
1. Implement filtered tail contracts, cursor, and backend behavior.
2. Add backend and MCP protocol tests.
3. Extend measurement harness and documentation; run focused and full validation.

## Progress
- [x] Read relevant implementation, tests, and plan template.
- [x] Implement filtered behavior.
- [x] Validate and measure.

## Filtered backend and contract
- State: complete
- Dependencies: none
- Purpose: bounded, lossless filtered cursor traversal.
- Expected implementation areas: query contracts, cursor codec, headless backend, MCP tool.
- Tasks: implement validation, full-line matching, output excerpt selection, counters, cursor binding, removal marker, and error handling.
- Acceptance criteria: prior unfiltered behavior remains; filtered cursor does not skip a match; no mismatched filter accepted.
- Focused validation: Core build and filtered backend/MCP tests.
- Progress/evidence: Core test project build passed with zero warnings/errors. Focused filtered tail and MCP tests passed 49/49; UTF-16/tampered-cursor and in-place truncation tests passed separately. Final full solution build passed with zero warnings/errors; full test suite passed 933 WPF + 541 Core tests.

## Documentation and measurement
- State: complete
- Dependencies: filtered backend and contract
- Purpose: explain semantics and quantify response size.
- Expected implementation areas: MCP guide and measurement script.
- Tasks: document API; compare initial, idle, and append response sizes and timing.
- Acceptance criteria: report structured and protocol bytes without presenting them as token counts.
- Focused validation: measurement smoke and script parser.
- Progress/evidence: Guide and measurement harness updated; PowerShell parser passed. Fresh published MCP binary completed packaged stdio measurement on a 50-file, 100-line-per-file fixture with no tail partial/errors. Initial 20-line protocol response: 6,985 unfiltered versus 2,903 filtered bytes; three-line append: 3,243 versus 3,101 bytes; idle poll: 2,625 versus 2,899 bytes.

## Final validation and demonstration
`dotnet build LogReader/LogReader.sln --no-restore -m:1` passed with zero warnings/errors. `dotnet test LogReader/LogReader.sln --no-build --no-restore -m:1` passed 933 WPF and 541 Core tests. PowerShell parser and `git diff --check` passed. Fresh self-contained `WeezTail.Mcp.exe` publish and measurement passed outside the sandbox after the sandboxed fixture returned `log_access_denied`.

## Surprises & discoveries
Existing snapshot reader returned only bounded prefixes, so a full indexed-line read method was added for matching while keeping returned excerpts bounded.

## Risks and mitigations
Full-line regex matching can consume work on very large lines; retain the existing regex timeout and request deadline. Cursor progress must use examined line position, not last returned match.
Idle filtered responses include counters and a longer cursor, so they may be larger than idle unfiltered responses. Measurement confirms this on the fixture; the reduction comes from omitted log text when a filter is selective.

## Deferred work
None.

## Decision log
- 2026-09-22: Extend `read_log_tail`, use matching-lines-only output, recent-window initial scan, physical-line limit, removal marker, filter-bound cursor, full-line matching, and stop-before-omitted-match response handling.

## Outcomes & retrospective
Filtered tailing is available through the existing MCP tool. Backend and protocol tests cover cursor integrity, line updates, bounded excerpts, budgets, errors, and regex behavior. The measured byte savings are substantial for a sparse initial tail and modest for a three-line append; idle overhead increased. This is a payload proxy, not a direct client token measurement.

## Handoff history
None.
