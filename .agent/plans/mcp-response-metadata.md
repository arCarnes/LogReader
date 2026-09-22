# Compact MCP response metadata — Execution Plan

## Document control
- Started 2026-09-21; owner: Codex. Earlier metadata/statistics commits are on `origin/main`; the sparse-result and excerpt-layout follow-ups are included in required local commits and are not pushed.
- Read `~/.codex/PLANS.md`; inspected completed search/count plans. This is a separate presentation-only follow-up.

## Resume checkpoint
- 2026-09-22: search result contract 4 implementation and validation are complete. Repeated per-hit context arrays are replaced with compact hit references plus merged excerpts; hit text is admitted page-wide before deduplicated context. The sparse-result and excerpt-layout commits remain local and unpushed.

## Purpose and observable outcome
Reduce response overhead for human-triggered Codex/Claude Code investigations over roughly 100 files while preserving useful excerpts and interpretation safeguards.

## Scope
MCP serialization, search/count result contracts, search result construction, protocol/backend tests, guide and measurement tooling.

## Non-goals
No scanner, cursor encoding, count-summary traversal, context-window, provenance content, authorization or dependency changes.

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
Initial compact serialization and optional statistics are complete. Current follow-up: reduce repeated per-file metadata and duplicate aggregate fields with explicit versioned semantics.

## Progress
- [x] Inspected contracts, tools, tests, measurement script, existing plans.
- [x] Implement and validate.
- [x] Review and document validation evidence; include all relevant files in the local feature commit.
- [x] Optional-statistics adapter, measurement switch and docs.
- [x] Follow-up validation and final review; include in local feature commit.
- [x] Omit clean zero-hit search file records and disclose the per-page omission count.
- [x] Make low-information per-file fields conditional in the MCP wire shape.
- [x] Remove duplicate search/count aggregate fields and update consumers/docs.
- [x] Run focused and full validation and record byte evidence.
- [x] Review and create the required local commit.
- [x] Replace per-hit context with merged search excerpts and validate contract 4.

## Issue: merged search excerpts
- State: complete; approved user plan on 2026-09-22.
- Dependencies: completed sparse-result and optional-statistics work.
- Purpose: charge and serialize each physical hit/context line once while preserving chronological excerpts and compact match coordinates.
- Implementation: search contract 4 returns compact `hits` plus ordered `excerpts` in both text-returning modes. Hit text is admitted across the whole page before context. Remaining context is selected in catalog file order and balanced outward across hits within each file. Empty arrays and false excerpt-line truncation are omitted.
- Invariants: every returned hit references exactly one excerpt line; match offsets remain local to the emitted match-centered text; counts, cursors, provenance, statistics, cancellation, errors and existing completeness signals retain their meanings. No compatibility flag.
- Acceptance: overlapping context is emitted once and budgeted once; later-file hits cannot be displaced by earlier context; disjoint ranges, long lines, partial context and context failures remain explicit; protocol schema/text parity, focused/full tests, stdio smoke and serialized-byte evidence pass.
- Focused validation: Core build; MCP protocol, backend context/budget, indexed-reader tests; then solution build/test, release publish, stdio smoke and measurement-script regression.
- Progress/evidence: backend, wire contract, schemas, descriptions and guides are complete. Focused 530/530 Core tests and full 530 Core + 933 Windows tests passed. Release publish, stdio smoke and measurement runs with statistics off/on passed. A representative received response encoded 168 repeated hit/context line objects for 39 unique lines; the contract 4 layout reduced compact UTF-8 JSON from 79,503 to 33,742 bytes (57.6%).

