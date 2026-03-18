# Bug & Correctness Review — LogReader

## High Severity

### H-1. Unprotected event invocations crash the tail loop
- **File:** `LogReader/LogReader.Infrastructure/Services/FileTailService.cs` lines 115, 132, 144, 153
- **What:** `FileRotated?.Invoke(...)` and `LinesAppended?.Invoke(...)` have no try-catch. If any subscriber throws, the exception propagates into `TailLoopAsync`, which terminates tailing for that file permanently. Only `TailError` has inner exception protection.
- **Fix:** Wrap each event invocation in try-catch, similar to the existing `TailError` pattern. Catch and route to `TailError` instead of killing the loop.

### H-2. Race condition on `_snapshotFilteredLineNumbers` list mutation
- **File:** `LogReader/LogReader.App/ViewModels/LogTabViewModel.cs` lines 568, 706, 278-284
- **What:** `_snapshotFilteredLineNumbers` is a `List<int>` mutated in-place by `InsertSortedUnique` (during `ApplyTailFilterForAppendedLinesAsync`) while `LoadViewportAsync` reads from it. Both run on the UI thread but can interleave across `await` boundaries, causing `ArgumentOutOfRangeException` or wrong line display.
- **Fix:** Use an immutable list pattern — create a new `List<int>` on mutation rather than modifying the shared reference. Or snapshot the list reference at the start of `LoadViewportAsync`.

### H-3. Corrupt JSON crashes app on startup
- **File:** `LogReader/LogReader.Infrastructure/Repositories/JsonStore.cs` lines 30-36
- **What:** If `session.json`, `settings.json`, or `logfiles.json` contains invalid JSON (power loss during write, zero-byte file), `JsonSerializer.DeserializeAsync` throws `JsonException` unhandled. Only `JsonLogGroupRepository` has a catch for this. The app cannot start.
- **Fix:** Add `JsonException` handling in `LoadAsync<T>` itself (return `default(T)` or `new T()`) or in each repository, matching the `JsonLogGroupRepository` pattern.

## Medium Severity

### M-1. Regex `WholeWord` + `IsRegex` operator precedence bug
- **File:** `LogReader/LogReader.Infrastructure/Services/SearchService.cs` line 110
- **What:** `$@"\b{request.Query}\b"` wraps user regex without grouping. A query like `a|b` becomes `\ba|b\b` which means `(\ba)|(b\b)` due to alternation precedence.
- **Fix:** Wrap in a non-capturing group: `$@"\b(?:{request.Query})\b"`.

### M-2. `RegexMatchTimeoutException` aborts entire file search
- **File:** `LogReader/LogReader.Infrastructure/Services/SearchService.cs` lines 59, 72-76
- **What:** A 250ms regex timeout on a pathological line throws `RegexMatchTimeoutException` (subclass of `TimeoutException`, not `OperationCanceledException`), which falls through to the general `catch (Exception)` that sets `result.Error` and stops the entire file scan.
- **Fix:** Catch `RegexMatchTimeoutException` inside the per-line loop and skip the problematic line.

### M-3. `StopTailing` doesn't await the tail loop task
- **File:** `LogReader/LogReader.Infrastructure/Services/FileTailService.cs` lines 49-53
- **What:** `StopTailing` cancels and removes the state but doesn't await `state.Task`. If `StartTailing` is immediately called for the same file, the old loop may still fire events briefly, causing duplicate notifications.
- **Fix:** Await `state.Task` with a timeout before returning, or have the tail loop verify its state is still current before raising events.

### M-4. Race between `StartTailing` and `Dispose` — wrong Task awaited
- **File:** `LogReader/LogReader.Infrastructure/Services/FileTailService.cs` lines 33, 39, 42-46
- **What:** `state.Task` is assigned after `TryAdd`. If `Dispose()` runs between these lines, it waits on the default `Task.CompletedTask`, and the real tail loop task is orphaned.
- **Fix:** Assign `state.Task` before `TryAdd`, or use a `TaskCompletionSource`.

