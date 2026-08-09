# README branch audit — Execution Plan

This is a living document. Keep `Progress`, `Surprises & Discoveries`,
`Decision Log`, and `Outcomes & Retrospective` current throughout execution.

## Document control

- Created: 2026-08-09
- Branch: `codex/mcp-log-server`
- Owner: Codex

## Resume checkpoint

- HEAD at start: `2cbd8d0`
- Working tree at start: clean
- Next action: none; the audit is complete and ready for handoff.

## Purpose and observable outcome

Ensure the repository root README accurately orients users and contributors to every relevant change on this branch, while delegating detailed MCP operation and architecture material to the existing focused guides.

## Scope

- Audit `origin/main...HEAD` for user-facing, installation, repository-layout, build, test, and contributor-workflow changes.
- Update the root `README.md` where branch changes affect repository orientation.
- Verify README links and documented commands against files and project configuration.
- Commit the completed documentation update locally.

## Non-goals

- Duplicate the complete MCP protocol or architecture documentation in the root README.
- Change runtime behavior or other product documentation unless the audit discovers a correctness defect that must be fixed.
- Rewrite unrelated README wording.

## Definitions

- “README-relevant” means information needed to understand the product surface, locate a major project or guide, or run the repository's normal validation and packaging workflows.

## Existing behavior and evidence

- The branch adds `LogReader.Mcp`, a packaged `WeezTail.Mcp.exe` sidecar, five bounded read-only MCP tools, headless persisted-dashboard querying, owner-scoped index caches, packaging validation, focused tests, and MCP documentation.
- The root README currently adds only a link to `McpLogServerGuide.md` relative to `origin/main`.

## Decisions and invariants

- Keep the root README concise and use links for detailed MCP setup, architecture, security, and performance information.
- Preserve the existing Windows x64 and source-tree naming explanation.
- Mention only behavior present in the branch and verified from code, project, packaging, or tests.

## Open questions

- None currently.

## Milestones / issue summary

## README audit and update

- State: COMPLETED
- Dependencies: branch diff, current documentation, project and packaging files
- Purpose: make the root README a correct entry point for the expanded product and solution
- Expected implementation areas: `README.md`
- Tasks: classify branch commits, patch omissions, verify links/commands, review diff, commit
- Acceptance criteria: all README-relevant branch changes are represented directly or by an obvious linked guide; all referenced paths exist; validation instructions match the solution
- Focused validation: link/path checks, `git diff --check`; no runtime validation for documentation-only changes
- Progress/evidence: all 40 branch commits and 73 changed paths were classified; the README now covers the shipped sidecar, project/test layout, detailed design references, focused validation, and packaging smoke test; every local README link resolves and `git diff --check` passes

## Progress

- [x] Inspected repository instructions, branch status, base, changed-file inventory, README, and existing MCP implementation tracker.
- [x] Audited commit-level changes and public artifacts.
- [x] Updated `README.md`.
- [x] Validated links, command paths, and diff hygiene.
- [x] Commit relevant tracked changes.

## Final validation and demonstration

- `git diff --check`: passed.
- Parsed every Markdown link in `README.md` and confirmed each local target exists: passed.
- Confirmed the documented focused test project and portable publish script exist: passed.
- Runtime build/tests were not run because this is a documentation-only change and the project instructions exclude purely content changes from runtime validation.

## Surprises & discoveries

- The existing MCP implementation tracker is complete and explicitly local/ignored; this focused plan records the separate README audit required by the current task.

## Risks and mitigations

- Risk: overloading the root README with protocol detail. Mitigation: summarize the product/repository surface and link focused guides.
- Risk: documenting planned rather than shipped behavior. Mitigation: verify statements against committed projects, package scripts, and tests.

## Deferred work

- None.

## Decision log

- 2026-08-09: Use `origin/main` as the audit base because `origin/HEAD` points to it and it is the merge base for the feature branch.
- 2026-08-09: Keep protocol limits and detailed behavior in the MCP guide; expose only the product boundary, repository orientation, authoritative reference links, and contributor commands in the root README.

## Outcomes & retrospective

- Observable outcome: a reader landing on the repository can now see that packaged WeezTail includes a separate headless MCP executable, find its source/tests/design documentation, and run its focused validation/package flow.
- Files changed: `README.md` plus this execution-plan record.
- Remaining risks: none identified; detailed values remain centralized in the focused documents to avoid drift.

## Handoff history

- None.