## Issue: sparse search results and contract cleanup
- State: complete.
- Dependencies: completed compact serialization and optional statistics.
- Purpose: prevent no-hit pages over roughly 100 files from spending most response bytes repeating identities and unchanged metadata, while keeping all evidence needed to interpret matches, partial scans, errors and truncation.
- Implementation: search `files` retains records with hits, positive counts, errors, incomplete evaluation, truncation or unstable generation evidence. Clean exact zero-hit records are omitted and `pageOmittedZeroHitFileCount` reports their number. Empty `hits`, exact-file `evaluatedThroughLine`, untruncated `provenanceTotalCount`, and counts-only search encodings are omitted from MCP serialization. Duplicate aggregate fields are removed in favor of `returnedHitCount`, `isPageComplete`, `isQueryComplete`, and count `isComplete`.
- Versioning: search result contract 3; count result contract 2; envelope schema remains 3. Search cursor fingerprint/version and scan semantics stay unchanged.
- Acceptance: all result modes preserve positive-count records even when hit text is absent; errors/incomplete/truncated/unstable records remain; omission counts are correct per page; schemas and text fallback match; remaining completeness and reason fields fully describe lower bounds; measurement tooling uses retained fields; focused and full solution tests pass.
- Focused validation: Core build; MCP protocol and headless backend tests; representative no-hit payload comparison; then full solution build/test and stdio smoke.
- Progress/evidence: backend and wire implementation complete. Focused 112/112 passed; full solution 525 Core + 933 Windows tests passed. Published-sidecar stdio smoke and measurement-script default/alternate-query/statistics runs passed. The real 100-file absent query omitted 50 clean files on each of two pages and serialized to 9,388 bytes total versus 149,414 bytes in the pre-change exploration, saving 140,026 bytes (93.7%).

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
### Merged search excerpts, 2026-09-22
- `dotnet build LogReader\LogReader.Core.Tests\LogReader.Core.Tests.csproj --no-restore -m:1`: passed; cached NU1900 vulnerability-feed warnings only.
- `dotnet test LogReader\LogReader.Core.Tests\LogReader.Core.Tests.csproj --no-build --no-restore`: 530 passed, no failures/skips. Coverage includes overlapping and disjoint context, page-wide hit priority, distance-balanced context, first-match coordinates with aggregate occurrence counts, long-line response truncation, changed-file context failure, file boundaries, sparse modes, schema omission and structured/text parity.
- `dotnet build LogReader\LogReader.sln --no-restore -m:1`: passed, zero errors; cached NU1900 warnings only.
- `dotnet test LogReader\LogReader.sln --no-build --no-restore`: 530 Core and 933 Windows tests passed; no failures/skips.
- Release self-contained single-file win-x64 publish to ignored `LogReader/artifacts/publish/McpExcerptValidation`: passed. `Test-McpStdioArtifact.ps1` passed against the published sidecar.
- `Measure-McpLogServer.ps1 -FileCount 1 -LinesPerFile 1000` passed against the published sidecar with statistics omitted and with `-IncludeStatistics`. Both runs exited 0 with empty stderr and exact four-line/four-occurrence search/count results; diagnostics were null off and populated on. Ignored reports: `mcp-headless-1files-20260922-182735-085` and `mcp-headless-1files-20260922-182744-544`.
- Representative received Codex response: 12 clustered hits emitted 168 hit/context line objects for 39 unique physical lines. Re-encoding only the layout as compact hits plus merged excerpts changed compact UTF-8 JSON from 79,503 to 33,742 bytes, saving 45,761 bytes (57.6%). This is serialized-byte evidence, not measured client token use.
- `git diff --check`: passed; line-ending conversion warnings only.