### M-5. NTFS creation-time tunneling defeats rotation detection
- **File:** `LogReader/LogReader.Infrastructure/Services/FileTailService.cs` lines 202-213
- **What:** `GetFileIdentity` uses `CreationTimeUtc.Ticks`. On NTFS, delete-and-recreate within ~15s inherits the old creation time (tunneling), so rotation goes undetected.
- **Fix:** Supplement with file ID via `GetFileInformationByHandle`, or always treat file-size decrease as rotation (already partially handled).

### M-6. UTF-16 buffer misalignment on partial reads
- **File:** `LogReader/LogReader.Infrastructure/Services/ChunkedLogReaderService.cs` lines 68, 252-261
- **What:** `ScanNewlines` steps in 2-byte increments from offset 0. If `ReadAsync` returns an odd byte count (permitted by the Stream contract), scanning falls out of alignment, producing false or missed newlines.
- **Fix:** Track a carry-over byte across reads, or verify even byte counts and handle the remainder.

### M-7. Overflow list grows unboundedly — no re-freeze after tail updates
- **File:** `LogReader/LogReader.Infrastructure/Services/ChunkedLogReaderService.cs` lines 130, 155
- **What:** `BuildIndexAsync` freezes offsets to a memory-mapped file. `UpdateIndexAsync` appends to `_overflow` in memory without ever re-freezing. Long-running tails of active files accumulate millions of entries in managed memory.
- **Fix:** Periodically re-freeze after a threshold (e.g., 10,000 overflow entries).

### M-8. `Clipboard.SetText` can throw `ExternalException`
- **File:** `LogReader/LogReader.App/Views/MainWindow.xaml.cs` line 854
- **What:** Unguarded `Clipboard.SetText` throws if the clipboard is locked by another application, crashing the app.
- **Fix:** Wrap in try-catch for `ExternalException`.

### M-9. `ViewModel!` null-forgiving operator without null guard
- **File:** `LogReader/LogReader.App/Views/MainWindow.xaml.cs` lines 434, 439, 466, 473, 687
- **What:** Multiple event handlers use `ViewModel!` without null checks. If a button click fires after `DataContext` is cleared during shutdown, this throws `NullReferenceException`.
- **Fix:** Replace with `if (ViewModel is { } vm) await vm.Method(...)`.

### M-10. `CollectDescendantIds` stack overflow on circular parent references
- **File:** `LogReader/LogReader.Infrastructure/Repositories/JsonLogGroupRepository.cs` lines 155-164
- **What:** Recursive with no cycle detection. Corrupt `loggroups.json` with circular `ParentGroupId` chains causes `StackOverflowException`.
- **Fix:** Pass a `HashSet<string> visited` and skip already-visited IDs.

### M-11. Fire-and-forget async calls silently swallow exceptions
- **Files:** `LogReader/LogReader.App/ViewModels/LogTabViewModel.cs` lines 156, 500, 545; `LogReader/LogReader.App/ViewModels/MainViewModel.cs` lines 960, 1638
- **What:** `_ = SomeAsyncMethod()` discards tasks. Methods like `RefreshAllMemberFilesAsync` and `SaveSessionAsync` lack internal try/catch — repository failures are completely lost.
- **Fix:** Add try/catch within each fire-and-forget method, or use a helper that observes faults.

### M-12. `CloseTab` disposes before checking `SelectedTab`
- **File:** `LogReader/LogReader.App/ViewModels/MainViewModel.cs` lines 292-303
- **What:** Tab is disposed and removed before checking if it was `SelectedTab`. The `Remove` triggers `CollectionChanged` → `NotifyFilteredTabsChanged`, which may set `SelectedTab` to another tab being batch-closed, interacting with a disposed VM.
- **Fix:** Capture and clear `SelectedTab` before dispose/remove.

