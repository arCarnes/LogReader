# WeezTail Developer Guide

Last updated: 2026-08-05

This guide is for contributors working on the main WeezTail product in `LogReader/`. If you want end-user workflows inside the app, use the [User Guide](./UserGuide.md).

> Naming note: the product is branded **WeezTail** (window title, executable, installer, and user data folder), but the source tree keeps its original `LogReader` identity — the `LogReader/` folder, `LogReader.sln`, the `LogReader.*` projects, and the `LogReader.*` C# namespaces are intentionally unchanged.

## Working Directories

- Repo root: the folder that contains both `LogReader/` and `LogGenerator/`
- Product root: `LogReader/`
- Commands below assume you are in the product root unless noted otherwise

From the repo root, enter the product root with:

```powershell
Set-Location .\LogReader
```

The peer `..\LogGenerator` folder is an internal developer utility and is documented separately in [LogGenerator README](../../LogGenerator/README.md).

## Solution Layout

```text
LogReader.sln
|- LogReader.Core            (net8.0, models + interfaces)
|- LogReader.Infrastructure  (net8.0, services + repositories)
|- LogReader.Mcp             (net8.0, WeezTail.Mcp.exe + MCP stdio composition)
|- LogReader.App             (net8.0-windows, WPF UI)
|- LogReader.Testing         (net8.0, shared test fakes + utilities)
|- LogReader.Core.Tests      (net8.0, core + infrastructure xUnit)
`- LogReader.Tests           (net8.0-windows, app shell + WPF xUnit)
```

Dependency graph:

```text
LogReader.Infrastructure -> LogReader.Core
LogReader.Mcp -> LogReader.Infrastructure + LogReader.Core
LogReader.App -> LogReader.Infrastructure + LogReader.Core
LogReader.Testing -> LogReader.Infrastructure + LogReader.Core
LogReader.Core.Tests -> LogReader.Mcp + LogReader.Infrastructure + LogReader.Core + LogReader.Testing
LogReader.Tests -> LogReader.App + LogReader.Mcp + LogReader.Infrastructure + LogReader.Core + LogReader.Testing
```

## Prerequisites

- Windows, because the app and UI tests target WPF
- .NET SDK 8.x
- WiX Toolset SDK packages restore through the `LogReader.Setup` project when building the MSI package

## Build, Test, Run

From the repo root:

```powershell
Set-Location .\LogReader
```

From the product root, use this repo's normal validation flow:

```powershell
dotnet clean LogReader.sln -m:1
dotnet build LogReader.sln -m:1
dotnet test LogReader.sln
```

Run those as separate blocking steps and in that exact order.

For focused test loops, you can still target individual suites:

```powershell
dotnet test LogReader.Tests\LogReader.Tests.csproj --framework net8.0-windows
dotnet test LogReader.Core.Tests\LogReader.Core.Tests.csproj
```

Launch the app from source with:

```powershell
dotnet run --project LogReader.App\LogReader.App.csproj
```

Notes:

- The standard validation flow is `clean`, then `build`, then `test` as three separate blocking steps in that order.
- Builds and tests restore as needed. The packaging scripts also perform their own explicit restore steps.
- If the app process is running, builds can fail because output files are locked.
- Use `-m:1` for solution clean and build. The current WPF and test project graph is more reliable with serial MSBuild nodes.
- Run `dotnet restore LogReader.sln` before the first build on a machine, after package changes, or before packaging if restore state is missing.
- `LogReader.Tests` targets `net8.0-windows` only.
- `LogReader.Core.Tests` and `LogReader.Testing` target `net8.0`.
- Debug builds of `LogReader.App` run `StopRunningDebugAppInstance.ps1` before build to stop a currently running debug copy of the app.
- Debug builds write a `WeezTail.install.json` beside the built app that points storage at `LogReader/.dev-storage/WeezTail`.

## Test Layout

- `LogReader.Core.Tests/` physically owns the non-WPF tests for `LogReader.Core` and `LogReader.Infrastructure`. If a test can run on plain `net8.0`, put it here.
- `LogReader.Tests/` physically owns the WPF- and shell-facing tests for `LogReader.App`, including UI-only doubles such as `UiTestDoubles.cs`.
- `LogReader.Testing/` is the shared support library for reusable non-WPF fakes and test utilities. Shared stubs now live in `LogReader.Testing/Stubs.cs`, and repository JSON assertions live in `LogReader.Testing/JsonRepositoryAssertions.cs`.
- Prefer `LogReader.Testing/` for reusable helpers that stay free of `System.Windows` and other app-shell-only dependencies. Keep a helper local to one suite when it is tightly coupled to that suite or needs WPF types.
- Do not reintroduce linked source files between the test projects. Each suite should own its tests in its own directory tree.

Parallel test execution note:

- No custom output-path isolation is configured today because each project already writes to its own project-scoped `bin/` and `obj/` folders.
- If the team revisits parallel test execution later, validate test-host and WPF behavior first before adding custom `BaseOutputPath` or `BaseIntermediateOutputPath` overrides.

## Versioning

- Product version metadata is centralized in `Directory.Build.props`.
- MSI release versions must use exactly three version fields and advance one of those fields for each released MSI artifact. Rebuilding a released version can create a different MSI `ProductCode`, so the installer detects same-version related products and blocks them instead of allowing duplicate installed products.
- The current release line is `0.16.8`.

## Release Publish

WeezTail now has one primary release packaging flow from the product root:

Publish all release artifacts:

```powershell
.\packaging\Publish-All.ps1
```

Supporting per-artifact scripts are available under `packaging\scripts`:

Portable package:

```powershell
.\packaging\scripts\Publish-Portable.ps1
```

MSI package:

```powershell
.\packaging\scripts\Build-Msi.ps1
```

Packaging notes:

- Both official packages target `win-x64`
- Both official packages are self-contained
- Portable output is written to `artifacts\publish\Portable`
- Portable release zip is written to `artifacts\publish\WeezTail-<version>-portable-win-x64.zip`
- MSI payload publish output is written to `artifacts\publish\WeezTail.MsiPayload`
- MSI build output is written to `artifacts\installer`
- The WiX installer project lives in `LogReader.Setup/` and is not included in `LogReader.sln`
- Portable packaging publishes `WeezTail.exe` and `WeezTail.Mcp.exe`, then copies `packaging/Portable.WeezTail.install.json` beside them
- Portable packaging validates the publish directory and release zip for required files, required `Data` and `Cache` directories, portable install config values, and absence of `.pdb` files.
- Portable and MSI-payload packaging run `packaging/scripts/Test-McpStdioArtifact.ps1` against the published `WeezTail.Mcp.exe`. The smoke initializes MCP, verifies the exact five-tool surface, calls `server_status`, confirms protocol-only stdout, closes stdin, and requires a clean exit.
- MSI packaging publishes both executables and copies `packaging/Msi.WeezTail.install.json` beside them
- MSI packaging runs `packaging/scripts/Validate-MsiIdentity.ps1` after build to confirm `ProductVersion`, `ProductCode`, `UpgradeCode`, and same-version blocking rows in the MSI tables.
- MSI packaging runs `packaging/scripts/Validate-MsiShortcuts.ps1` after build to confirm per-user non-advertised shortcut rows and HKCU shortcut component key paths.

Troubleshooting MSI install failures:

```powershell
msiexec /i .\artifacts\installer\WeezTail.Setup.msi /l*v! .\artifacts\installer\WeezTail.Setup.install.log
```

Search the resulting log for `Return value 3`. Storage-folder selection now happens in the app on first launch rather than in the installer.

## Architecture Summary

WeezTail uses a layered architecture with MVVM in the app project:

- `LogReader.Core`: models, enums, and interfaces
- `LogReader.Infrastructure`: service and repository implementations
- `LogReader.Mcp`: dedicated `WeezTail.Mcp.exe`, WPF-free stdio transport, fixed tool registration, and headless query composition
- `LogReader.App`: views, viewmodels, converters, and startup wiring
- `LogReader.Testing`: shared test fakes and utilities for the test projects

Desktop startup remains code-wired rather than container-driven and uses the generated WPF entry point. Its composition is split across:

- `LogReader.App/App.xaml.cs`: WPF entry point, exception handling, and cleanup
- `LogReader.App/Services/AppStartupRunner.cs`: single-instance gating, storage readiness, persisted-state recovery, and startup error flow
- `LogReader.App/Services/AppBootstrapper.cs`: composition initialization
- `LogReader.App/Services/AppCompositionBuilder.cs`: concrete repository and service graph construction

## Startup Flow

- `SingleInstanceCoordinator` prevents a second app instance for the same Windows user. A second launch shows an informational dialog and exits early.
- `StartupStorageCoordinator` resolves the storage root and opens the first-launch storage picker for MSI installs when needed.
- `AppStartupRunner` retries startup after persisted-state recovery when saved JSON is invalid, then surfaces a recovery summary dialog.
- `AppBootstrapper` builds and initializes `MainViewModel` before the main window is shown.
- The WPF project does not reference the MCP project or SDK and does not host an MCP listener or share its private sessions with MCP clients.

## MCP Log Server

The accepted design and invariants are recorded in [MCP Log Server Architecture](./McpLogServerArchitecture.md). User setup and tool behavior are in the [MCP Log Server Guide](./McpLogServerGuide.md), the reviewed threat boundaries and residual risks are in [MCP Security and Resilience Model](./McpSecurityModel.md), normal-product effects are tracked in [MCP Mainline Impact Analysis](./McpMainlineImpact.md), and the repeatable timing/memory evidence is in [MCP Performance and Mainline Measurements](./McpPerformanceMeasurements.md).

Project boundaries:

- `LogReader.Core` owns SDK-independent configured-target, tree, request, result, error, status, limit, and cursor contracts.
- `LogReader.Infrastructure` owns non-interactive storage resolution, immutable persisted snapshot reading, target authorization, bounded headless queries, and owner-scoped index caches.
- `LogReader.Mcp` produces `WeezTail.Mcp.exe` and owns the official `ModelContextProtocol.Core` adapter, exact five-tool registration, stdio lifecycle, and headless backend composition. It must remain free of App and WPF references.
- `LogReader.App` uses the generated WPF entry point and has no MCP project or SDK dependency.

Cache ownership is process-scoped. Every MCP process has a unique owner directory and lifetime lock; cleanup can remove a stale owner only after acquiring its lock. UI sessions remain independent and keep their existing behavior. Never map, delete, or infer ownership of another process's `idx_*.bin` files.

Contract evolution rules:

- Keep public request/result DTOs independent of MCP SDK attributes and types.
- Keep tool names, structured schemas, annotations, bounds, documentation, and tests synchronized.
- Accept typed configured IDs only; re-resolve current persisted membership before every file operation or retry.
- Preserve protocol-only stdout. Diagnostics must be sanitized and use stderr or the existing diagnostic path.
- Do not add arbitrary-path inputs, whole-log resources, mutation tools, remote transport, interactive storage/migration/recovery, or UI activation without a new reviewed architecture decision.

Focused MCP validation:

```powershell
dotnet test LogReader.Core.Tests\LogReader.Core.Tests.csproj --filter "FullyQualifiedName~Mcp|FullyQualifiedName~ConfiguredLog|FullyQualifiedName~HeadlessLog"
powershell -NoProfile -ExecutionPolicy Bypass -File .\packaging\scripts\Publish-Portable.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\packaging\scripts\Test-McpStdioArtifact.ps1 -ExecutablePath .\artifacts\publish\Portable\WeezTail.Mcp.exe
```

Before release, also run the full solution build/tests and `packaging\Publish-All.ps1`. Record executable, portable zip, MSI, default-UI startup/idle, and representative headless request measurements. Active MCP client processes must be stopped before installer replacement testing.

## Shell Edit Map

- `LogReader.App/App.xaml.cs`: WPF entry point, startup exception handling, and cleanup.
- `LogReader.App/Services/AppCompositionBuilder.cs`, `LogReader.App/Services/AppBootstrapper.cs`, and `LogReader.App/Services/AppStartupRunner.cs`: composition, initialization, single-instance/storage gating, and startup recovery flow.
- `LogReader.App/ViewModels/MainViewModel.cs`: shell-wide properties, lifecycle settings, and shared command entry points.
- `LogReader.App/ViewModels/MainViewModel.Scope.cs`: scope switching, filtered tab snapshots, auto-scroll sync, and visibility refresh behavior.
- `LogReader.App/ViewModels/MainViewModel.Dashboard.cs`: dashboard CRUD, import/export, file membership commands, modifier actions, and dashboard reload behavior.
- `LogReader.App/ViewModels/MainViewModel.NavigationSettings.cs`: settings dialog flow, search-result navigation, and modifier label formatting.
- `LogReader.App/ViewModels/MainViewModel.Recovery.cs`: persisted-state recovery refresh and recovered-store reload behavior.
- `LogReader.App/ViewModels/SearchPanelViewModel.cs` and `LogReader.App/ViewModels/FilterPanelViewModel.cs`: shared target/source state, search result caching, tail search/filter lifecycle, and stale-output handling.
- `LogReader.App/ViewModels/SettingsViewModel.cs` and `LogReader.App/Views/SettingsWindow.xaml(.cs)`: settings dialog state, validation, and persistence of general, highlight, and date-pattern options.
- `LogReader.App/ViewModels/StorageSetupViewModel.cs` and `LogReader.App/Views/StorageSetupWindow.xaml(.cs)`: first-launch MSI storage folder selection and validation.
- `LogReader.App/Services/DashboardWorkspaceService.cs`: facade over dashboard tree, membership, import/export, and activation services.
- `LogReader.App/Services/DashboardActivationService.cs` and `LogReader.App/Services/DashboardOpenCoordinator.cs`: dashboard open/load behavior, while `MainViewModel` owns shell-local scope filtering and dashboard selection rules.
- `LogReader.App/Services/DashboardModifierService.cs`: date-shift modifier expansion and effective-path remapping for dashboards and Ad Hoc scope.
- `LogReader.App/Services/DashboardMembershipService.cs` and `LogReader.App/Views/BulkOpenDashboardPathsWindow.xaml(.cs)`: bulk path parsing, wildcard expansion, preview, and dashboard membership registration.
- `LogReader.App/Services/ImportedViewPathTrustAnalyzer.cs`: trust assessment for imported dashboard paths, including the UNC-path exception.
- `LogReader.App/Services/TabWorkspaceService.cs`: tab lifecycle, activation, reopen-state caching, visibility-based tailing, ordering, and disposal.
- `LogReader.App/Services/FileSession.cs` and `LogReader.App/Services/FileSessionRegistry.cs`: shared file-backed state, encoding/session ownership, and warm-session retention.
- `LogReader.App/Services/LogViewportService.cs`: asynchronous viewport reads, filtered viewport projection, line highlighting, and stale viewport request suppression.
- `LogReader.App/Services/LogTailCoordinator.cs`: tab-local tail event coordination between file sessions and viewport/filter/search refresh behavior.
- `LogReader.App/Services/WorkspaceHosts.cs`: shell host wiring between workspace services and shell-facing view models.
- `LogReader.App/Views/MainWindow.xaml` and `LogReader.App/Views/MainWindow.xaml.cs`: top-level shell composition and window-only event wiring.
- Focused views under `LogReader.App/Views/` such as `DashboardTreeView`, `TabStripView`, `LogViewportView`, and `SearchWorkspaceView`: region-specific layout, context menus, and shell-region behavior.

## Core Models and Interfaces

Key models in `LogReader.Core/Models` include:

- `LogFileEntry`
- `LogGroup` and `LogGroupKind`
- `ViewExport` and `ViewExportGroup`
- `FileEncoding`
- `AppSettings`
- `ReplacementPattern`
- `LineHighlightRule`
- `LineIndex` and `MappedLineOffsets`
- `SearchRequest`, `SearchResult`, and `SearchHit`
- `TimestampNavigationResult`

Important interfaces in `LogReader.Core/Interfaces` include:

- `IEncodingDetectionService`
- `ILogReaderService`
- `ISearchService`
- `IFileTailService`
- `ILogFileRepository`
- `ILogGroupRepository`
- `ISettingsRepository`

Encoding notes:

- `EncodingHelper` maps `FileEncoding` to .NET encodings.
- ANSI uses Windows-1252 via `CodePagesEncodingProvider`.
- `LogTabViewModel` currently exposes `Auto`, `UTF-8`, `UTF-16`, `UTF-16 BE`, and `ANSI` in the toolbar, while auto-detection can still resolve BOM-backed UTF-8.
- `FileSessionKey` normalizes `filePath + requestedEncoding`, so `Auto` and manual `UTF-8` intentionally do not share a session.
- Changing a tab's encoding rebinds that tab to a different `FileSession` when the session key changes.

Search and filter notes:

- `SearchRequest` carries source mode, usage, timestamp bounds, optional line ranges, and per-file line scopes.
- Filter inversion is represented with `SearchLineScopeMode.Exclude` when search runs against an active inverted filter.
- Timestamp filtering uses `TimestampParser`, which accepts ISO-8601, `yyyy-MM-dd HH:mm:ss`, fractional-second variants, and time-only values.
- `SearchResult.HasParseableTimestamps` distinguishes a valid zero-hit time range from a file with no parseable timestamps.

Settings notes:

- `AppSettings` currently persists the default open directory, log font family, log font size, dashboard full-path labels, search result match highlighting, line highlight rules, recent custom highlight colors, and date rolling patterns.
- `LogFileEntry` is a known-file catalog record with a stable ID, file path, and `LastOpenedAt` timestamp. It is not a saved open-tab session record.

## Infrastructure Services

### ChunkedLogReaderService

- Uses 64 KB buffered scanning
- Detects BOM markers for UTF-8 and UTF-16 variants
- Stores newline offsets for random access reads
- Treats empty and BOM-only files as `LineCount == 0`
- Extends indexes for appended data and rebuilds on truncation or rotation

### SearchService

- Streams file content line by line with a 256 KB `StreamReader` buffer
- Supports plain text and regex matching
- Returns one search result hit per matching line, with match spans attached for highlighting
- Supports timestamp-bounded searches and filters through `SearchRequest.FromTimestamp` and `SearchRequest.ToTimestamp`
- Applies include-only or exclude line scopes so search can run against the current filtered view
- Can cap retained line text and per-file hit counts for UI-facing result sets
- Uses a 250 ms regex timeout
- Uses adaptive bounded parallelism for multi-file search and filter application

### FileTailService

- Polls each tailed file every 250 ms
- Raises append events when file size grows
- Raises rotation events when identity changes, the file shrinks, or it disappears and reappears
- Tracks active tails in a `ConcurrentDictionary<string, TailState>`

### FileSession / FileSessionRegistry

- `TabWorkspaceService` owns the workspace-wide `FileSessionRegistry`.
- `FileSessionRegistry` keys sessions by normalized file path plus requested encoding and hands tabs `FileSessionLease` instances.
- `FileSession` owns shared file-backed state: encoding resolution, line index lifetime, load/error state, search content version, and tail coordination.
- `LogTabViewModel` keeps tab-local state: viewport, filter session, navigation, pinning, visibility timestamps, and local status text.
- Released `FileSession` instances now stay warm briefly for same-key reopen and are swept during lifecycle maintenance.
- `TabWorkspaceService` also keeps a short-lived in-memory reopen cache per `scope + filePath` so recent same-scope closes can restore requested encoding, pin state, viewport, and filter state without touching durable storage.

## Persistence and Storage

Repositories in `LogReader.Infrastructure/Repositories`:

- `JsonLogFileRepository` for `logfiles.json`
- `JsonLogGroupRepository` for `loggroups.json`
- `JsonSettingsRepository` for `settings.json`
- `JsonStore` for shared JSON load and save helpers

Storage behavior:

- Packaged builds resolve storage from the shared `WeezTail.install.json` beside `WeezTail.exe` and `WeezTail.Mcp.exe`
- Portable packages use the executable directory as the storage root
- New MSI installs use `storageMode = PerUserChoice` and prompt on first launch for the current user's storage root
- Existing MSI installs with `storageMode = Absolute` keep using the configured absolute storage root
- `Data` and `Cache` always live under the same storage root
- MSI per-user selections are stored at `%LOCALAPPDATA%\WeezTailSetup\WeezTail.msi-user.json`
- Before removing a related LogReader MSI, setup records any legacy per-user selection or absolute `LogReader.install.json` root under the WeezTail selection path
- At runtime, a missing WeezTail selection also falls back to `%LOCALAPPDATA%\LogReaderSetup\LogReader.msi-user.json` or an existing `%LOCALAPPDATA%\LogReader` root
- Debug runs from source normally use `LogReader/.dev-storage/WeezTail` because the app project writes a debug install config after build
- Debug builds can still fall back to `%LOCALAPPDATA%\WeezTail` when no install config is present and no source solution root can be found
- Writes go to `*.tmp` first and then move into place
- Repository JSON is written as a versioned envelope with `schemaVersion` and `data`
- Legacy repository payloads are rewritten to the current versioned envelope on successful load
- JSON uses camelCase, indented formatting, and string enums
- `ImportViewAsync` returns `null` when the import file is missing
- Malformed import JSON throws `InvalidDataException` with context
- `settings.json` stores UI and pattern settings
- `loggroups.json` stores the dashboard tree, memberships, and sort order
- `logfiles.json` stores the known-file catalog that backs dashboard memberships and import remapping; startup does not reopen tabs from it

## Sensitive Workflows

- Startup is intentionally guarded. `AppStartupRunner` coordinates single-instance enforcement, storage readiness, and persisted-state recovery before the main window is shown.
- Persisted-state recovery is explicit. Invalid `settings.json`, `logfiles.json`, or `loggroups.json` content is moved aside as a timestamped `.corrupt-*` backup, a sibling `.note.txt` is written, and the app surfaces the recovery details to the user.
- Dashboard orchestration is intentionally split. `DashboardImportService` owns import/export materialization, `DashboardWorkspaceService` is the facade used by the shell, `DashboardTreeService` owns tree CRUD/filtering, and `DashboardActivationService` coordinates member refresh plus open/load behavior.
- Modifier and dashboard-open behavior are sensitive to scope state. If you touch dashboard selection, modifier labels, effective paths, or the member refresh flow, re-check both `FilteredTabs` behavior and dashboard loading cancellation.
- Imported dashboard views can carry non-standard paths. UNC paths are allowed without an extra warning, but relative, drive-relative, and device-prefixed paths trigger a trust confirmation before the import is applied.
- Storage safety rules should stay aligned between runtime and uninstall cleanup. Runtime validation rejects protected roots through `StoragePathValidator`; installer cleanup should only delete `Data` and `Cache` beneath a resolved, non-protected storage root and should skip cleanup when the root is blank or malformed.

## Runtime Data Flow

### Open a File

1. `MainViewModel.OpenFilePathAsync`
2. Resolve or create `LogFileEntry`
3. `TabWorkspaceService` acquires a `FileSessionLease` and creates `LogTabViewModel`
4. `LogTabViewModel.LoadAsync` delegates file-backed loading to `FileSession`, then loads the initial viewport

### Append or Rotation

1. `FileTailService` raises an event
2. `FileSession` updates or rebuilds the line index and coordinates tail callbacks for the active tab client
3. The visible viewport refreshes when needed

### Search

1. `SearchPanelViewModel.ExecuteSearch`
2. Resolve scope to the selected tab when `Current tab` is selected, or to the currently visible `FilteredTabs` set when `All open tabs` is selected
3. Choose `DiskSnapshot` or `Tail`; disk results can then opt into `Monitor New Matches`
4. Navigate from a result through `MainViewModel.NavigateToLineAsync`

### Filter

1. `FilterPanelViewModel.ApplyFilter`
2. `SearchService.SearchFileAsync` computes initial matching lines
3. `LogTabViewModel.ApplyFilterAsync` activates the filtered line map
4. Tail updates merge new matching lines into the filtered view

## UI Notes

Primary viewmodels in `LogReader.App/ViewModels`:

- `MainViewModel`
- `LogTabViewModel`
- `SearchPanelViewModel`
- `FilterPanelViewModel`
- `LogGroupViewModel`
- `SettingsViewModel`
- `HighlightRuleViewModel`
- `FileSearchResultViewModel`
- `SearchHitViewModel`
- `LogLineViewModel`

Current converters in `LogReader.App/Converters`:

- `BoolToVisibilityConverter`
- `HexColorToBrushConverter`
- `LessThanConverter`

Threading and safety notes:

- JSON repositories serialize mutations with `SemaphoreSlim(1,1)`
- `FileSession` guards line-index swaps and disposal with `_lineIndexLock`
- `LogTabViewModel` projects session-backed state and ignores stale property notifications from detached sessions
- `MainViewModel` uses cycle-safe traversal for dashboard tree building and file ID resolution
