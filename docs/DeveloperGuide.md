# LogReader Developer Guide

This guide covers the architecture, build process, and internals of the LogReader application for contributors and maintainers.

---

## Table of Contents

- [Solution Structure](#solution-structure)
- [Build and Test](#build-and-test)
- [Architecture Overview](#architecture-overview)
- [Core Models](#core-models)
- [Interfaces](#interfaces)
- [Service Implementations](#service-implementations)
- [Persistence Layer](#persistence-layer)
- [Memory-Mapped Line Index](#memory-mapped-line-index)
- [Data Flow](#data-flow)
- [Threading Model](#threading-model)
- [ViewModels](#viewmodels)
- [DI and Startup](#di-and-startup)
- [Converters](#converters)
- [Key Design Decisions](#key-design-decisions)
- [Adding a New Feature](#adding-a-new-feature)

---

## Solution Structure

The solution contains 4 projects with a layered dependency graph:

```
LogReader.sln
├── LogReader.Core          (net8.0)        Pure domain models and interfaces
├── LogReader.Infrastructure (net8.0)       Service implementations and JSON persistence
├── LogReader.App           (net8.0-windows) WPF UI with MVVM ViewModels
└── LogReader.Tests         (net8.0-windows) xUnit test suite
```

**Dependency graph:**

```
LogReader.App ──> LogReader.Infrastructure ──> LogReader.Core
LogReader.Tests ──> LogReader.App, LogReader.Infrastructure, LogReader.Core
```

`LogReader.Core` has no project dependencies — it defines the domain models and interfaces that the other projects implement and consume.

### NuGet Dependencies

| Project | Package | Version | Purpose |
|---------|---------|---------|---------|
| LogReader.Core | System.Text.Encoding.CodePages | 10.0.3 | ANSI (Windows-1252) encoding support |
| LogReader.App | CommunityToolkit.Mvvm | 8.2.2 | MVVM source generators (`[ObservableProperty]`, `[RelayCommand]`) |
| LogReader.Tests | xunit | 2.5.3 | Test framework |
| LogReader.Tests | Microsoft.NET.Test.Sdk | 17.8.0 | Test runner integration |
| LogReader.Tests | xunit.runner.visualstudio | 2.5.3 | VS/CLI test discovery |
| LogReader.Tests | coverlet.collector | 6.0.0 | Code coverage |

---

## Build and Test

```bash
# Build the full solution
dotnet build

# Run all tests (46 tests)
dotnet test

# Build individual projects (useful if the app is running and locks DLLs)
dotnet build LogReader.Core
dotnet build LogReader.Infrastructure
dotnet build LogReader.Tests
```

**Note:** If LogReader.App is running, building the full solution will fail with file-locking errors on the WPF temp project. Building Core, Infrastructure, and Tests individually works fine.

---

## Architecture Overview

LogReader follows a **layered architecture** with **MVVM** for the UI layer:

```
┌─────────────────────────────────────────────┐
│  LogReader.App (WPF)                        │
│  ┌───────────┐  ┌──────────┐  ┌──────────┐ │
│  │ Views     │  │ViewModels│  │Converters│ │
│  │ (XAML)    │◄─┤ (MVVM)   │  │          │ │
│  └───────────┘  └────┬─────┘  └──────────┘ │
├──────────────────────┼──────────────────────┤
│  LogReader.Infrastructure                   │
│  ┌──────────────┐  ┌┴────────────┐          │
│  │ Repositories │  │  Services   │          │
│  │ (JSON)       │  │ (File I/O)  │          │
│  └──────────────┘  └─────────────┘          │
├─────────────────────────────────────────────┤
│  LogReader.Core                             │
│  ┌──────────┐  ┌────────────┐               │
│  │  Models  │  │ Interfaces │               │
│  └──────────┘  └────────────┘               │
└─────────────────────────────────────────────┘
```

- **Core** defines the contracts (interfaces) and data shapes (models).
- **Infrastructure** implements file I/O, search, tailing, and JSON persistence.
- **App** wires everything together and provides the WPF UI via ViewModels.

MVVM is implemented using **CommunityToolkit.Mvvm 8.2.2**, which provides source-generated properties and commands via attributes.

---

## Core Models

Located in `LogReader.Core/Models/`:

| Model | File | Purpose |
|-------|------|---------|
| `LogFileEntry` | `LogFileEntry.cs` | Persisted log file metadata (ID, path, display name, timestamps) |
| `LogGroup` | `LogGroup.cs` | Named collection of file IDs with color and sort order |
| `GroupExport` | `GroupExport.cs` | Import/export format for group definitions (name + file paths) |
| `SessionState` | `SessionState.cs` | Persists open tabs and active tab across app restarts |
| `OpenTabState` | `SessionState.cs` | Per-tab state within a session (file ID, path, encoding, auto-scroll, pin state) |
| `AppSettings` | `AppSettings.cs` | Application settings (default directory, highlight rules) |
| `LineHighlightRule` | `LineHighlightRule.cs` | Pattern-based line highlighting rule (pattern, regex, color) |
| `SearchRequest` | `SearchRequest.cs` | Search parameters (query, regex, case, whole word, file list) |
| `SearchResult` | `SearchResult.cs` | Search results for a single file (path, hits list, error) |
| `SearchHit` | `SearchResult.cs` | Single match (line number, text, byte offset, match position/length) |
| `LineIndex` | `LineIndex.cs` | Byte-offset index for random access to lines in a file |
| `MappedLineOffsets` | `MappedLineOffsets.cs` | Memory-mapped offset storage to reduce GC pressure on large files |
| `FileEncoding` | `FileEncoding.cs` | Enum: Utf8, Ansi, Utf16 |

### EncodingHelper

`LogReader.Core/EncodingHelper.cs` provides encoding resolution:

```csharp
FileEncoding.Utf8  → new UTF8Encoding(false)        // No BOM
FileEncoding.Ansi  → Encoding.GetEncoding(1252)      // Windows-1252
FileEncoding.Utf16 → Encoding.Unicode                // UTF-16 LE
```

Registers `CodePagesEncodingProvider` on first use for ANSI support.

---

## Interfaces

Located in `LogReader.Core/Interfaces/`:

### ILogReaderService

Builds line offset indices and reads lines by position:

```csharp
Task<LineIndex> BuildIndexAsync(string filePath, FileEncoding encoding, CancellationToken ct);
Task<LineIndex> UpdateIndexAsync(string filePath, LineIndex existingIndex, FileEncoding encoding, CancellationToken ct);
Task<IReadOnlyList<string>> ReadLinesAsync(string filePath, LineIndex index, int startLine, int count, FileEncoding encoding, CancellationToken ct);
Task<string> ReadLineAsync(string filePath, LineIndex index, int lineNumber, FileEncoding encoding, CancellationToken ct);
```

### ISearchService

Text and regex search across one or more files:

```csharp
Task<SearchResult> SearchFileAsync(string filePath, SearchRequest request, FileEncoding encoding, CancellationToken ct);
Task<IReadOnlyList<SearchResult>> SearchFilesAsync(SearchRequest request, IDictionary<string, FileEncoding> fileEncodings, CancellationToken ct, int maxConcurrency = 4);
```

### IFileTailService

Live file monitoring with rotation detection:

```csharp
event EventHandler<TailEventArgs>? LinesAppended;
event EventHandler<FileRotatedEventArgs>? FileRotated;
void StartTailing(string filePath, FileEncoding encoding);
void StopTailing(string filePath);
void StopAll();
```

### Repository Interfaces

| Interface | Purpose |
|-----------|---------|
| `ILogFileRepository` | CRUD for log file entries, lookup by path |
| `ILogGroupRepository` | CRUD for groups, reorder, export/import |
| `ISessionRepository` | Save/load session state |
| `ISettingsRepository` | Save/load application settings |

---

## Service Implementations

Located in `LogReader.Infrastructure/Services/`:

### ChunkedLogReaderService

Implements `ILogReaderService`. Uses a **64KB buffer** for sequential file scanning.

**Index Building (`BuildIndexAsync`):**

1. Open file with `FileShare.ReadWrite` (critical for files being written to).
2. Detect and skip BOM (UTF-8: 3 bytes, UTF-16: 2 bytes).
3. Read file in 64KB chunks.
4. Scan each chunk for newline characters (`\n`).
5. Record the byte offset of the character after each newline.
6. Remove trailing empty line if file ends with `\n`.
7. Call `LineOffsets.Freeze()` to transition to memory-mapped storage.

**Index Update (`UpdateIndexAsync`):**

- If file shrunk: rebuild entirely (file was truncated/rotated).
- If file grew: seek to the previous end position, scan only new bytes, append offsets.
- If unchanged: return as-is.

**Line Reading (`ReadLinesAsync`):**

- Uses the offset index to seek directly to the requested line range.
- Reads bytes between offsets, decodes with the specified encoding.
- Splits on newlines and trims `\r`.

### SearchService

Implements `ISearchService`. Uses a **256KB buffer** for streaming reads.

**Single-file search:**

- Reads line by line via `StreamReader`.
- For text search: uses `string.Contains()` with `StringComparison.Ordinal` or `OrdinalIgnoreCase`.
- For regex search: compiles `Regex` with a **5-second timeout** to prevent runaway patterns.
- Whole-word matching: checks that characters before/after the match are not letters or digits (text mode) or wraps the pattern with `\b` (regex mode).

**Multi-file search:**

- Uses `SemaphoreSlim(maxConcurrency)` (default 4) for bounded parallelism.
- Each file searched in its own task with its own encoding.
- Results aggregated and returned as a list.

### FileTailService

Implements `IFileTailService`. Uses a **250ms polling loop** per file.

**Polling algorithm (per file):**

1. Check if file exists. If deleted, wait 500ms and check again. If reappears, fire `FileRotated`.
2. Compare `FileInfo.CreationTimeUtc` with stored value. If changed, fire `FileRotated`.
3. Compare current file size with stored value:
   - **Shrunk**: Fire `FileRotated` (truncation).
   - **Grew**: Read new bytes from `[lastSize, currentSize)`, split into lines, fire `LinesAppended`.
   - **Unchanged**: No action.

**State management:** Uses `ConcurrentDictionary<string, TailState>` with one `CancellationTokenSource` and background `Task` per tailed file.

---

## Persistence Layer

Located in `LogReader.Infrastructure/Repositories/`:

### JsonStore

Static utility for JSON file persistence. All data stored in `%LocalAppData%\LogReader\`.

**Atomic writes:**

1. Serialize object to `{filename}.tmp`.
2. Move (overwrite) to `{filename}`.
3. Prevents corruption if the app crashes mid-write.

**Serialization:** `System.Text.Json` with `PropertyNameCaseInsensitive = true` and `WriteIndented = true`.

### Repository Implementations

| Repository | File | Thread Safety |
|------------|------|---------------|
| `JsonLogFileRepository` | `logfiles.json` | `SemaphoreSlim(1,1)` |
| `JsonLogGroupRepository` | `loggroups.json` | `SemaphoreSlim(1,1)` |
| `JsonSessionRepository` | `session.json` | None (single-caller) |
| `JsonSettingsRepository` | `settings.json` | None (single-caller) |

### JSON Schema Examples

**logfiles.json:**
```json
[
  {
    "id": "abc-123",
    "filePath": "C:\\logs\\app.log",
    "displayName": "app.log",
    "addedAt": "2025-01-01T00:00:00Z",
    "lastOpenedAt": "2025-01-02T00:00:00Z"
  }
]
```

**loggroups.json:**
```json
[
  {
    "id": "def-456",
    "name": "Production",
    "color": "#E57373",
    "createdAt": "2025-01-01T00:00:00Z",
    "sortOrder": 0,
    "fileIds": ["abc-123", "abc-124"]
  }
]
```

**session.json:**
```json
{
  "openTabs": [
    {
      "fileId": "abc-123",
      "filePath": "C:\\logs\\app.log",
      "encoding": "Utf8",
      "autoScrollEnabled": true,
      "isPinned": false
    }
  ],
  "activeTabId": "abc-123"
}
```

**settings.json:**
```json
{
  "defaultOpenDirectory": "C:\\logs",
  "highlightRules": [
    {
      "id": "ghi-789",
      "pattern": "ERROR",
      "isRegex": false,
      "caseSensitive": true,
      "color": "#FFCCCC",
      "isEnabled": true
    }
  ]
}
```

---

## Memory-Mapped Line Index

`MappedLineOffsets` (`LogReader.Core/Models/MappedLineOffsets.cs`) manages byte offsets for line positions.

**Two modes:**

1. **Build mode**: Offsets collected in a `List<long>` during index construction.
2. **Frozen mode**: After `Freeze()` is called, the list is written to a memory-mapped temp file (`%TEMP%\LogReader\idx\idx_{guid}.bin`). Subsequent offset lookups read from the mapped file, reducing GC pressure.

An overflow `List<long>` handles offsets added after freezing (from `UpdateIndexAsync`).

**Cleanup:** Stale index files are cleaned up on app startup in `App.OnStartup`.

---

## Data Flow

### Opening a File

```
User clicks Open / Ctrl+O
  └─> OpenFilePathAsync(filePath)
        ├─> LogFileRepository.GetByPathAsync() — find or create entry
        ├─> Create LogTabViewModel
        └─> LogTabViewModel.LoadAsync()
              ├─> ChunkedLogReaderService.BuildIndexAsync()
              │     └─> Sequential 64KB scan → LineIndex with byte offsets
              ├─> LoadViewportAsync(lastLines, 100)
              │     └─> ChunkedLogReaderService.ReadLinesAsync()
              │           └─> Seek by offset, read bytes, decode → strings
              └─> FileTailService.StartTailing()
                    └─> Spawn background polling task
```

### Live Tailing

```
Background: FileTailService.TailLoopAsync() [every 250ms]
  ├─> File grew → Read new bytes → LinesAppended event
  └─> Rotation detected → FileRotated event

LogTabViewModel.OnLinesAppended()
  ├─> ChunkedLogReaderService.UpdateIndexAsync() — append offsets
  └─> If AutoScrollEnabled → LoadViewportAsync(end of file)

LogTabViewModel.OnFileRotated()
  └─> LoadAsync() — full rebuild from scratch
```

### Scrolling

```
User scrolls (mouse wheel / scrollbar)
  └─> ScrollPosition property changes
        └─> ScrollToLineAsync(startLine)
              └─> LoadViewportAsync(startLine, 100)
                    └─> ReadLinesAsync() → Update VisibleLines collection
```

### Search

```
User clicks Search
  └─> SearchPanelViewModel.ExecuteSearchAsync()
        ├─> Determine scope → build file paths + encodings
        └─> SearchService.SearchFilesAsync()
              └─> SemaphoreSlim(4) bounded parallelism
                    └─> Per file: StreamReader → line-by-line match

User clicks result line
  └─> MainViewModel.NavigateToLine(filePath, lineNumber)
        ├─> Open file if not already open
        └─> LogTabViewModel.NavigateToLineAsync(lineNumber)
              └─> LoadViewportAsync(centered on target line)
```

---

## Threading Model

| Component | Thread | Concurrency Control |
|-----------|--------|---------------------|
| ChunkedLogReaderService | ThreadPool (async) | Stateless; safe for concurrent calls |
| SearchService | ThreadPool (async) | `SemaphoreSlim(4)` bounds file-level parallelism |
| FileTailService | Background Task (one per file) | `ConcurrentDictionary` for state, `CancellationTokenSource` per file |
| ViewModels | UI thread | Property changes dispatch to UI via CommunityToolkit.Mvvm |
| JsonLogFileRepository | Async, serialized | `SemaphoreSlim(1)` for exclusive file access |
| JsonLogGroupRepository | Async, serialized | `SemaphoreSlim(1)` for exclusive file access |

All file I/O uses `FileShare.ReadWrite` to allow concurrent access with the process writing the log file.

---

## ViewModels

Located in `LogReader.App/ViewModels/`:

| ViewModel | Responsibility | Key Properties |
|-----------|----------------|----------------|
| `MainViewModel` | Root orchestrator — file open/close, group CRUD, settings | `Tabs`, `Groups`, `SelectedTab`, `SearchPanel`, `FilteredTabs` |
| `LogTabViewModel` | Per-file state — viewport, scrolling, tailing, encoding, pinning | `FilePath`, `TotalLines`, `VisibleLines`, `ScrollPosition`, `AutoScrollEnabled`, `Encoding`, `IsPinned` |
| `SearchPanelViewModel` | Search UI — query, options, execution, results | `Query`, `IsRegex`, `CaseSensitive`, `WholeWord`, `SelectedScope`, `Results`, `IsSearching` |
| `LogGroupViewModel` | Group UI — name editing, selection, member files | `Name`, `IsEditing`, `IsSelected`, `IsExpanded`, `MemberFiles` |
| `SettingsViewModel` | Settings dialog — load/save, highlight rule management | `DefaultOpenDirectory`, `HighlightRules` |
| `HighlightRuleViewModel` | Single highlight rule in settings | `Pattern`, `IsRegex`, `CaseSensitive`, `Color`, `IsEnabled` |
| `ManageGroupFilesViewModel` | Group file membership dialog | `Files` (list of checkable file items) |
| `FileSearchResultViewModel` | Search results for one file | `FilePath`, `HitCount`, `Hits` |
| `SearchHitViewModel` | Single search hit | `LineNumber`, `Text`, `NavigateCommand` |
| `LogLineViewModel` | Single visible line in the viewer | `LineNumber`, `Text`, `HighlightColor` |

### Observable Patterns

CommunityToolkit.Mvvm generates boilerplate from attributes:

```csharp
// [ObservableProperty] generates a public property with PropertyChanged notification
[ObservableProperty]
private string _name;  // → public string Name { get; set; }

// [RelayCommand] generates an ICommand property
[RelayCommand]
private async Task OpenFile() { ... }  // → public IAsyncRelayCommand OpenFileCommand
```

---

## DI and Startup

`LogReader.App/App.xaml.cs` performs **manual dependency injection** in `OnStartup`:

```csharp
ILogFileRepository fileRepo = new JsonLogFileRepository();
ILogGroupRepository groupRepo = new JsonLogGroupRepository(fileRepo);
ISessionRepository sessionRepo = new JsonSessionRepository();
ISettingsRepository settingsRepo = new JsonSettingsRepository();
ILogReaderService logReader = new ChunkedLogReaderService();
ISearchService searchService = new SearchService();
IFileTailService tailService = new FileTailService();

var mainVm = new MainViewModel(
    fileRepo, groupRepo, sessionRepo, settingsRepo,
    logReader, searchService, tailService);
```

All services are **singletons** — created once and shared for the lifetime of the app.

**Lifecycle:**

- `OnStartup`: Create services, clean up stale index temp files, initialize MainViewModel, show window.
- `OnExit`: Save session state, stop all file tailing (`tailService.StopAll()`).

---

## Converters

Located in `LogReader.App/Converters/`:

| Converter | Input | Output | Notes |
|-----------|-------|--------|-------|
| `BoolToVisibilityConverter` | `bool` | `Visibility` | Supports `Invert` parameter for reverse logic |
| `EncodingToBoolConverter` | `FileEncoding` | `bool` | Used for radio button binding (parameter = target encoding) |
| `HexColorToBrushConverter` | `string` (hex) | `SolidColorBrush` | Used in Settings for highlight rule color preview. Returns `null` for null/empty input |

---

## Key Design Decisions

### FileShare.ReadWrite
All file operations open files with `FileShare.ReadWrite`. This is critical because log files are typically being written to by another process while LogReader reads them.

### Polling vs FileSystemWatcher
`FileTailService` uses 250ms polling instead of `FileSystemWatcher`. Polling is more reliable across network drives and volume types, and avoids known issues with `FileSystemWatcher` missing events or firing duplicates.

### Bounded Concurrency
Multi-file search uses `SemaphoreSlim(4)` to prevent thread pool starvation when searching many files simultaneously. The limit of 4 is a balance between throughput and resource consumption.

### Approximate Byte Offsets in Search
`SearchResult.SearchHit` stores approximate byte offsets (calculated via `Encoding.GetByteCount` per line). These are not used for navigation — line numbers are used instead — but provide metadata about match positions within the file.

### First-Match-Wins Highlighting
Line highlight rules are evaluated in order, and the first matching rule determines the color. This gives users explicit control over priority (e.g., ERROR rules before WARN rules).

### Memory-Mapped Indices
For large files, line offset arrays can consume significant memory and create GC pressure. `MappedLineOffsets` writes offsets to a temp file and memory-maps it, keeping the working set small.

### Atomic JSON Writes
All persistence goes through `JsonStore`, which writes to a `.tmp` file first and then atomically moves it into place. This prevents data corruption if the app crashes during a write.

---

## Adding a New Feature

A typical feature touches these layers:

1. **Model** (`LogReader.Core/Models/`): Define any new data types.
2. **Interface** (`LogReader.Core/Interfaces/`): Add methods to existing interfaces or create new ones.
3. **Service** (`LogReader.Infrastructure/Services/`): Implement the interface.
4. **Repository** (if persisted) (`LogReader.Infrastructure/Repositories/`): Add storage support.
5. **ViewModel** (`LogReader.App/ViewModels/`): Add observable properties and commands.
6. **View** (`LogReader.App/Views/`): Add XAML bindings.
7. **Wiring** (`LogReader.App/App.xaml.cs`): Register new services in the manual DI setup.
8. **Tests** (`LogReader.Tests/`): Add xUnit tests. Update any stub implementations if you changed an interface.

When modifying an interface, remember to update the stub implementations in `LogReader.Tests/MainViewModelTests.cs` (and any other test files with stubs) to avoid build errors.