### M-13. `FilterPanelViewModel.ApplyFilter` can apply to a disposed tab
- **File:** `LogReader/LogReader.App/ViewModels/FilterPanelViewModel.cs` lines 48, 87, 113
- **What:** Captures `SelectedTab`, awaits `SearchFileAsync` (potentially slow), then calls `ApplyFilterAsync` on the captured tab — which may have been closed and disposed during the await.
- **Fix:** Verify the tab is still in `Tabs` and not disposed after the await.

### M-14. `async void` dispatcher lambda lacks general exception handling
- **File:** `LogReader/LogReader.App/ViewModels/LogTabViewModel.cs` lines 906, 933
- **What:** `OnLinesAppended` dispatches an async lambda via `BeginInvoke`. The inner lambda only catches `OperationCanceledException`. Other exceptions (e.g., `ArgumentOutOfRangeException` from H-2) propagate to the dispatcher unhandled.
- **Fix:** Add a general catch block inside the `BeginInvoke` lambda.

### M-15. `Dispose` blocks on `Task.WaitAll` — potential deadlock
- **File:** `LogReader/LogReader.App/ViewModels/MainViewModel.cs` lines 1650-1657
- **What:** `Task.Run(tab.Dispose)` then `Task.WaitAll`. Tab `Dispose` sets `IsSuspended`/`IsLoading` which raise `PropertyChanged`. If called from the UI thread, WPF marshaling back to the blocked UI thread deadlocks (mitigated by 5s timeout but tabs may not clean up).
- **Fix:** Call `BeginShutdown` on all tabs from the UI thread first, then only run non-UI cleanup on background threads.

### M-16. `ClearResults` during active tail search creates inconsistent state
- **File:** `LogReader/LogReader.App/ViewModels/SearchPanelViewModel.cs` lines 487-498
- **What:** Clearing results while a tail monitor is running allows `MergeResult` to immediately repopulate, with potential for negative `_totalHits` from interleaved reads across `await` boundaries.
- **Fix:** Cancel the active search session before clearing results.

### M-17. `NavigateToTimestampAsync` scans entire file without cancellation
- **File:** `LogReader/LogReader.App/ViewModels/MainViewModel.cs` lines 1184-1214
- **What:** Opens a `FileStream` and reads the entire file line-by-line with no cancellation support. For multi-GB files this blocks indefinitely. Also, `SelectedTab` could change during the scan.
- **Fix:** Accept a `CancellationToken`, and verify the tab is still selected after the scan.

## Low Severity

### L-1. Cancelled search returns partial results silently
- **File:** `LogReader/LogReader.Infrastructure/Services/SearchService.cs` line 72
- **What:** `OperationCanceledException` is caught and swallowed. The method returns whatever partial `SearchResult` has been accumulated so far with no indication results are incomplete.
- **Fix:** Set a `WasCancelled` flag on `SearchResult` or re-throw.

### L-2. `long` LineNumber silently truncated to `int`
- **File:** `LogReader/LogReader.Infrastructure/Services/SearchService.cs` line 35; `LogReader/LogReader.App/ViewModels/FilterPanelViewModel.cs` line 98
- **What:** `SearchHit.LineNumber` is `long` but cast to `int` in `FilterPanelViewModel`. Files with more than ~2.1 billion lines would overflow silently.
- **Fix:** Standardize on `int` throughout (the de facto limit everywhere else) or handle the cast safely.

### L-3. Error path skips `Cts.Cancel()`
- **File:** `LogReader/LogReader.Infrastructure/Services/FileTailService.cs` lines 162-178
- **What:** When an unhandled exception occurs in `TailLoopAsync`, the state is removed from `_tailedFiles` but `CancelState(state)` is never called, leaving registered cancellation callbacks untriggered.
- **Fix:** Call `state.Cts.Cancel()` in the `finally` block.

### L-4. `Dispose` swallows all exceptions including non-cancellation
- **File:** `LogReader/LogReader.Infrastructure/Services/FileTailService.cs` lines 78-84
- **What:** `Task.WaitAll` exceptions are silently swallowed, including unexpected non-cancellation errors.
- **Fix:** Filter `AggregateException` to only swallow `OperationCanceledException` / `TaskCanceledException`.

