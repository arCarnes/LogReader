# Agent D - Performance and Scalability

## Scope

This pass focused on repeated I/O, lookup patterns, polling behavior, hot-path regex work, UI update costs, and places where the current implementation is likely to hit scaling cliffs.

## Findings

### [MEDIUM] Dashboard workflows repeatedly reload the full file index
- Confidence: High
- Location: `LogReader.App/Services/DashboardWorkspaceService.cs`, `LogReader.Infrastructure/Repositories/JsonLogFileRepository.cs`
- Evidence: dashboard open and refresh paths resolve files through repository calls that each flow through `GetAllAsync`.
- Why it matters: the cost of opening a dashboard grows with total tracked files, not just the files used by that dashboard.
- Fix direction: batch-load file metadata once per dashboard operation and add targeted lookup APIs.

### [MEDIUM] Tailing uses one polling loop per file and tail search adds another loop
- Confidence: High
- Location: `LogReader.Infrastructure/Services/FileTailService.cs`, `LogReader.App/ViewModels/SearchPanelViewModel.cs`
- Evidence: each tailed file gets its own watcher loop and tail-search refresh adds a second timer-driven path.
- Why it matters: active workspaces scale CPU wakeups and file-system polling with file count.
- Fix direction: drive search refresh from tail events where possible and consolidate polling responsibilities.

### [MEDIUM] Search result materialization can overwhelm the UI on dense matches
- Confidence: High
- Location: `LogReader.Infrastructure/Services/SearchService.cs`, `LogReader.App/ViewModels/FileSearchResultViewModel.cs`
- Evidence: wide searches materialize all hits and then add them into observable collections item by item.
- Why it matters: large result sets pay both allocation cost and repeated UI notification cost.
- Fix direction: batch insert results, cap or page large result sets, and avoid materializing more hits than the UI can display.

### [MEDIUM] Highlight regex evaluation lacks the timeout guard already used by search
- Confidence: High
- Location: `LogReader.App/Helpers/LineHighlighter.cs`
- Evidence: highlight rules use regex matching without the timeout applied in search and filter code.
- Why it matters: a single pathological highlight rule can stall rendering repeatedly across visible lines.
- Fix direction: share one regex execution policy with a timeout across search, filter, and highlighting.

### [LOW] Serial retry behavior can amplify dashboard-open latency
- Confidence: Medium
- Location: `LogReader.App/Services/DashboardWorkspaceService.cs` - `OpenGroupFilesAsync`
- Evidence: failed file opens are retried serially per file.
- Why it matters: transient errors across multiple files can turn one dashboard activation into a long blocking path.
- Fix direction: narrow retries to cases that can actually recover, and consider bounded parallelism or faster fail-fast behavior.

### [LOW] Whole-store JSON persistence may become a throughput bottleneck as state grows
- Confidence: Medium
- Location: `JsonSettingsRepository.cs`, `JsonLogFileRepository.cs`, `JsonLogGroupRepository.cs`
- Evidence: common mutations rewrite full JSON files rather than applying targeted updates.
- Why it matters: performance cost grows with the size of persisted state and makes write amplification harder to hide.
- Fix direction: keep the current format if state remains small, but monitor growth and consider a more incremental store if usage expands.

## Performance Theme

The current code is likely fine for small workspaces, but the main scaling cliffs are already visible: whole-store lookups, stacked polling loops, and per-item UI updates. If the product starts handling larger dashboards or many tailed files, these patterns will compound quickly.
