# Compact MCP response metadata — Execution Plan

## Document control
- Started 2026-09-21; owner: Codex. One local commit; no push.
- Read `~/.codex/PLANS.md`; inspected completed search/count plans. This is a separate presentation-only follow-up.

## Resume checkpoint
- 2026-09-22: optional-statistics follow-up complete and validated; final diff reviewed. Included in the local feature commit containing this plan. No deployment or push requested.

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
- Omit search/count statistics by default; expose existing execution statistics only with includeStatistics=true. Always omit effectiveLimits; limits remain in server_status.
- Omit selected empty arrays and null per-file errors; retain all populated values, identities, provenance, cursors, counts and explicit booleans.
- Increment envelope schema to 3 to identify omission semantics; result count semantics/versions stay unchanged.
- Update measurement script to report unavailable diagnostics as null, not fabricated zero.

## Open questions
None blocking.

## Milestones / issue summary
Initial compact serialization is complete. Follow-up: optional statistics with unchanged backend contracts and schema version 3.

## Progress
- [x] Inspected contracts, tools, tests, measurement script, existing plans.
- [x] Implement and validate.
- [x] Review and document validation evidence; include all relevant files in the local feature commit.
- [x] Optional-statistics adapter, measurement switch and docs.
- [x] Follow-up validation and final review; include in local feature commit.

## Issue: optional execution statistics
- State: complete; approved user plan on 2026-09-22.
- Dependencies: completed compact serialization.
- Purpose: restore useful performance diagnostics without ordinary response overhead.
- Implementation: search/count add includeStatistics=false, explicitly serialize CallToolResult using fixed compact/statistics options and typed envelope schemas; method-based search registration avoids Func arity limit. Backend contracts remain unchanged.
- Behavior: opt-in returns existing current-page search / whole-call count statistics, including partial-result evidence; null-result failures stay unchanged. No cache, extra scan or follow-up tool. Flag can change between cursor pages and must not enter cursor fingerprints.
- Tasks: implement adapter; add measurement -IncludeStatistics switch default off and report setting; document optional schema and usage.
- Acceptance: omitted/false/true protocol results and schemas agree; exact values and text/structured parity verified; errors, cancellation, concurrency and real paginated searches covered; existing metadata safeguards preserved; full validation passes.
- Validation: focused Core build/tests, solution build/tests, stdio smoke, measurement runs off/on, fixture byte comparison (not token measurement).
- Progress/evidence: 35 focused checks passed, including both directions of statistics toggling over three real file pages. Default output remains compact; opt-in includes exact backend statistics. Full solution and published sidecar checks passed (below).

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
### Optional-statistics follow-up, 2026-09-22
- `dotnet build LogReader.Core.Tests\LogReader.Core.Tests.csproj --no-restore -m:1`: passed.
- `dotnet test LogReader.Core.Tests\LogReader.Core.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~McpLogToolsTests|FullyQualifiedName~McpSearch_StatisticsCanToggle" --logger "console;verbosity=detailed"`: 35 passed.
- `dotnet build LogReader.sln --no-restore -m:1`: passed, zero errors; cached NU1900 vulnerability-feed warnings.
- `dotnet test LogReader.sln --no-build --no-restore`: 554 Core and 950 Windows tests passed; no skips/failures.
- Published validation-only self-contained win-x64 single-file MCP sidecar with `dotnet publish LogReader.Mcp\LogReader.Mcp.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:DebugType=None /p:DebugSymbols=false /p:NuGetAudit=false -o artifacts\publish\McpStatisticsValidation`: passed. No installed client changed or release published.
- `Test-McpStdioArtifact.ps1 -ExecutablePath .\artifacts\publish\McpStatisticsValidation\WeezTail.Mcp.exe`: passed.
- Ran `Measure-McpLogServer.ps1` against that sidecar with `-FileCount 100 -LinesPerFile 1000`, once with default settings and once with `-IncludeStatistics`. Both passed and returned 400 matching lines. Verified compact diagnostics null; opted-in search filesStarted=50 per page / 100 across two pages, count filesStarted=100. Report includeStatistics matches each switch setting.
- Measurement reports (ignored artifacts): `LogReader/artifacts/measurements/mcp-headless-100files-20260922-043302-690/measurement.json` (off) and `mcp-headless-100files-20260922-043318-645/measurement.json` (on). Cold traversal protocol bytes: 127,324 off / 128,100 on; cold count: 106,989 off / 107,379 on. Execution-dependent counters explain small variation across calls.
- Same-object 50-file synthetic structured-response comparison: 30,373 bytes default vs 30,556 opted in (+183); partial fixture 36,363 vs 36,546 (+183). These are UTF-8 bytes, not measured client tokens.

### Initial compact implementation, 2026-09-21
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
- Follow-up initial focused run: 29 passed, 6 failed because new tests assumed null result/nextCursor were explicit JSON null. Existing MCP defaults already omit nulls; corrected tests to verify exact original failure serialization and absent final cursor rather than changing established output behavior.

## Risks and mitigations
Consumers assuming all properties exist must honor v3 omissions. Verify tool output schemas and document semantics. No security boundary changes; membership checks and sanitization remain in the backend.

## Deferred work
Cursor cache, summary-only counts, provenance deduplication and merging overlapping context.

## Decision log
- 2026-09-21: Keep this first pass conservative: preserve provenance and explicit false/zero safety signals; avoid verbosity switches that add tool arguments for ordinary agents.
- 2026-09-22: User approved a narrowly scoped includeStatistics flag for search/count, default false. This supersedes unconditional removal of statistics; provenance deduplication and historical diagnostics remain deferred. Keep wire v3 because the new capability is opt-in and additive.

## Outcomes & retrospective
The default MCP response is smaller without dropping populated context, provenance, errors, counts, or explicit safety signals. Changes are confined to MCP presentation, envelope version, tests, measurement compatibility and documentation. The modest measured reduction is an appropriate first step for ad hoc investigations; additional savings from provenance deduplication or cursor changes are deferred. No Codex/Claude token totals measured, no installed client upgraded, no release published.

The follow-up restores optional existing statistics with no backend changes, mutable shared serialization state, extra scan or history cache. Input/output schemas, compact defaults and diagnostic scope are explicit; wire/result versions remain unchanged. Changed components: MCP adapter/policy, protocol/backend integration tests, measurement script, guide and architecture notes. All acceptance checks passed. No new security boundary: statistics remain numeric and path-free; configured membership checks and sanitization are unchanged. Agents need to rediscover tools after upgrading/restarting the sidecar. Provenance deduplication, cursor redesign and historical diagnostics remain deferred.

## Handoff history
None.
