# LogReader Developer Guide

Last updated: 2026-03-17

This guide describes the current architecture and contributor workflows for LogReader.

All commands below assume your working directory is the `LogReader/` product folder at the repo root.
The peer `LogGenerator/` folder at the repo root is an internal developer utility and is documented separately.

## Solution Layout

```text
LogReader.sln
|- LogReader.Core            (net8.0)
|- LogReader.Infrastructure  (net8.0)
|- LogReader.App             (net8.0-windows, WPF)
`- LogReader.Tests           (net8.0-windows, xUnit)
```

Dependency graph:

```text
LogReader.App -> LogReader.Infrastructure -> LogReader.Core
LogReader.Tests -> LogReader.App + LogReader.Infrastructure + LogReader.Core
```

## Prerequisites

- Windows (WPF target)
- .NET SDK 8.x

## Build, Test, Run

```powershell
# From repo-root/LogReader
dotnet clean LogReader.sln -m:1
dotnet restore LogReader.sln
dotnet build LogReader.sln -m:1

# Tests target net8.0-windows
dotnet test LogReader.Tests\LogReader.Tests.csproj --framework net8.0-windows
dotnet test LogReader.Core.Tests\LogReader.Core.Tests.csproj

# Run the app
dotnet run --project LogReader.App\LogReader.App.csproj
```

Notes:

- If the app process is running, builds can fail due to locked output files.
- Use `-m:1` for solution clean/build. The current WPF/test project graph is more reliable with serial MSBuild nodes.
- `LogReader.Tests` does not target `net8.0` (only `net8.0-windows`).

## Versioning

- Product version metadata is centralized in `Directory.Build.props`.
- The current release line starts at `0.9.0` to leave room for rapid install and upgrade iteration before `1.0`.

## Installer Build

Create a versioned setup folder from the repo root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\Build-Installer.ps1
```

What the script does:

- Publishes `LogReader.App` as a Release build
- Stages a setup folder under `..\artifacts\installer\output\LogReader-Setup-<version>`
- Includes `Setup.cmd`, install/uninstall PowerShell scripts, and the published app payload

Installer behavior:

- Install location: `%LOCALAPPDATA%\Programs\LogReader`
- Start menu shortcut: `LogReader`
- Uninstall entry: current user only (`HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\LogReader`)
- App data is preserved in `%LOCALAPPDATA%\LogReader`
- The default staged installer is framework-dependent, so target machines need the .NET 8 Desktop Runtime

