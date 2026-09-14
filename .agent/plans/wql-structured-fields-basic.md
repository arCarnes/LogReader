# Basic WQL and structured fields — Execution Plan

This is a living document. Keep Progress, Surprises & Discoveries, Decision Log,
and Outcomes & Retrospective current throughout execution.

## Document control
- Owner: Codex; approved implementation requested 2026-09-13.
- Branch: `explore/wql-structured-fields`. No push, amend, or unrelated changes.
- Source: user-approved Basic WQL and structured fields plan in this task.

## Resume checkpoint
- Current milestone: A–D implementation and automated validation complete.
- Next action: native visual walkthrough requires Computer Use approval for
  WeezTail; the helper refused launch with "Computer Use was not approved to use
  weeztail". Do not bypass that permission. No implementation work remains.
- Starting state: clean worktree on the requested exploration branch.

## Purpose and observable outcome
Users define reusable independent regex text/number fields, choose a profile,
and run expressions such as `level = "ERROR" AND duration_ms > 500` against
desktop disk snapshots or configured MCP targets. Results expose field values,
parsing diagnostics, and honest completion/truncation status.

## Scope
- Settings profiles, editor and sample preview, expression compiler/evaluator.
- Existing snapshot scanner integration and desktop search results/details.
- Read-only `list_field_profiles` and `query_logs` MCP tools.
- Focused and full regression validation, examples, local milestone commits.

## Non-goals
Aggregation, SQL tables, live WQL/tail/viewport filtering, profile assignments,
JSON parsing, custom timestamps, automatic inference, new dependencies.

## Definitions
- Field: name, text/number type, regex with matching named capture, case option.
- Missing: no capture; invalid: failed number conversion. Neither is a value.
- Query match: the expression evaluates to true (not false or unknown).
- Coverage: value/missing/invalid counts over eligible evaluated lines, separate
  from scan completion and retained-output limits.

## Existing behavior and evidence
- Core SearchRequest/SearchResult and Infrastructure SearchService provide
  snapshot boundaries, encoding, cancellation, line scopes and generation checks.
- Settings are versioned JSON; PersistedDashboardSnapshotReader reads settings
  without migration/writes and already snapshots them alongside catalog data.
- SearchPanelViewModel owns scope/session state and existing result navigation.
- MCP search provides allowlisted resolution, signed file pagination, budgets,
  sanitization and local/UNC admission gates.

## Decisions and invariants
- Independent rules use first match/first named capture. Built-ins: raw and
  line_number. Names and keywords ignore case. Text comparisons default to
  case-insensitive; numeric literals/conversions use invariant decimal.
- Operators: = != < <= > >= CONTAINS IN IS [NOT] MISSING AND OR NOT and parentheses.
  Precedence: comparisons, NOT, AND, OR. Missing/invalid comparisons are unknown;
  missing checks include invalid values, but diagnostics distinguish them.
- Reject syntax, type, profile errors before log I/O. Limits: 8192 characters
  per expression/pattern, 32 fields, 32 nesting levels, 250ms regex timeout.
- Regex timeout stops the affected file and prevents complete status.
- Compile immutable query/profile snapshots once; retain fields only on retained
  hits. Evaluate before truncation; field output shares existing output budgets.
- Existing text/regex and MCP contracts remain compatible. WQL cannot execute
  tail/filter requests. Profile revisions bind WQL continuation cursors.
- Preserve ordinary search input/options when toggling WQL. Profile changes mark
  affected output stale without changing definitions used by in-flight requests.
- Preview sample text is never persisted. No profile-writing MCP tools.

## Open questions
None; approved scope is decision complete.

## Milestones / issue summary
A. Profiles, persistence, extraction and language.
B. Snapshot search and bounded structured results.
C. Desktop editor/search/details.
D. MCP, documentation and final validation.

## Progress
- [x] A
- [x] B
- [x] C
- [x] D (implementation/automated acceptance; native demonstration blocked below)

## A. Profiles and language
- State: complete.
- Dependencies: existing Core models and settings repositories.
- Purpose: one validated immutable extractor/evaluator for all consumers.
- Expected implementation areas: Core, settings persistence, Core tests.
- Tasks: models, profile validation, extractor, tokenizer/parser, three-valued
  evaluator, settings compatibility and import/export retention.
- Acceptance criteria: captures/conversions/missing logic/precedence/case/limits
  tested; old settings load and new profiles round-trip.
