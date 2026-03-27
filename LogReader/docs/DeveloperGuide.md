# LogReader Developer Guide

Last updated: 2026-03-26

This guide is for contributors working on the main LogReader product in `LogReader/`. If you want end-user workflows inside the app, use the [User Guide](./UserGuide.md).

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
|- LogReader.App             (net8.0-windows, WPF UI)
|- LogReader.Testing         (net8.0, shared test fakes + utilities)
|- LogReader.Core.Tests      (net8.0, core + infrastructure xUnit)
`- LogReader.Tests           (net8.0-windows, app shell + WPF xUnit)
```

Dependency graph:

```text
LogReader.Infrastructure -> LogReader.Core
LogReader.App -> LogReader.Infrastructure + LogReader.Core
LogReader.Testing -> LogReader.Infrastructure + LogReader.Core
LogReader.Core.Tests -> LogReader.Infrastructure + LogReader.Core + LogReader.Testing
LogReader.Tests -> LogReader.App + LogReader.Infrastructure + LogReader.Core + LogReader.Testing
```

## Prerequisites

- Windows, because the app and UI tests target WPF
- .NET SDK 8.x

## Build, Test, Run

From the repo root:

```powershell
Set-Location .\LogReader
```

From the product root:

```powershell
dotnet clean LogReader.sln -m:1
dotnet restore LogReader.sln
dotnet build LogReader.sln -m:1

dotnet test LogReader.Tests\LogReader.Tests.csproj --framework net8.0-windows
dotnet test LogReader.Core.Tests\LogReader.Core.Tests.csproj

dotnet run --project LogReader.App\LogReader.App.csproj
```

Notes:

- If the app process is running, builds can fail because output files are locked.
- Use `-m:1` for solution clean and build. The current WPF and test project graph is more reliable with serial MSBuild nodes.
- `LogReader.Tests` targets `net8.0-windows` only.
- `LogReader.Core.Tests` and `LogReader.Testing` target `net8.0`.

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
- The current release line is `0.9.2`.

## Release Publish

LogReader now has two supported packaging flows from the product root:

Portable package:

```powershell
.\packaging\Publish-Portable.ps1
```

MSI package:

```powershell
.\packaging\Build-Msi.ps1
```

Packaging notes:

- Both official packages target `win-x64`
- Both official packages are self-contained
- Portable output is written to `artifacts\publish\Portable`
- MSI payload publish output is written to `artifacts\publish\LogReader.MsiPayload`
- MSI build output is written to `artifacts\installer`
- The WiX installer project lives in `LogReader.Setup/` and is not included in `LogReader.sln`
- Portable packaging copies `packaging/Portable.LogReader.install.json` beside `LogReader.exe`
- MSI packaging copies `packaging/Msi.LogReader.install.json` beside `LogReader.exe`

Troubleshooting MSI install failures:

```powershell
msiexec /i .\artifacts\installer\LogReader.Setup.msi /l*v! .\artifacts\installer\LogReader.Setup.install.log
```

Search the resulting log for `Return value 3`. Storage-folder selection now happens in the app on first launch rather than in the installer.

## Architecture Summary

LogReader uses a layered architecture with MVVM in the app project:

- `LogReader.Core`: models, enums, and interfaces
- `LogReader.Infrastructure`: service and repository implementations
- `LogReader.App`: views, viewmodels, converters, and startup wiring
- `LogReader.Testing`: shared test fakes and utilities for the test projects

Startup wiring is manual in `LogReader.App/App.xaml.cs`.

## Shell Edit Map

- `LogReader.App/App.xaml.cs`: composition root, service wiring, and main window startup.
- `LogReader.App/Services/AppBootstrapper.cs` and `LogReader.App/Services/AppStartupRunner.cs`: startup sequencing, storage gating, and first-window flow.
- `LogReader.App/ViewModels/MainViewModel.cs`: shell commands and orchestration across tabs, dashboard state, search, and settings.
- `LogReader.App/Services/DashboardWorkspaceService.cs`: dashboard tree ownership, selection flow, and dashboard-backed workspace state.
- `LogReader.App/Services/DashboardModifierService.cs`: date-shift modifier expansion and effective-path remapping for dashboards and Ad Hoc scope.
- `LogReader.App/Services/DashboardMembershipService.cs` and `LogReader.App/Views/BulkOpenDashboardPathsWindow.xaml(.cs)`: bulk path parsing, preview, and dashboard membership registration.
- `LogReader.App/Services/ImportedViewPathTrustAnalyzer.cs`: trust assessment for imported dashboard paths, including the UNC-path exception.
- `LogReader.App/Services/TabWorkspaceService.cs`: tab lifecycle, activation, ordering, and disposal.
- `LogReader.App/Services/WorkspaceHosts.cs`: shell host wiring between workspace services and shell-facing view models.
- `LogReader.App/Views/MainWindow.xaml` and `LogReader.App/Views/MainWindow.xaml.cs`: top-level shell composition and window-only event wiring.
- Focused views under `LogReader.App/Views/` such as `DashboardTreeView`, `TabStripView`, `LogViewportView`, and `SearchWorkspaceView`: region-specific layout and behavior.

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

