# LogReader Developer Guide

Last updated: 2026-03-18

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
|- LogReader.Testing         (net8.0, shared test helpers)
|- LogReader.Core.Tests      (net8.0, xUnit)
`- LogReader.Tests           (net8.0-windows, xUnit)
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

## Versioning

- Product version metadata is centralized in `Directory.Build.props`.
- The current release line is `0.9.0`.

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
- Portable output is written to `..\artifacts\publish\Portable`
- MSI payload publish output is written to `..\artifacts\publish\LogReader.MsiPayload`
- MSI build output is written to `..\artifacts\installer`
- The WiX installer project lives in `LogReader.Setup/` and is not included in `LogReader.sln`

## Architecture Summary

LogReader uses a layered architecture with MVVM in the app project:

- `LogReader.Core`: models, enums, and interfaces
- `LogReader.Infrastructure`: service and repository implementations
- `LogReader.App`: views, viewmodels, converters, and startup wiring
- `LogReader.Testing`: shared test doubles and helpers for the test projects

Startup wiring is manual in `LogReader.App/App.xaml.cs`.

## Core Models and Interfaces

Key models in `LogReader.Core/Models` include:

- `LogFileEntry`
- `LogGroup` and `LogGroupKind`
- `ViewExport` and `ViewExportGroup`
- `FileEncoding`
- `AppSettings`
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
- MSI installs use an absolute storage root chosen at install time
- `Data` and `Cache` always live under the same storage root
- Debug runs from source fall back to `%LOCALAPPDATA%\LogReader` when no install config is present
- Writes go to `*.tmp` first and then move into place
- JSON uses camelCase, indented formatting, and string enums
- `ImportViewAsync` returns `null` when the import file is missing
- Malformed import JSON throws `InvalidDataException` with context

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
2. Resolve scope to the current tab or all open tabs
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