- Focused validation: build Core test project, run new WQL/profile/settings tests.
- Progress/evidence: models/extractor/compiler/settings integration implemented.
  First focused build found a tuple member naming error (Data versus Settings);
  corrected before rerunning validation.
  Core test-project build passed with 0 warnings/errors; WQL/settings tests passed
  46/46. Desktop test-project build passed with 0 warnings/errors; settings
  view-model regression tests passed 28/28.

## B. Snapshot scanner
- State: complete. Dependencies: A.
- Purpose: stream WQL through existing search with bounded retained fields.
- Expected implementation areas: search request/result and SearchService.
- Tasks: optional compiled plan, whole-line matches, coverage accounting,
  cancellation, timeout/error handling, retention budgets, clone coverage.
- Acceptance criteria: unchanged ordinary search; time/line scopes compose;
  generation changes, cancellation and caps never masquerade as completion.
- Focused validation: build/test SearchRequestTests, SearchServiceTests and WQL tests.
- Progress/evidence: Core test-project build passed with 0 warnings/errors;
  scanner/request/WQL selection passed 141/141, including WQL time/line scopes,
  range parity, four encodings, output budgets, caps, mode rejection and timeout.

## C. Desktop
- State: complete. Dependencies: A and B.
- Purpose: configure, preview, query and inspect fields without a new workspace.
- Expected implementation areas: Settings and Search view models/views.
- Tasks: focused profile editor, bounded nonpersisted preview, WQL toggle/profile,
  disk-only controls, state snapshots/staleness and selected-hit fields expander.
- Acceptance criteria: normal input/options survive toggles; settings/import
  retain profiles; existing scoping/navigation/filter behavior remains intact.
- Focused validation: build/test settings, search view-model and layout tests.
- Progress/evidence: desktop test-project build passed with 0 warnings/errors;
  desktop search/settings/result/editor selection passed 194/194. Includes an
  automated real-file desktop walkthrough, preview validity/bounds, WPF editor
  construction/layout, profile deletion/staleness and workspace/input restoration.

## D. MCP and final validation
- State: complete; native visual demonstration remains separately blocked.
  Dependencies: A and B (desktop parity after C).
- Purpose: read-only agent schema discovery/query and tested public behavior.
- Expected implementation areas: catalog snapshots, headless backend, MCP tools,
  public docs, protocol/integration tests.
- Tasks: profile discovery and WQL query contracts, profile-bound cursors,
  existing authorization/budgets/sanitization, examples and stdio validation.
- Acceptance criteria: desktop/MCP parity; no raw path input/leaks; profile edits
  reject continuation; old six tools unchanged; new tools discoverable via stdio.
- Focused validation: backend/catalog/MCP tool/protocol tests and artifact smoke.
- Progress/evidence: MCP query/discovery contracts, profile-bound cursor reuse and
  bounded parsing/value mapping implemented; focused Core build passed with no
  warnings/errors and WQL/MCP/catalog tests passed 80/80.
  Production composition now forwards both new tools, and the real executable
  discovers/calls all eight. An isolated portable-install fixture also discovers
  a saved level/duration profile, queries mixed-validity logs, inspects typed
  fields/diagnostics, and verifies saved stores remain unchanged. Latest focused
  builds passed with zero warnings/errors; 218 Core scanner/backend/catalog/tool
  tests and 8 desktop WQL/stdio tests passed. Final full validation passed as
  recorded below; native walkthrough is permission-blocked.

## Final validation and demonstration
Build then test focused targets per milestone. Finally run `dotnet build
LogReader/LogReader.sln` and `dotnet test LogReader/LogReader.sln --no-build`.
Exercise supported encodings, long lines, scopes/time bounds, cancellation,
append/truncation/rotation, response limits, profile revisions and sanitization.
Demonstrate a level/duration profile with valid/missing/invalid lines, WQL search,
hit inspection and equivalent MCP results. Record actual evidence, never assume.

2026-09-14 final evidence:
- `dotnet build LogReader/LogReader.sln`: passed, 0 errors. The final restore
  reported 8 NU1900 warnings because NuGet vulnerability data could not be fetched
  over SSL, including on retry with normal permissions. No dependency or audit
  policy was changed to hide that environmental warning.
- `dotnet test LogReader/LogReader.sln --no-build` with normal local cache access:
  Core 549/549 and desktop/integration 937/937 passed (1,486 total, none skipped).
- Focused build/test selections passed throughout A–D. Additional real WPF
  binding checks verify profile refresh/deletion selection and selected-result
  level/duration text; editor/desktop walkthrough selection passed 5/5.