Important interfaces in `LogReader.Core/Interfaces` include:

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

Settings notes:

- `AppSettings` currently persists the default open directory, log font family, dashboard full-path labels, line highlight rules, and date rolling patterns.
- `LogFileEntry` is a known-file catalog record with a stable ID, file path, and `LastOpenedAt` timestamp. It is not a saved open-tab session record.

## Infrastructure Services

### ChunkedLogReaderService

- Uses 64 KB buffered scanning
- Detects BOM markers for UTF-8 and UTF-16 variants
- Stores newline offsets for random access reads
- Treats empty and BOM-only files as `LineCount == 0`
- Extends indexes for appended data and rebuilds on truncation or rotation

### SearchService

- Streams file content line by line with `StreamReader`
- Supports plain text and regex matching
- Uses a 250 ms regex timeout
- Uses bounded parallelism for multi-file search

### FileTailService

- Polls each tailed file every 250 ms
- Raises append events when file size grows
- Raises rotation events when identity changes, the file shrinks, or it disappears and reappears
- Tracks active tails in a `ConcurrentDictionary<string, TailState>`

## Persistence and Storage

Repositories in `LogReader.Infrastructure/Repositories`:

- `JsonLogFileRepository` for `logfiles.json`
- `JsonLogGroupRepository` for `loggroups.json`
- `JsonSettingsRepository` for `settings.json`
- `JsonStore` for shared JSON load and save helpers

Storage behavior:

- Packaged builds resolve storage from `LogReader.install.json` beside `LogReader.exe`
- Portable packages use the executable directory as the storage root
- New MSI installs use `storageMode = PerUserChoice` and prompt on first launch for the current user's storage root
- Existing MSI installs with `storageMode = Absolute` keep using the configured absolute storage root
- `Data` and `Cache` always live under the same storage root
- MSI per-user selections are stored at `%LOCALAPPDATA%\LogReaderSetup\LogReader.msi-user.json`
- Debug runs from source fall back to `%LOCALAPPDATA%\LogReader` when no install config is present
- Writes go to `*.tmp` first and then move into place
- JSON uses camelCase, indented formatting, and string enums
- `ImportViewAsync` returns `null` when the import file is missing
- Malformed import JSON throws `InvalidDataException` with context
- `settings.json` stores UI and pattern settings
- `loggroups.json` stores the dashboard tree, memberships, and sort order
- `logfiles.json` stores the known-file catalog that backs dashboard memberships and import remapping; startup does not reopen tabs from it

## Sensitive Workflows

- Persisted-state recovery is explicit. Invalid `settings.json`, `logfiles.json`, or `loggroups.json` content is moved aside as a timestamped `.corrupt-*` backup, a sibling `.note.txt` is written, and the app surfaces the recovery details to the user.
- Dashboard orchestration is intentionally split. `DashboardImportService` owns import/export materialization, `DashboardWorkspaceService` is the facade used by the shell, `DashboardTreeService` owns tree CRUD/filtering, and `DashboardActivationService` coordinates member refresh plus open/load behavior.
- Modifier and dashboard-open behavior are sensitive to scope state. If you touch dashboard selection, modifier labels, effective paths, or the member refresh flow, re-check both `FilteredTabs` behavior and dashboard loading cancellation.
- Imported dashboard views can carry non-standard paths. UNC paths are allowed without an extra warning, but relative, drive-relative, and device-prefixed paths trigger a trust confirmation before the import is applied.
- Storage safety rules should stay aligned between runtime and uninstall cleanup. Runtime validation rejects protected roots through `StoragePathValidator`; installer cleanup should only delete `Data` and `Cache` beneath a resolved, non-protected storage root and should skip cleanup when the root is blank or malformed.

## Runtime Data Flow

### Open a File

1. `MainViewModel.OpenFilePathAsync`
2. Resolve or create `LogFileEntry`
3. Create `LogTabViewModel`
4. `LogTabViewModel.LoadAsync` builds the index, loads the initial viewport, and starts tailing

### Append or Rotation

1. `FileTailService` raises an event
2. `LogTabViewModel` updates or rebuilds the line index
3. The visible viewport refreshes when needed

### Search

1. `SearchPanelViewModel.ExecuteSearch`
2. Resolve scope to the selected tab or the currently visible `FilteredTabs` set when `All open tabs` is selected
3. Choose `DiskSnapshot`, `Tail`, or `SnapshotAndTail`
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
- `InverseBooleanConverter`
- `HexColorToBrushConverter`

Threading and safety notes:

- JSON repositories serialize mutations with `SemaphoreSlim(1,1)`
- `LogTabViewModel` guards index swaps and disposal with `_lineIndexLock`
- `MainViewModel` uses cycle-safe traversal for dashboard tree building and file ID resolution