### L-5. Double file handle during truncation rebuild
- **File:** `LogReader/LogReader.Infrastructure/Services/ChunkedLogReaderService.cs` lines 85, 91-92
- **What:** When truncation is detected, the original stream stays open while `BuildIndexAsync` opens a second stream to the same file. Wastes a file descriptor and could block rotation tools that need exclusive access.
- **Fix:** Close the initial stream before calling `BuildIndexAsync`.

### L-6. `TrimTrailingEmptyLine` fragility with newline-only appends
- **File:** `LogReader/LogReader.Infrastructure/Services/ChunkedLogReaderService.cs` lines 130, 153
- **What:** When appended data consists solely of newline characters, the pre-seeded offset and the scanned offset may conflict, and `TrimTrailingEmptyLine` could remove a legitimate offset.
- **Fix:** Only run `TrimTrailingEmptyLine` on scan-loop-added offsets, not pre-seeded ones.

### L-7. `_lineIndexLock` SemaphoreSlim never disposed
- **File:** `LogReader/LogReader.App/ViewModels/LogTabViewModel.cs` line 27
- **What:** The semaphore holds a kernel handle when `WaitAsync` is used with cancellation tokens. `Dispose()` disposes other resources but not the semaphore.
- **Fix:** Dispose `_lineIndexLock` at the end of the `Dispose` method.

### L-8. `OnEncodingChanged` fires duplicate `LoadAsync` during initialization
- **File:** `LogReader/LogReader.App/ViewModels/LogTabViewModel.cs` lines 488-501
- **What:** Setting `tab.Encoding` before `tab.LoadAsync()` in `OpenFileInternalAsync` triggers `OnEncodingChanged` → `_ = LoadAsync()`, then the caller also calls `LoadAsync()`. Two concurrent loads run; the first gets cancelled by `_loadCts` but wastes work.
- **Fix:** Set encoding via constructor parameter, or use a flag to suppress `OnEncodingChanged` during initialization.

### L-9. `DisposeLineIndexAsync` acquires lock without timeout
- **File:** `LogReader/LogReader.App/ViewModels/LogTabViewModel.cs` line 1148
- **What:** `_lineIndexLock.WaitAsync()` has no cancellation token or timeout. If another operation is holding the semaphore, this wait hangs indefinitely. The outer 2-second timeout mitigates actual deadlock but leaves an abandoned background task.
- **Fix:** Pass a `CancellationToken` with a timeout.

### L-10. `OnFileRotated` clears filter without notifying `FilterPanelViewModel`
- **File:** `LogReader/LogReader.App/ViewModels/LogTabViewModel.cs` lines 969-1001
- **What:** File rotation clears `_snapshotFilteredLineNumbers` and related state, but `FilterPanelViewModel` is not notified. The UI still shows stale "Filter active: N matching lines" text.
- **Fix:** Raise a notification or call `FilterPanel.OnSelectedTabChanged(tab)` after rotation completes.

### L-11. `MonitorTailAsync` holds strong reference to disposed/closed tabs
- **File:** `LogReader/LogReader.App/ViewModels/SearchPanelViewModel.cs` lines 286-336
- **What:** `TailSearchTracker` holds a strong reference to `LogTabViewModel`. If the tab is closed during monitoring, properties are read from a disposed VM.
- **Fix:** Check `tracker.Tab.IsShuttingDown` or verify tab is still in `_mainVm.Tabs` before processing.

### L-12. `BuildSearchTargets` TOCTOU on `SelectedTab` null check
- **File:** `LogReader/LogReader.App/ViewModels/SearchPanelViewModel.cs` lines 338-364
- **What:** Checks `_mainVm.SelectedTab == null` then accesses `_mainVm.SelectedTab.FilePath` without capturing to a local. Safe on the UI thread today but fragile if calling conventions change.
- **Fix:** Capture `_mainVm.SelectedTab` into a local variable before the null check.