### Sparse-result and contract cleanup, 2026-09-22
- `dotnet build LogReader\LogReader.Core.Tests\LogReader.Core.Tests.csproj --no-restore -m:1`: passed; cached NU1900 vulnerability-feed warnings only.
- Focused `McpLogToolsTests|HeadlessLogQueryBackendTests`: 112 passed, no failures/skips. Coverage includes all search modes, positive-count/no-text retention, error/incomplete/provenance-truncated zero-hit retention, all-omitted cursor pages, conditional fields, removed aliases, contract versions, schemas, statistics behavior, and structured/text parity.
- `dotnet build LogReader\LogReader.sln --no-restore -m:1`: passed, zero errors; cached NU1900 warnings only.
- `dotnet test LogReader\LogReader.sln --no-build --no-restore`: 525 Core and 933 Windows tests passed; no failures/skips.
- Release single-file win-x64 publish to ignored `artifacts/publish/McpSparseValidation`: passed. `Test-McpStdioArtifact.ps1` passed.
- `Measure-McpLogServer.ps1` schema 4 passed against the published sidecar for 100 files with the fixture query, 100 files with an absent query, and one absent-query file with `-IncludeStatistics`. The alternate `-SearchQuery` and new returned/omitted file-record fields were recorded correctly; statistics remained opt-in.
- Real 100-file absent query: two complete pages, returned file records `0,0`, omitted clean zero-hit records `50,50`, 0 matches, 9,388 cumulative serialized bytes. The pre-change exploration of the same 100-file absent-query shape produced 78,001 + 71,413 = 149,414 bytes, for a 140,026-byte / 93.7% reduction. This is serialized protocol size, not a measured client token count.
- PowerShell AST parsing and `git diff --check`: passed.

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
Consumers assuming all properties exist must honor v3 envelope omissions and the v4 search result shape. Tool output schemas and guides document the semantics. No security boundary changes; membership checks and sanitization remain in the backend.

## Deferred work
Cursor cache, summary-only counts and provenance deduplication.

## Decision log
- 2026-09-21: Keep this first pass conservative: preserve provenance and explicit false/zero safety signals; avoid verbosity switches that add tool arguments for ordinary agents.
- 2026-09-22: User approved a narrowly scoped includeStatistics flag for search/count, default false. This supersedes unconditional removal of statistics; provenance deduplication and historical diagnostics remain deferred. Keep wire v3 because the new capability is opt-in and additive.
- 2026-09-22: User approved the first three follow-up reductions. Treat `files` as noteworthy per-file evidence rather than an inventory, disclose clean zero-hit omissions explicitly, and remove aggregate aliases in new search/count contract versions. Keep the envelope at schema version 3 and leave cursor payloads unchanged.
- 2026-09-22: User approved search contract 4 without a compatibility flag. Return compact hit coordinates plus merged excerpts in both text modes; admit page hit lines before context, then select unique context by configured file order and balanced distance across hits. Keep cursor inputs, envelope schema, count contract, provenance and statistics behavior unchanged.

## Outcomes & retrospective
The default MCP response is smaller without dropping populated context, provenance, errors, counts, or explicit safety signals. Changes are confined to MCP presentation, envelope version, tests, measurement compatibility and documentation. The modest measured reduction is an appropriate first step for ad hoc investigations; additional savings from provenance deduplication or cursor changes are deferred. No Codex/Claude token totals measured, no installed client upgraded, no release published.

The follow-up restores optional existing statistics with no backend changes, mutable shared serialization state, extra scan or history cache. Input/output schemas, compact defaults and diagnostic scope are explicit; wire/result versions remain unchanged. Changed components: MCP adapter/policy, protocol/backend integration tests, measurement script, guide and architecture notes. All acceptance checks passed. No new security boundary: statistics remain numeric and path-free; configured membership checks and sanitization are unchanged. Agents need to rediscover tools after upgrading/restarting the sidecar. Provenance deduplication, cursor redesign and historical diagnostics remain deferred.

The sparse-result follow-up changes search `files` into noteworthy evidence while keeping page/query aggregates authoritative. Clean exact zero-hit files are explicitly counted instead of repeated, and errors, incomplete scans, unstable generations, truncation, matches and positive counts still force a record. Conditional field omission and removal of aggregate aliases reduce both structured content and its JSON text fallback. Search/count result versions identify the breaking cleanup; envelope and cursor versions remain unchanged. A real 100-file no-hit response was 93.7% smaller than the pre-change exploration, with complete traversal still proven by counts, completion flags, cursors and omission totals.

Search contract 4 removes repeated text from each hit. Compact hit records point into explicit numbered excerpt lines; overlapping context is budgeted and emitted once, while disjoint ranges remain separate. The backend admits hit lines across the page before context and balances the remaining per-file context budget across hits. Existing counts, cursors, errors, provenance, cancellation and optional statistics retain their meanings. The representative clustered response was 57.6% smaller in serialized bytes. No installed client or release was changed; clients must restart after upgrading the sidecar to discover the new output schema.

## Handoff history
None.
