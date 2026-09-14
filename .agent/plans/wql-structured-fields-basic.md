# Basic WQL and structured fields — Execution Plan

This is a living document. Keep Progress, Surprises & Discoveries, Decision Log,
and Outcomes & Retrospective current throughout execution.

## Document control
- Owner: Codex; approved implementation requested 2026-09-13.
- Branch: `explore/wql-structured-fields`. No push, amend, or unrelated changes.
- Source: user-approved Basic WQL and structured fields plan in this task.

## Resume checkpoint
- Current milestone: D, MCP and final validation.
- Next action: add profile discovery, profile-bound WQL queries and protocol tests.
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
- [ ] D

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
- State: in progress. Dependencies: A and B (desktop parity after C).
- Purpose: read-only agent schema discovery/query and tested public behavior.
- Expected implementation areas: catalog snapshots, headless backend, MCP tools,
  public docs, protocol/integration tests.
- Tasks: profile discovery and WQL query contracts, profile-bound cursors,
  existing authorization/budgets/sanitization, examples and stdio validation.
- Acceptance criteria: desktop/MCP parity; no raw path input/leaks; profile edits
  reject continuation; old six tools unchanged; new tools discoverable via stdio.
- Focused validation: backend/catalog/MCP tool/protocol tests and artifact smoke.
- Progress/evidence: not started.

## Final validation and demonstration
Build then test focused targets per milestone. Finally run `dotnet build
LogReader/LogReader.sln` and `dotnet test LogReader/LogReader.sln --no-build`.
Exercise supported encodings, long lines, scopes/time bounds, cancellation,
append/truncation/rotation, response limits, profile revisions and sanitization.
Demonstrate a level/duration profile with valid/missing/invalid lines, WQL search,
hit inspection and equivalent MCP results. Record actual evidence, never assume.

## Surprises & discoveries
- Desktop search and filter share source selection. WQL uses an effective Disk
  source without overwriting the shared ordinary source, preserving filter state.
- Desktop build initially warned about direct observable backing-field writes;
  replaced these with guarded property restoration. A new toggle test initially
  configured Tail before constructor state initialization reset it; fixed setup
  to select Tail after construction, matching the actual UI sequence.

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

## Outcomes & retrospective
Implementation and validation pending.

## Handoff history
- 2026-09-13: implementation started from the approved plan; worktree clean.