### L-13. `RunTabLifecycleMaintenance` triggers cascading `CollectionChanged` events
- **File:** `LogReader/LogReader.App/ViewModels/MainViewModel.cs` lines 1609-1639
- **What:** Each `Tabs.Remove(tab)` fires `CollectionChanged` → `RefreshAllMemberFilesAsync()`. For N purged tabs, N cascading async operations fire.
- **Fix:** Use the existing `BeginTabCollectionNotificationSuppression` / `EndTabCollectionNotificationSuppression` around the purge loop.

### L-14. `UpdateAsync` silently no-ops when ID not found
- **File:** `LogReader/LogReader.Infrastructure/Repositories/JsonLogFileRepository.cs` lines 42-53
- **What:** If `entry.Id` doesn't match any existing entry, `FindIndex` returns -1, the update is skipped, but `SaveAsync` is still called on unchanged data. No error or indication to the caller.
- **Fix:** Throw an exception or return a boolean indicating whether the update was applied.

### L-15. Non-reentrant `SemaphoreSlim` pattern invites future deadlock
- **File:** `LogReader/LogReader.Infrastructure/Repositories/JsonLogGroupRepository.cs` lines 18-48
- **What:** `GetAllAsync()` acquires `_lock`. `GetByIdAsync()` calls `GetAllAsync()`, which also acquires `_lock`. Currently safe because `GetByIdAsync` is only called externally, but a future method calling `GetByIdAsync` while holding `_lock` would deadlock.
- **Fix:** Extract an internal `LoadAllUnsafe()` that does not acquire the lock.

### L-16. `.tmp` file left behind if `File.Move` fails
- **File:** `LogReader/LogReader.Infrastructure/Repositories/JsonStore.cs` lines 38-47
- **What:** `SaveAsync` writes to `.tmp` then does `File.Move`. If `File.Move` throws (locked target file), the `.tmp` file is orphaned until the next save.
- **Fix:** Wrap `File.Move` in try/finally that deletes the temp file on failure.

### L-17. Hard cast of `FileDrop` data
- **File:** `LogReader/LogReader.App/Views/MainWindow.xaml.cs` line 217
- **What:** `(string[])e.Data.GetData(DataFormats.FileDrop)!` performs a hard cast. Non-standard drag sources may provide data in a different type even though `GetDataPresent` returns true.
- **Fix:** Use `as string[]` and null-check before iterating.

### L-18. Fire-and-forget `RunStartupAsync`
- **File:** `LogReader/LogReader.App/App.xaml.cs` line 36
- **What:** `_ = RunStartupAsync()` discards the startup task. If an exception occurs outside the try/catch, it becomes an unobserved task exception.
- **Fix:** Attach a continuation to log faulted tasks, or use `Dispatcher.InvokeAsync`.

### L-19. `SaveSessionAsync` enumerates `Tabs` without snapshot
- **File:** `LogReader/LogReader.App/ViewModels/MainViewModel.cs` lines 173-188
- **What:** `Tabs.Select(...)` iterates `ObservableCollection` during shutdown. If `Dispose` modifies `Tabs` concurrently, `InvalidOperationException` is thrown.
- **Fix:** Snapshot `Tabs` with `.ToList()` at the start of `SaveSessionAsync`.

## Suggested Fix Priority
1. **H-1 + H-3** — Highest impact. Unprotected events kill file tailing silently; corrupt JSON prevents app startup. Both are straightforward fixes.
2. **H-2** — Filtered viewport race condition. Switch to immutable list pattern.
3. **M-1, M-2** — Search correctness bugs (regex precedence, timeout handling). Both are one-line fixes.
4. **M-8, M-9** — Clipboard and null-forgiving crashes. Simple defensive wrapping.
5. **M-3, M-4** — Tail service lifecycle races. Require modest refactoring.
6. Remaining medium items by judgment.