- Release self-contained win-x64 single-file publishes of both App and MCP passed
  into the new ignored `LogReader/artifacts/publish/WqlValidation` directory,
  using the existing packaging publish flags. `Validate-PortableArtifact.ps1`
  and `Test-McpStdioArtifact.ps1` both passed against those artifacts.
- The automated desktop walkthrough defines level/duration fields, previews
  valid/missing/invalid lines, queries a real file, inspects fields and staleness.
  Real executable stdio repeats the query against isolated saved settings/logs,
  validates typed fields and parsing counts, and verifies no store writes.
- Native visual interaction was attempted using the computer-use skill, but
  WeezTail launch was not approved. No native screenshot/visual pass is claimed;
  enable that app permission before completing the manual guide walkthrough.
- `git diff --check` passed. Only task-related files are included in local commits.

## Surprises & discoveries
- Desktop search and filter share source selection. WQL uses an effective Disk
  source without overwriting the shared ordinary source, preserving filter state.
- Desktop build initially warned about direct observable backing-field writes;
  replaced these with guarded property restoration. A new toggle test initially
  configured Tail before constructor state initialization reset it; fixed setup
  to select Tail after construction, matching the actual UI sequence.
- Final WPF binding test initially inherited a test-host window icon resource
  relative to the wrong assembly. Giving the host window an explicit neutral
  style, as other WPF tests do, fixed setup; profile-selection refresh passes.
- The isolated stdio installation initially omitted runtime-specific dependency
  files. Copying its runtimes directory fixed the fixture; positive real-process
  WQL execution now passes without elevated permissions.
- An earlier full run passed 545 Core tests but failed three existing desktop
  index-cache tests under sandbox access restrictions. All 20 tests in that
  desktop class passed with local cache access. Final full rerun will use that
  permission rather than change unrelated runtime code.

## Risks and mitigations
- Regex CPU cost: bounded patterns/rules, existing timeout, cancellation per rule.
- Memory/output amplification: share text budgets, do not retain non-hit fields.
- Incorrect completeness: separate extraction statistics from scan status.
- Search state regressions: opt-in execution and explicit session/scope tests.
- Sensitive data: no saved preview samples or diagnostic logging of log values;
  MCP extracted strings follow existing untrusted-content/budget rules.

## Deferred work
All non-goals above; no speculative extension mechanisms.

## Decision log
- 2026-09-13: user approved filtering only, desktop and agents, independent rules,
  per-query profiles, snapshots only, text/number fields and the complete plan.
- 2026-09-14: bound field names/profile IDs to 128 characters and profile names
  to 256 so schema metadata is bounded too. Discovery pages contain at most 50
  schemas. Extraction timeouts have a dedicated safe MCP error code; neither
  regex patterns nor offending log input are exposed in that error.

## Outcomes & retrospective
- Observable outcome: Settings can manage and preview independent text/number
  profiles; desktop snapshot search can run WQL and inspect retained fields and
  extraction coverage; agents can discover profiles and run equivalent read-only
  bounded queries. Existing text/regex search, filters, tails and six MCP tools
  retain their contracts. No dependency was added.
- Changed components: Core profiles/extractor/compiler/output and search models;
  JSON settings and read-only catalog snapshots; snapshot scanner and headless
  backend; desktop Settings/editor/search/state/detail views; MCP registration
  and executable forwarding; unit/integration/protocol tests and user/security/
  developer/packaging documentation.
- Milestone commits A/B/C are 59dca71, 272318a, d28f57c; D is recorded in the local
  commit containing this final plan update. No push was performed.
- Remaining validation risks: native visual walkthrough requires app permission;
  vulnerability audit could not refresh due to NuGet SSL connectivity. Automated
  build/runtime acceptance and published-artifact checks passed.
- Retrospective: shared immutable plans kept desktop/MCP semantics aligned. The
  real-process positive fixture caught a test-install runtime omission that unit
  tests could not expose. Bounded diagnostics and explicit field-timeout errors
  make incomplete scans distinguishable from missing fields.

## Handoff history
- 2026-09-13: implementation started from the approved plan; worktree clean.
- 2026-09-14: resumed after interruption; A/B/C are committed as 59dca71,
  272318a and d28f57c; D changes remain uncommitted and under validation.
- 2026-09-14: resumed implementation finished; all 1,486 automated tests and
  published-artifact checks passed. Native demonstration is permission-blocked;
  the test build and synthetic walkthrough log remain in the ignored artifact
  directory for that follow-up.
