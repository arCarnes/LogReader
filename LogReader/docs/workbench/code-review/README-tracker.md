# Code Review Remediation Tracker

Created: 2026-05-22

This tracker breaks the multi-agent review findings into focused passes that can be assigned to Codex one at a time. Each pass should be implemented on its own, validated, and then marked complete here.

## Priority Notation

- P0: Critical correctness, data-loss, or crash issue that can break core usage immediately.
- P1: High-impact issue likely to cause incorrect results, hangs, severe performance problems, or memory leaks.
- P2: Medium-impact issue affecting maintainability, reliability, performance, or testability.
- P3: Low-impact cleanup, style, or minor maintainability issue.

The priority is the severity bucket. The title is the short name of the individual finding inside that bucket.

## Passes

| Status | Pass | Plan | Findings Covered |
| --- | --- | --- | --- |
| Partially complete | Pass 1 | [01-correctness-and-file-io.md](01-correctness-and-file-io.md) | Disposed line index after failed truncate rebuild; bulk path error reporting; path identity consistency fixed. UNC probe containment intentionally skipped. |
| Partially complete | Pass 2 | [02-search-tail-memory-bounds.md](02-search-tail-memory-bounds.md) | Tail filter catch-up and retained search line text fixed. Virtualized search row cache and highlight regex cache intentionally skipped. |
| Not started | Pass 3 | [03-dashboard-ui-scalability.md](03-dashboard-ui-scalability.md) | Dashboard member virtualization; broad member collection refresh; O(n2) dashboard tree rebuild |
| Not started | Pass 4 | [04-search-filter-architecture.md](04-search-filter-architecture.md) | Duplicated search/filter predicate logic; workspace context coupling |
| Not started | Pass 5 | [05-test-hardening.md](05-test-hardening.md) | Dashboard cancellation tests; probe classification tests; deterministic search cancellation tests; import cleanup tests |

## Progress Log

| Date | Pass | Change | Validation | Notes |
| --- | --- | --- | --- | --- |
| 2026-05-22 | Setup | Created remediation tracker and pass plans. | Not run; documentation-only change. | Ready for implementation passes. |
| 2026-05-22 | Pass 1 | Fixed truncate rebuild line-index ownership, bulk path preview error reporting, and case-insensitive bulk path dedupe. | `dotnet test LogReader.Tests\LogReader.Tests.csproj --filter "DashboardWorkspace\|FileSession\|SessionThreadingLifetime"`; `dotnet test LogReader.Core.Tests\LogReader.Core.Tests.csproj --filter "LineIndex\|Rotation"`; `dotnet build LogReader.sln`; `dotnet test LogReader.sln`. | UNC probe containment / not truly cancellable issue skipped for now, so Pass 1 remains partially complete. |
| 2026-05-22 | Pass 2 | Processed tail filter catch-up in 2,000-line chunks and capped display search retained line text at 8,192 characters. | `dotnet test LogReader\LogReader.Tests\LogReader.Tests.csproj --filter "LogFilterSession\|SearchPanel"`; `dotnet test LogReader\LogReader.Core.Tests\LogReader.Core.Tests.csproj --filter "SearchService\|SearchRequest"`; `dotnet build LogReader\LogReader.sln`; `dotnet test LogReader\LogReader.sln`. | Virtualized search row cache and highlight regex cache skipped for now, so Pass 2 remains partially complete. |

## Definition of Done

For each pass:

1. Implement only the findings assigned to that pass.
2. Add or update focused xUnit coverage for behavior changed by the pass.
3. Run the narrow validation first, then `dotnet build LogReader.sln` and `dotnet test LogReader.sln`.
4. Update this tracker with completion status, validation results, and any residual risk.
5. Keep commits scoped to the pass.