Optional self-contained publish:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\Build-Installer.ps1 -SelfContained -RuntimeIdentifier win-x64
```

## NuGet Packages

| Project | Package | Version |
|---|---|---|
| LogReader.Core | System.Text.Encoding.CodePages | 10.0.3 |
| LogReader.App | CommunityToolkit.Mvvm | 8.2.2 |
| LogReader.Tests | coverlet.collector | 6.0.0 |
| LogReader.Tests | Microsoft.NET.Test.Sdk | 17.8.0 |
| LogReader.Tests | xunit | 2.5.3 |
| LogReader.Tests | xunit.runner.visualstudio | 2.5.3 |

## Architecture Summary

LogReader uses a layered architecture with MVVM in the app project:

- `LogReader.Core`: models and interfaces only
- `LogReader.Infrastructure`: service/repository implementations
- `LogReader.App`: views, viewmodels, startup wiring

Startup wiring is manual in `LogReader.App/App.xaml.cs`.

## Core Models

Key models in `LogReader.Core/Models`:

- `LogFileEntry`: `Id`, `FilePath`, `LastOpenedAt`
- `LogGroup`: `Id`, `Name`, `SortOrder`, `ParentGroupId`, `Kind`, `FileIds`
- `LogGroupKind`: `Branch`, `Dashboard`
- `ViewExport` + `ViewExportGroup`: import/export payload for saved dashboard views
- `FileEncoding` enum: `Utf8`, `Utf8Bom`, `Ansi`, `Utf16`, `Utf16Be`
- `AppSettings`: open directory, global auto-tail, default/fallback encodings, font, highlight rules
- `LineIndex` + `MappedLineOffsets`: file line offset indexing
- `SearchRequest`, `SearchResult`, `SearchHit`

Encoding helper:

- `EncodingHelper` maps `FileEncoding` to .NET encodings.
- ANSI is Windows-1252 via `CodePagesEncodingProvider`.

## Core Interfaces

Located in `LogReader.Core/Interfaces`:

- `ILogReaderService`: build/update index and read lines by line range/number
- `ISearchService`: single-file and multi-file search
- `IFileTailService`: append/rotation events + tail lifecycle
- `ILogFileRepository`, `ILogGroupRepository`, `ISettingsRepository`

## Infrastructure Services

### ChunkedLogReaderService

File: `LogReader.Infrastructure/Services/ChunkedLogReaderService.cs`

- Uses 64 KB buffered scanning.
- Detects BOM for UTF-8/UTF-16 LE/UTF-16 BE.
- Stores newline offsets for random access.
- Empty and BOM-only files normalize to `LineCount == 0`.
- `UpdateIndexAsync` extends index for appended content and rebuilds on truncation/rotation.

### SearchService

File: `LogReader.Infrastructure/Services/SearchService.cs`

- Streams file line by line using `StreamReader`.
- Supports plain text and regex search.
- Regex timeout is 250 ms.
- Multi-file search uses bounded parallelism via `SemaphoreSlim(maxConcurrency)`.

### FileTailService

File: `LogReader.Infrastructure/Services/FileTailService.cs`

- Polls each tailed file every 250 ms.
- Raises `LinesAppended` when file size grows.
- Raises `FileRotated` when identity changes, file shrinks, or file disappears/reappears.
- Tracks active tails in a `ConcurrentDictionary<string, TailState>`.

## Persistence Layer

Files in `LogReader.Infrastructure/Repositories`:

- `JsonStore`: common load/save helpers and JSON options
- `JsonLogFileRepository`: `logfiles.json`
- `JsonLogGroupRepository`: `loggroups.json`
- `JsonSettingsRepository`: `settings.json`

Storage location:

- `%LocalAppData%\LogReader\`

Write strategy:

- Save to `*.tmp`, then atomic move/overwrite.

Serialization settings:

- CamelCase property naming
- Indented output
- String enum converter

Import behavior:

- `JsonLogGroupRepository.ImportViewAsync` returns `null` when file is missing.
- Malformed import JSON throws `InvalidDataException` with context.

## Runtime Data Flow

### Open file

1. `MainViewModel.OpenFilePathAsync`
2. Resolve or create `LogFileEntry`
3. Create `LogTabViewModel`
4. `LogTabViewModel.LoadAsync`:
   - build index
   - load initial viewport
   - start tailing

### Append/rotation

1. `FileTailService` raises event
2. `LogTabViewModel` updates/rebuilds index
3. Visible viewport refreshes when needed

### Search

1. `SearchPanelViewModel.ExecuteSearch`
2. Scope resolves to current tab or all open tabs
3. Search mode determines execution path:
   - `DiskSnapshot`: one-pass file search
   - `Tail`: continuous polling of appended lines only
   - `SnapshotAndTail`: tail monitoring plus bounded snapshot backfill
4. Result click navigates to line via `MainViewModel.NavigateToLineAsync`

### Filter (current tab)

1. `FilterPanelViewModel.ApplyFilter`
2. `SearchService.SearchFileAsync` computes initial matching lines for selected tab
3. `LogTabViewModel.ApplyFilterAsync` activates filtered line map
4. During tail updates, `LogTabViewModel` merges new matching lines into filtered view

## UI ViewModels

Current viewmodels in `LogReader.App/ViewModels`:

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

## Converters

Current converters in `LogReader.App/Converters`:

- `BoolToVisibilityConverter`
- `InverseBooleanConverter`
- `HexColorToBrushConverter`

## Threading and Safety Notes

- Repositories that mutate shared JSON files use `SemaphoreSlim(1,1)` for serialization.
- `LogTabViewModel` guards index swaps and disposal with `_lineIndexLock`.
- `MainViewModel` uses cycle-safe traversal for runtime dashboard tree building and file ID resolution.

## JSON Examples

`logfiles.json`

```json
[
  {
    "id": "3a7f1b4d-4ef9-4ea3-a747-a2cc6cb3a593",
    "filePath": "C:\\logs\\app.log",
    "lastOpenedAt": "2026-03-08T14:55:00Z"
  }
]
```

`loggroups.json`

```json
[
  {
    "id": "ae9f4bcf-b880-4d7a-a69f-8ccd5e4ead90",
    "name": "Production",
    "sortOrder": 0,
    "parentGroupId": null,
    "kind": "Branch",
    "fileIds": []
  },
  {
    "id": "4f5ad188-9725-4d4b-96f9-e67b91dc9fcf",
    "name": "API Dashboard",
    "sortOrder": 0,
    "parentGroupId": "ae9f4bcf-b880-4d7a-a69f-8ccd5e4ead90",
    "kind": "Dashboard",
    "fileIds": ["3a7f1b4d-4ef9-4ea3-a747-a2cc6cb3a593"]
  }
]
```

`settings.json`

```json
{
  "defaultOpenDirectory": "C:\\logs",
  "globalAutoTailEnabled": true,
  "defaultFileEncoding": "Utf8",
  "fileEncodingFallbacks": ["Utf16"],
  "logFontFamily": "Consolas",
  "highlightRules": [
    {
      "id": "4a64308f-f223-451c-a58d-30bece1eb7ab",
      "pattern": "ERROR",
      "isRegex": false,
      "caseSensitive": false,
      "color": "#FFCCCC",
      "isEnabled": true
    }
  ]
}
```
