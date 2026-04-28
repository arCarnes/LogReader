# Code Review Fix Tracker

Date: 2026-04-28

Source: multi-agent whole-repository code review.

## Suggested Implementation Order

| Order | Group | Priority | Plan | Issues Included | Rationale |
| --- | --- | --- | --- | --- | --- |
| 1 | Tail/session correctness | P1 | [01-tail-session-index-plan](2026-04-28-01-tail-session-index-plan.md) | Path-level tail ownership; appended offset overflow | Prevent stale live views first, then address the memory retention created by the same long-running tail lifecycle. |
| 2 | Search/filter memory | P1 | [02-search-filter-memory-plan](2026-04-28-02-search-filter-memory-plan.md) | Filter full-line materialization; retained search lines; tail catch-up range reads; duplicate filter line sets | These share `SearchResult`, `SearchRequest`, filter snapshots, and result presentation, so a single pass can avoid incompatible partial fixes. |
| 3 | UNC and path handling | P2 | [03-unc-path-io-plan](2026-04-28-03-unc-path-io-plan.md) | `FileShare.Delete`; transient tail metadata errors; extended path wildcard parsing; inaccessible path reporting | These are all local/UNC reliability fixes and should be validated with file-system-focused tests. |
| 4 | Line and rotation correctness | P2 | [04-line-rotation-culture-plan](2026-04-28-04-line-rotation-culture-plan.md) | CR-only line endings; in-place rewrite detection; culture-invariant regex | Smaller correctness fixes with targeted parser/index/search tests. |
| 5 | UI responsiveness | P2/P3 | [05-ui-responsiveness-plan](2026-04-28-05-ui-responsiveness-plan.md) | Dispatcher result merge; dashboard member refresh; flat search collection lookup | These are UI performance changes that should be profiled or tested around large collections. |
| 6 | Architecture/test follow-up | P2/P3 | [06-architecture-test-followup-plan](2026-04-28-06-architecture-test-followup-plan.md) | Composition/dispatcher coupling; dashboard code-behind; coverage gaps and brittle tests | Lower urgency because they mainly reduce future risk, but they support the higher-priority fixes. |

## Tracker

| Status | Priority | Issue | Group | Suggested Pass |
| --- | --- | --- | --- | --- |
| Fixed | P1 | Path-level tailing can be stopped by another session for the same file | Tail/session correctness | Pass 1 |
| Fixed | P1 | Appended line offsets grow unbounded on the managed heap | Tail/session correctness | Pass 1 |
| Fixed | P1 | Filter application materializes full matching log lines unnecessarily | Search/filter memory | Pass 2 |
| Fixed | P1 | Broad search results can retain large portions of log files | Search/filter memory | Pass 2 |
| Fixed | P2 | Tail catch-up reads the full appended range even when only the viewport tail is needed | Search/filter memory | Pass 2 |
| Fixed | P2 | Searching within filters duplicates large line-number sets | Search/filter memory | Pass 2 |
| Not started | P2 | Log reads can block producer-side delete/rename rotation | UNC and path handling | Pass 3 |
| Not started | P2 | Transient UNC metadata failures permanently stop tailing | UNC and path handling | Pass 3 |
| Not started | P2 | Extended Windows paths are treated as wildcard patterns | UNC and path handling | Pass 3 |
| Not started | P3 | Dashboard existence checks collapse inaccessible paths into missing paths | UNC and path handling | Pass 3 |
| Not started | P2 | Indexing and search disagree on CR-only line endings | Line and rotation correctness | Pass 4 |
| Not started | P2 | Rotation detection misses in-place rewrites that regain size before polling | Line and rotation correctness | Pass 4 |
| Not started | P3 | Regex case-insensitive matching is culture-sensitive | Line and rotation correctness | Pass 4 |
| Not started | P2 | Completed searches apply all result materialization on the dispatcher | UI responsiveness | Pass 5 |
| Not started | P2 | Dashboard member refresh performs repeated per-item UI updates | UI responsiveness | Pass 5 |
| Not started | P3 | Search result virtualized collection uses linear segment lookup | UI responsiveness | Pass 5 |
| Not started | P2 | Composition and dispatcher responsibilities are split across viewmodels/services | Architecture/test follow-up | Pass 6 |
| Not started | P3 | Dashboard tree code-behind is large and tightly coupled | Architecture/test follow-up | Pass 6 |
| Not started | P3 | Search/filter request construction and cloning are duplicated | Architecture/test follow-up | Pass 6 |

## Validation Baseline

Before starting each implementation pass:

```powershell
dotnet build LogReader.sln
dotnet test LogReader.sln
```

Run narrower tests during each pass first, then the full build/test pair before closing the pass.
