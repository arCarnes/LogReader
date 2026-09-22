# Compact MCP response metadata — Execution Plan

## Document control
- Started 2026-09-21; owner: Codex. One local commit; no push.
- Read `~/.codex/PLANS.md`; inspected completed search/count plans. This is a separate presentation-only follow-up.

## Resume checkpoint
- Complete. Implementation, validation and final diff review passed; recorded in the local feature commit containing this plan. No remaining implementation action.

## Purpose and observable outcome
Reduce response overhead for human-triggered Codex/Claude Code investigations over roughly 100 files while preserving useful excerpts and interpretation safeguards.

## Scope
MCP serialization, wire version, protocol tests, guide and measurement tooling.

## Non-goals
No scanner, cursor, count-summary, context-window, provenance, authorization or dependency changes.

## Definitions
Optional empty metadata: empty incomplete-reason/context arrays and null per-file errors. Omission means empty/no error, never unknown completeness.

## Existing behavior and evidence
All six tools use one serializer. Search/count repeat statistics and complete limit tables. Per-hit empty context and per-file empty reasons repeat. Measurement scripts consume statistics. Backend contracts remain useful for internal diagnostics.

## Decisions and invariants
- Apply policy only at MCP serialization boundary; retain internal backend data.
- Remove search/count statistics and effectiveLimits from wire output and schemas. Limits remain in server_status.
- Omit selected empty arrays and null per-file errors; retain all populated values, identities, provenance, cursors, counts and explicit booleans.
- Increment envelope schema to 3 to identify omission semantics; result count semantics/versions stay unchanged.
- Update measurement script to report unavailable diagnostics as null, not fabricated zero.

## Open questions
None blocking.

## Milestones / issue summary
One coherent change: serialization policy plus contract verification and docs.

## Progress
- [x] Inspected contracts, tools, tests, measurement script, existing plans.
- [x] Implement and validate.
- [x] Review and document validation evidence; include all relevant files in the local feature commit.

## Issue: compact serialization
- State: implementation and validation complete.
- Dependencies: none.
- Purpose: remove repeated low-information metadata.
- Expected implementation areas: MCP tool serialization, protocol fixtures and documentation.
- Tasks: implement type-specific metadata policy; ensure schema agrees with output; test populated context/errors and omitted empty fields; compare representative serialized bytes.
- Acceptance criteria: no loss of populated diagnostic context or exactness/truncation signals; schema fields optional where omitted; actual protocol payloads smaller; focused and full solution build/tests pass.
- Focused validation: Core.Tests build and McpLogToolsTests, followed by solution build/test.
- Progress/evidence: serializer and schema transform agree across search/count/read/tail; 17/17 focused tests passed. Backend scanning and result types retain original metadata.

## Final validation and demonstration
- `dotnet build LogReader.Core.Tests\LogReader.Core.Tests.csproj --no-restore -m:1`: passed.
- `dotnet test LogReader.Core.Tests\LogReader.Core.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~McpLogToolsTests --logger "console;verbosity=detailed"`: 17 passed.
- `dotnet build LogReader.sln --no-restore -m:1`: passed, zero errors; cached NU1900 vulnerability-feed warnings.
- `dotnet test LogReader.sln --no-build --no-restore`: 536 Core tests and 950 Windows tests passed, no skips/failures.
- `Test-McpStdioArtifact.ps1 -ExecutablePath .\LogReader.Mcp\bin\Debug\net8.0\WeezTail.Mcp.exe`: passed against built sidecar (not a published release).
- PowerShell AST parsing: both modified scripts passed.
- Synthetic 50-file search with one hit and one preceding context line per file: structured UTF-8 bytes 33,179 -> 30,373 (8.46% smaller). The all-files-error fixture: 37,993 -> 36,364 (4.29% smaller). Same backend result serialized with previous/current options; not actual client token measurements or a real-log benchmark. Text fallback equals structured output.

## Surprises & discoveries
The measurement script currently sums server statistics, so it needs explicit handling when the compact wire contract omits them.
- Initial focused run: 8 schema tests failed because the SDK inferred required fields from positional record constructor parameters despite serializer `IsRequired = false`. Added a type-scoped schema transform; rerun passed all 17 tests. No test expectations weakened.

## Risks and mitigations
Consumers assuming all properties exist must honor v3 omissions. Verify tool output schemas and document semantics. No security boundary changes; membership checks and sanitization remain in the backend.

## Deferred work
Cursor cache, summary-only counts, provenance deduplication and merging overlapping context.

## Decision log
- 2026-09-21: Keep this first pass conservative: preserve provenance and explicit false/zero safety signals; avoid verbosity switches that add tool arguments for ordinary agents.

## Outcomes & retrospective
The default MCP response is smaller without dropping populated context, provenance, errors, counts, or explicit safety signals. Changes are confined to MCP presentation, envelope version, tests, measurement compatibility and documentation. The modest measured reduction is an appropriate first step for ad hoc investigations; additional savings from provenance deduplication or cursor changes are deferred. No Codex/Claude token totals measured, no installed client upgraded, no release published.

## Handoff history
None.
