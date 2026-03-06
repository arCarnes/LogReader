# Dashboard Refactor Test Rewrite Plan

## Objective
Rewrite tests for the dashboard-only model with strict branch/leaf rules and single active dashboard filtering.

## New Domain Rules (Test Contract)
- Branch nodes are organizational only.
- Branch nodes can have children and cannot contain files.
- Dashboard nodes are leaf nodes.
- Dashboard nodes can contain files and cannot have children.
- Exactly one dashboard can be active at a time.
- With no active dashboard, all tabs are visible.

## Scope
The refactor impacts these test files:
- `LogReader.Tests/MainViewModelTests.cs`
- `LogReader.Tests/NestedGroupTests.cs`
- `LogReader.Tests/GroupPersistenceTests.cs`

These test files should remain unchanged unless breakage appears during compile:
- `LogReader.Tests/NavigationTests.cs`
- `LogReader.Tests/LogTabViewModelLoadTests.cs`
- `LogReader.Tests/LineIndexTests.cs`
- `LogReader.Tests/LineIndexEncodingTests.cs`
- `LogReader.Tests/LineHighlighterTests.cs`
- `LogReader.Tests/JsonSettingsRepositoryTests.cs`
- `LogReader.Tests/RotationDetectionTests.cs`
- `LogReader.Tests/SearchServiceTests.cs`

## Rewrite Map

### MainViewModelTests.cs
Keep with minimal/no changes:
- `OpenFilePathAsync_DeduplicatesByPath`
- `OpenFilePathAsync_CaseInsensitiveDedupe`
- `CloseTab_DisposesAndRemovesTab`
- `CloseAllTabs_ClearsAllTabs`
- `CloseOtherTabs_KeepsOnlySpecifiedTab`
- `CloseAllButPinned_KeepsPinnedTabs`
- `TogglePinTab_TogglesIsPinned`
- `FilteredTabs_SortsPinnedFirst`
- `FilteredTabs_PinnedOrder_RemainsStableAcrossSelectionChanges`
- `TogglePinTab_RePinningTab_UpdatesPinnedOrderDeterministically`
- `SessionPersistence_PreservesIsPinned`
- `LifecycleMaintenance_PurgesOldHiddenTabs_ButKeepsPinned`
- `LifecycleMaintenance_Purge_UpdatesSavedSessionState`
- `GlobalAutoTailSettingChange_IsAppliedToVisibleTabs`
- `Dispose_CanBeCalledMultipleTimes_WhenLifecycleTimerEnabled`
- `PaneState_DefaultsToBothOpen`
- `ToggleFocusMode_TogglesBothPanes`
- `RememberPanelWidths_IgnoresSmallValues`

Replace/remove (group selection semantics changed):
- Replace `FilteredTabs_ReturnsAllWhenNoGroupSelected` with `FilteredTabs_ReturnsAllWhenNoDashboardActive`.
- Replace `FilteredTabs_FiltersWhenGroupSelected` with `FilteredTabs_FiltersByActiveDashboard`.
- Remove `ToggleGroupSelection_SingleSelect_ClearsOthers`.
- Remove `ToggleGroupSelection_MultiSelect_PreservesOthers`.
- Replace `OpenGroupFilesAsync_SkipsMissingFiles` with `OpenDashboardFilesAsync_SkipsMissingFiles`.
- Replace `GroupFilter_HidesTabs_StopsTailingForHiddenTabs` with `DashboardFilter_HidesTabs_StopsTailingForHiddenTabs`.
- Replace visibility resume tests so transitions are driven by active dashboard changes instead of group selection API.

### NestedGroupTests.cs
Do not patch incrementally. Replace this file entirely with dashboard-tree tests.

Drop old invariants:
- `Neutral/FileSet/Container` kind behavior.
- Mixed node behavior (files + children on same node).
- Multi-select union filtering.
- Legacy migration/default kind expectations.

Retain as rewritten equivalents:
- Tree depth-first ordering.
- Sibling-scoped reorder rules.
- Reorder persistence across reload.
- Delete subtree behavior.
- Malformed topology safety (orphan/cycle).
- Recursive file resolution under organizational tree.

Add new invariants:
- `Branch_CannotManageFiles`.
- `Dashboard_CannotAddChild`.
- `Branch_CanAddChild`.
- `Dashboard_CanManageFiles`.
- `ActivateDashboard_DeactivatesPreviousDashboard`.
- `DeleteActiveDashboard_ClearsActiveDashboard`.

### GroupPersistenceTests.cs
Rename conceptually to dashboard persistence but keep storage/repository focus.

Replace:
- `ExportImport_RoundTrip` -> `ExportImport_Dashboard_RoundTrip`.
- `Export_Container_FlattensDescendantFilePaths` -> `Export_Branch_FlattensDescendantDashboardFilePaths`.

Keep with wording updates:
- `ImportGroup_NonExistentFile_ReturnsNull`.
- `LogGroup_ManyToMany_MultipleGroups` (rename to dashboard terminology).
- `GroupExport_HasCorrectDefaults` (rename type/label if model changes).

## New Target Test Layout
After refactor, prefer these files:
- `LogReader.Tests/MainViewModelTests.cs` (tab lifecycle + dashboard activation behavior)
- `LogReader.Tests/DashboardTreeTests.cs` (tree and branch/leaf invariants)
- `LogReader.Tests/DashboardPersistenceTests.cs` (repo import/export and recursive resolution)

## Preparation Steps Before Main Refactor
1. Keep existing stubs but move reusable stubs to `Stubs.cs` if duplicated in `MainViewModelTests.cs` and `NestedGroupTests.cs`.
2. Introduce a shared test helper for creating tree nodes (`CreateBranch`, `CreateDashboard`) once model types are renamed.
3. Remove dependencies on `ToggleGroupSelection` from tests first, then update production API names.
4. Rewrite tests in small commits:
   - Commit A: introduce new test files with passing baseline for unchanged behavior.
   - Commit B: replace `NestedGroupTests` with dashboard tree tests.
   - Commit C: update persistence tests.
   - Commit D: remove obsolete group-only tests and dead helper code.

## Validation Commands
Run both frameworks used by this repository:
- `dotnet test LogReader.Tests\LogReader.Tests.csproj --framework net8.0`
- `dotnet test LogReader.Tests\LogReader.Tests.csproj --framework net8.0-windows`

## Definition of Done
- No tests assert legacy `Neutral/FileSet/Container` semantics.
- No tests assert multi-select group filtering behavior.
- New tests enforce branch/leaf restrictions and single active dashboard behavior.
- Test suite passes in `net8.0` and `net8.0-windows`.
