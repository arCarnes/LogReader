# Compact MCP idle tail responses — Execution Plan

## Document control
2026-09-22. Owner: Codex. Status: complete.

## Resume checkpoint
Complete. The worktree was validated and the implementation is ready for a local commit.

## Purpose and observable outcome
Cursor polls without new content return fewer protocol bytes. Filtered polls that advance past nonmatches omit repeated file metadata while keeping progress and cursor evidence.

## Scope
Core tail result metadata, headless tail paths, MCP serialization/schema, protocol tests, guide, and measurement harness.

## Non-goals
Changing file reads, cursor encoding, WPF tailing, or the common MCP envelope.

## Definitions
Idle: a cursor poll on an unchanged file with no new physical lines, generation event, updated unfinished line, removal, or error.

## Existing behavior and evidence
The backend always returns file metadata and a cursor. Previous packaged measurements recorded 2,625 unfiltered and 2,899 filtered protocol bytes for idle polls. The MCP policy already omits empty line arrays but not file metadata or cursors.

## Decisions and invariants
- Default compact wire response; backend result retains file and cursor.
- Include `isIdle` explicitly. Omit unchanged cursor and repeated file/count metadata only on true idle.
- Filtered continuation over nonmatches omits file metadata but retains the advanced cursor and line counters.
- Initial reads, matches, generation changes, unfinished-line updates, removals, and errors retain full evidence.
- Keep structured and text MCP content identical.

## Open questions
None.

## Milestones / issue summary
1. Mark compactible tail results and serialize/schema them correctly.
2. Add backend and protocol coverage, update guide and measurement harness.
3. Run focused and full validation, measure protocol bytes, and commit.

## Progress
- [x] Inspected current tail paths, contracts, MCP JSON policy, tests, and measurement harness.
- [x] Implement compact response behavior.
- [x] Validate focused tests and measure.
- [x] Run full build and test suite, including UI tests.
- [x] Commit focused change.

## Compact tail response
- State: complete
- Dependencies: none
- Purpose: lower idle and sparse-filter polling bytes without losing cursor progress or event evidence.
- Expected implementation areas: tail contract/backend and MCP JSON policy.
- Tasks: identify true idle and no-match continuations; conditionally omit wire fields; align output schema.
- Acceptance criteria: cursor reuse works, advanced cursor is returned, non-idle events and errors remain full.
- Focused validation: backend and MCP protocol tests.
- Progress/evidence: Focused tail/backend/MCP tests passed 59/59. Exact `HEAD` baseline versus current packaged binary on the same 50-file, 100-line fixture: unfiltered idle protocol bytes 2,625→719; filtered idle 2,899→719; filtered nonmatching append 2,899→1,839. Matching append 3,101→3,141 due to `isIdle: false`.

## Final validation and demonstration
`dotnet build LogReader/LogReader.sln --no-restore -m:1` passed with zero warnings/errors. `dotnet test LogReader/LogReader.sln --no-build --no-restore -m:1` passed 933 UI and 552 Core tests. Focused backend/MCP tests passed 59/59. PowerShell measurement script parsed, and `git diff --check` passed. Packaged stdio measurements on identical 50-file, 100-line fixtures yielded unfiltered idle 1,079/2,625→256/719 structured/protocol bytes; filtered idle 1,201/2,899→256/719; filtered nonmatching append 1,201/2,899→776/1,839. The filtered matching append grew 1,282/3,101→1,297/3,141 bytes. Measured 0/1 and 1/3 filtered match rates. Timings are recorded in `McpPerformanceMeasurements.md` and are single samples, not evidence of a speedup.

## Surprises & discoveries
The previously published portable binary predates filtered tailing, so it was unsuitable as a baseline. An ignored source archive of exact `HEAD` was published for the comparison. The sandbox identity could not authorize the generated fixture (`log_access_denied`); the packaged measurements succeeded with normal host file access. The initial protocol test assumed a `required` schema array, but the SDK omitted it when no fields were required; the test now accepts that valid shape.

## Risks and mitigations
Default compaction changes the wire shape for existing clients. The guide and tool descriptions document optional file/cursor fields and cursor reuse. Protocol tests cover the schema and identical text/structured content. The backend still retains the original file and cursor data. Matching responses are 40 protocol bytes larger because `isIdle: false` is explicit.

## Deferred work
None.

## Decision log
- 2026-09-22: User chose default compact responses, omitted unchanged cursor, explicit `isIdle`, and compact filtered no-match continuations.

## Outcomes & retrospective
Compact idle and sparse filtered continuation responses are implemented. Wire compaction removes repeated metadata and unchanged cursors without changing index reads or signed cursor encoding. The measurement harness now covers no-match appends and records match rates. The exact-commit baseline was necessary because the existing published binary predated filtered tailing.

## Handoff history
None.
